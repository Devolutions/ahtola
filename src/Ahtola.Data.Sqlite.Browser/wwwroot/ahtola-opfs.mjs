const contexts = new Map();
let nextContextId = 0;

function requiredFeatures() {
    return globalThis.crossOriginIsolated
        && typeof SharedArrayBuffer !== "undefined"
        && typeof Worker !== "undefined"
        && typeof navigator.storage?.getDirectory === "function"
        && typeof navigator.locks?.request === "function";
}

function context(contextId) {
    const value = contexts.get(contextId);
    if (!value)
        throw new Error(`Unknown Ahtola OPFS context '${contextId}'.`);
    return value;
}

function deserializeError(value) {
    const name = value?.name ?? "Error";
    const message = value?.message ?? "The Ahtola OPFS worker failed.";
    const error = new Error(`${name}: ${message}`);
    error.name = name;
    // .NET marshals Error.stack for promise rejections. Normalize it so error
    // names survive V8, SpiderMonkey, and JavaScriptCore stack differences.
    error.stack = `${name}: ${message}`;
    error.code = value?.code;
    return error;
}

function request(value, message) {
    const id = ++value.nextRequestId;
    return new Promise((resolve, reject) => {
        value.pending.set(id, { resolve, reject });
        value.worker.postMessage({ ...message, id });
    });
}

function enqueue(value, operation) {
    const result = value.queue.then(operation, operation);
    value.queue = result.catch(() => {});
    return result;
}

function failContext(value, error) {
    for (const pending of value.pending.values())
        pending.reject(error);
    value.pending.clear();
}

export function getCapabilityMask() {
    let mask = 0;
    if (globalThis.crossOriginIsolated)
        mask |= 1 << 0;
    if (typeof SharedArrayBuffer !== "undefined")
        mask |= 1 << 1;
    if (typeof navigator.storage?.getDirectory === "function")
        mask |= 1 << 2;
    // The synchronous handle method is worker-only and some browsers don't
    // expose FileSystemFileHandle's constructor on the window global.
    if (typeof Worker !== "undefined"
        && typeof navigator.storage?.getDirectory === "function") {
        mask |= 1 << 3;
    }
    if (typeof navigator.locks?.request === "function")
        mask |= 1 << 4;
    return mask;
}

export async function createContext(lockName, sharedBufferSize) {
    if (!requiredFeatures())
        throw new Error("Ahtola OPFS requires cross-origin isolation, SharedArrayBuffer, OPFS, module workers, and Web Locks.");
    if (!Number.isInteger(sharedBufferSize)
        || sharedBufferSize < 64 * 1024
        || sharedBufferSize > 64 * 1024 * 1024) {
        throw new RangeError("The Ahtola OPFS shared buffer must be between 64 KiB and 64 MiB.");
    }

    const worker = new Worker(new URL("./ahtola-opfs-worker.mjs", import.meta.url), {
        type: "module",
    });
    const shared = new SharedArrayBuffer(sharedBufferSize);
    const control = new Int32Array(
        new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 2));
    const value = {
        worker,
        shared,
        control,
        bytes: new Uint8Array(shared),
        pending: new Map(),
        nextRequestId: 0,
        nextOperationId: 0,
        queue: Promise.resolve(),
    };
    worker.addEventListener("message", event => {
        const pending = value.pending.get(event.data?.id);
        if (!pending)
            return;

        value.pending.delete(event.data.id);
        if (event.data.error)
            pending.reject(deserializeError(event.data.error));
        else
            pending.resolve(event.data.result);
    });
    worker.addEventListener("error", event => {
        failContext(value, new Error(event.message || "The Ahtola OPFS worker terminated."));
    });

    const contextId = ++nextContextId;
    contexts.set(contextId, value);
    try {
        await request(value, {
            type: "initialize",
            lockName,
            shared,
            control: control.buffer,
        });
        return contextId;
    } catch (error) {
        contexts.delete(contextId);
        worker.terminate();
        throw error;
    }
}

export async function disposeContext(contextId) {
    const value = contexts.get(contextId);
    if (!value)
        return;

    contexts.delete(contextId);
    try {
        await enqueue(value, () => request(value, { type: "dispose" }));
    } finally {
        value.worker.terminate();
        failContext(value, new Error("The Ahtola OPFS context was disposed."));
    }
}

