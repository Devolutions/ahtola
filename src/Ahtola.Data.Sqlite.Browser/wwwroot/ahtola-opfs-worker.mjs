let root;
let sharedBytes;
let lockRelease;
let lockRequest;
let nextHandleId = 0;

const entriesByPath = new Map();
const viewsByHandle = new Map();

function errorPayload(error) {
    return {
        name: error?.name ?? "Error",
        message: error?.message ?? String(error),
        code: error?.code,
    };
}

function normalizePath(value) {
    if (typeof value !== "string" || value.trim() === "")
        throw new TypeError("An OPFS path is required.");

    const normalized = value.replaceAll("\\", "/");
    if (normalized.startsWith("/"))
        throw new TypeError("OPFS paths must be relative to the origin-private root.");

    const segments = normalized.split("/");
    if (segments.some(segment => segment === "" || segment === "." || segment === ".."))
        throw new TypeError("OPFS paths cannot contain empty, current, or parent segments.");
    return segments;
}

async function resolveParent(path, createDirectories) {
    const segments = normalizePath(path);
    const name = segments.pop();
    let directory = root;
    for (const segment of segments) {
        directory = await directory.getDirectoryHandle(segment, {
            create: createDirectories,
        });
    }
    return { directory, name, path: [...segments, name].join("/") };
}

async function tryGetFile(path) {
    try {
        const resolved = await resolveParent(path, false);
        return {
            ...resolved,
            file: await resolved.directory.getFileHandle(resolved.name),
        };
    } catch (error) {
        if (error?.name === "NotFoundError")
            return undefined;
        throw error;
    }
}

async function acquireLock(lockName) {
    let readyResolve;
    const ready = new Promise(resolve => readyResolve = resolve);
    const held = new Promise(resolve => lockRelease = resolve);
    lockRequest = navigator.locks.request(
        `ahtola:${lockName}`,
        { mode: "exclusive", ifAvailable: true },
        async lock => {
            readyResolve(Boolean(lock));
            if (lock)
                await held;
        });
    if (!await ready) {
        lockRelease = undefined;
        throw new DOMException(
            `The browser database '${lockName}' is open in another tab or worker.`,
            "NoModificationAllowedError");
    }
}

async function openFile(path, mode, readOnly) {
    if (!Number.isInteger(mode) || mode < 0 || mode > 2)
        throw new RangeError("The OPFS open mode is invalid.");

    const segments = normalizePath(path);
    const normalized = segments.join("/");
    let entry = entriesByPath.get(normalized);
    if (!entry) {
        const existing = await tryGetFile(normalized);
        if (mode === 0 && !existing)
            throw new DOMException(`The OPFS file '${normalized}' does not exist.`, "NotFoundError");
        if (mode === 2 && existing)
            throw new DOMException(`The OPFS file '${normalized}' already exists.`, "InvalidModificationError");

        const resolved = existing ?? await resolveParent(normalized, true);
        const file = existing?.file
            ?? await resolved.directory.getFileHandle(resolved.name, { create: true });
        const access = await file.createSyncAccessHandle();
        entry = {
            path: normalized,
            directory: resolved.directory,
            name: resolved.name,
            access,
            references: 0,
        };
        entriesByPath.set(normalized, entry);
    } else if (mode === 2) {
        throw new DOMException(`The OPFS file '${normalized}' already exists.`, "InvalidModificationError");
    }

    const handleId = ++nextHandleId;
    entry.references++;
    viewsByHandle.set(handleId, { entry, readOnly });
    return handleId;
}

function view(handleId) {
    const value = viewsByHandle.get(handleId);
    if (!value)
        throw new DOMException(`Unknown OPFS handle '${handleId}'.`, "InvalidStateError");
    return value;
}

function closeView(handleId) {
    const value = view(handleId);
    viewsByHandle.delete(handleId);
    value.entry.references--;
}

async function deleteFile(path) {
    const normalized = normalizePath(path).join("/");
    const entry = entriesByPath.get(normalized);
    if (entry) {
        if (entry.references !== 0)
            throw new DOMException(`The OPFS file '${normalized}' is still open.`, "InvalidStateError");
        entry.access.close();
        entriesByPath.delete(normalized);
        await entry.directory.removeEntry(entry.name);
        return;
    }

    const existing = await tryGetFile(normalized);
    if (existing)
        await existing.directory.removeEntry(existing.name);
}

async function dispose() {
    viewsByHandle.clear();
    for (const entry of entriesByPath.values())
        entry.access.close();
    entriesByPath.clear();

    lockRelease?.();
    lockRelease = undefined;
    await lockRequest;
    lockRequest = undefined;
}

self.addEventListener("message", async event => {
    const { id, type } = event.data;
    try {
        let result;
        switch (type) {
            case "initialize":
                if (root)
                    throw new DOMException("The OPFS worker is already initialized.", "InvalidStateError");
                sharedBytes = new Uint8Array(event.data.shared);
                root = await navigator.storage.getDirectory();
                await acquireLock(event.data.lockName);
                result = 0;
                break;
            case "exists":
                result = Boolean(await tryGetFile(event.data.path));
                break;
            case "open":
                result = await openFile(
                    event.data.path,
                    event.data.mode,
                    event.data.readOnly);
                break;
            case "length":
                result = view(event.data.handleId).entry.access.getSize();
                break;
            case "read": {
                const value = view(event.data.handleId);
                const destination = sharedBytes.subarray(0, event.data.length);
                result = value.entry.access.read(destination, { at: event.data.position });
                break;
            }
            case "write": {
                const value = view(event.data.handleId);
                if (value.readOnly)
                    throw new DOMException("The OPFS file is read-only.", "NoModificationAllowedError");
                const source = sharedBytes.subarray(0, event.data.length);
                result = value.entry.access.write(source, { at: event.data.position });
                break;
            }
            case "truncate": {
                const value = view(event.data.handleId);
                if (value.readOnly)
                    throw new DOMException("The OPFS file is read-only.", "NoModificationAllowedError");
                value.entry.access.truncate(event.data.length);
                result = 0;
                break;
            }
            case "flush": {
                const value = view(event.data.handleId);
                if (!value.readOnly)
                    value.entry.access.flush();
                result = 0;
                break;
            }
            case "close":
                closeView(event.data.handleId);
                result = 0;
                break;
            case "delete":
                await deleteFile(event.data.path);
                result = 0;
                break;
            case "dispose":
                await dispose();
                result = 0;
                break;
            default:
                throw new Error(`Unknown Ahtola OPFS worker operation '${type}'.`);
        }

        self.postMessage({ id, result });
    } catch (error) {
        self.postMessage({ id, error: errorPayload(error) });
    }
});