export function fileExists(contextId, path) {
    const value = context(contextId);
    return enqueue(value, () => request(value, { type: "exists", path }));
}

export function openFile(contextId, path, mode, readOnly) {
    const value = context(contextId);
    return enqueue(value, () => request(value, {
        type: "open",
        path,
        mode,
        readOnly,
    }));
}

export function getLength(contextId, handleId) {
    const value = context(contextId);
    return enqueue(value, () => request(value, { type: "length", handleId }));
}

export function readFile(contextId, handleId, position, length) {
    const value = context(contextId);
    if (!Number.isSafeInteger(position) || position < 0)
        throw new RangeError("The OPFS read position is invalid.");
    if (!Number.isSafeInteger(length) || length < 0)
        throw new RangeError("The OPFS read length is invalid.");

    if (length > value.bytes.length)
        throw new RangeError("The OPFS read exceeds the shared buffer size.");
    return enqueue(value, () => request(value, {
        type: "read",
        handleId,
        position,
        length,
    }));
}

export function copyFromSharedBuffer(contextId, destination, length) {
    const value = context(contextId);
    if (!Number.isInteger(length)
        || length < 0
        || length > destination.byteLength
        || length > value.bytes.length) {
        throw new RangeError("The OPFS shared-buffer read length is invalid.");
    }
    try {
        destination.set(value.bytes.subarray(0, length));
        return length;
    } finally {
        destination.dispose();
    }
}

export function copyToSharedBuffer(contextId, source) {
    const value = context(contextId);
    if (source.byteLength > value.bytes.length)
        throw new RangeError("The OPFS shared-buffer write exceeds its capacity.");
    try {
        source.copyTo(value.bytes);
        return source.byteLength;
    } finally {
        source.dispose();
    }
}

export function writeFile(contextId, handleId, position, length) {
    const value = context(contextId);
    if (!Number.isSafeInteger(position) || position < 0)
        throw new RangeError("The OPFS write position is invalid.");
    if (!Number.isInteger(length) || length < 0 || length > value.bytes.length)
        throw new RangeError("The OPFS write length is invalid.");
    return enqueue(value, () => request(value, {
        type: "write",
        handleId,
        position,
        length,
    }));
}

export function setLength(contextId, handleId, length) {
    const value = context(contextId);
    if (!Number.isSafeInteger(length) || length < 0)
        throw new RangeError("The OPFS file length is invalid.");
    return enqueue(value, () => request(value, { type: "truncate", handleId, length }));
}

export function flushFile(contextId, handleId) {
    const value = context(contextId);
    return enqueue(value, () => request(value, { type: "flush", handleId }));
}

export function closeFile(contextId, handleId) {
    const value = context(contextId);
    return enqueue(value, () => request(value, { type: "close", handleId }));
}

export function deleteFile(contextId, path) {
    const value = context(contextId);
    return enqueue(value, () => request(value, { type: "delete", path }));
}

export function listFiles(contextId, directoryPath) {
    const value = context(contextId);
    return enqueue(value, () => request(value, {
        type: "list",
        directoryPath,
    }));
}

export function replaceFileAtomically(
    contextId,
    operationId,
    sourcePath,
    destinationPath,
    replaceEmptyDestination) {
    const value = context(contextId);
    return enqueue(value, async () => {
        Atomics.store(value.control, 0, operationId);
        try {
            return await request(value, {
                type: "replace",
                operationId,
                sourcePath,
                destinationPath,
                replaceEmptyDestination,
            });
        } finally {
            Atomics.compareExchange(value.control, 0, operationId, 0);
            Atomics.compareExchange(value.control, 1, operationId, 0);
        }
    });
}

export function allocateOperationId(contextId) {
    const value = context(contextId);
    value.nextOperationId = value.nextOperationId >= 0x7ffffffe
        ? 1
        : value.nextOperationId + 1;
    return value.nextOperationId;
}

export function cancelOperation(contextId, operationId) {
    const value = context(contextId);
    Atomics.store(value.control, 1, operationId);
}
