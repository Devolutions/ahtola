// Minimal in-memory fakes for the OPFS/Web Locks/Worker-adjacent globals
// that src/Ahtola.Data.Sqlite.Browser/wwwroot/ahtola-opfs-worker.mjs and
// ahtola-opfs.mjs rely on, so their logic can run under plain Node without a
// browser. Only the surface those two files actually touch is implemented.

export class FakeSyncAccessHandle {
    constructor(record) {
        this._record = record;
        this._closed = false;
    }

    getSize() {
        this._assertOpen();
        return this._record.bytes.length;
    }

    read(buffer, { at }) {
        this._assertOpen();
        const source = this._record.bytes;
        const count = Math.max(0, Math.min(buffer.length, source.length - at));
        buffer.set(source.subarray(at, at + count));
        return count;
    }

    write(buffer, { at }) {
        this._assertOpen();
        const end = at + buffer.length;
        if (end > this._record.bytes.length) {
            const grown = new Uint8Array(end);
            grown.set(this._record.bytes);
            this._record.bytes = grown;
        }
        this._record.bytes.set(buffer, at);
        return buffer.length;
    }

    truncate(newSize) {
        this._assertOpen();
        const next = new Uint8Array(newSize);
        next.set(this._record.bytes.subarray(0, Math.min(newSize, this._record.bytes.length)));
        this._record.bytes = next;
    }

    flush() {
        this._assertOpen();
    }

    close() {
        this._closed = true;
    }

    _assertOpen() {
        if (this._closed)
            throw new DOMException("The sync access handle is closed.", "InvalidStateError");
    }
}

export class FakeFileHandle {
    kind = "file";

    constructor(directory, name) {
        this._directory = directory;
        this.name = name;
    }

    async createSyncAccessHandle() {
        const record = this._directory._files.get(this.name);
        if (!record)
            throw new DOMException(`File '${this.name}' does not exist.`, "NotFoundError");
        return new FakeSyncAccessHandle(record);
    }
}

export class FakeDirectoryHandle {
    kind = "directory";

    constructor(name = "") {
        this.name = name;
        this._directories = new Map();
        this._files = new Map();
    }

    async getDirectoryHandle(name, { create = false } = {}) {
        if (!this._directories.has(name)) {
            if (!create)
                throw new DOMException(`Directory '${name}' does not exist.`, "NotFoundError");
            this._directories.set(name, new FakeDirectoryHandle(name));
        }
        return this._directories.get(name);
    }

    async getFileHandle(name, { create = false } = {}) {
        if (!this._files.has(name)) {
            if (!create)
                throw new DOMException(`File '${name}' does not exist.`, "NotFoundError");
            this._files.set(name, { bytes: new Uint8Array(0) });
        }
        return new FakeFileHandle(this, name);
    }

    async removeEntry(name) {
        if (this._files.delete(name))
            return;
        if (this._directories.delete(name))
            return;
        throw new DOMException(`Entry '${name}' does not exist.`, "NotFoundError");
    }

    /** Test-only helper: seed a file's raw bytes without going through the worker. */
    seedFile(name, bytes) {
        this._files.set(name, { bytes });
    }

    /** Test-only helper: whether a plain file entry exists directly in this directory. */
    hasFile(name) {
        return this._files.has(name);
    }

    async *entries() {
        for (const [name, directory] of this._directories)
            yield [name, directory];
        for (const [name] of this._files)
            yield [name, new FakeFileHandle(this, name)];
    }
}

/** A permissive stand-in for navigator.locks: exclusive per resource name. */
export class FakeLockManager {
    constructor() {
        this._held = new Map();
    }

    async request(name, options, callback) {
        if (this._held.has(name)) {
            if (options?.ifAvailable)
                return callback(null);
            throw new Error("FakeLockManager only supports ifAvailable:true in tests.");
        }

        const marker = {};
        this._held.set(name, marker);
        try {
            return await callback({ name });
        } finally {
            if (this._held.get(name) === marker)
                this._held.delete(name);
        }
    }
}

export function installFakeNavigator(root, lockManager = new FakeLockManager()) {
    Object.defineProperty(globalThis, "navigator", {
        value: {
            storage: { getDirectory: async () => root },
            locks: lockManager,
        },
        configurable: true,
        writable: true,
    });
}

/**
 * Loads a fresh instance of the OPFS worker module against the given fake
 * root, returning a `send(data)` helper that drives its single "message"
 * listener the same way the real Worker/postMessage bridge would.
 */
export async function loadWorkerModule(workerModuleUrl, root, lockManager) {
    installFakeNavigator(root, lockManager);

    let messageHandler;
    const posted = [];
    Object.defineProperty(globalThis, "self", {
        value: {
            addEventListener: (type, handler) => {
                if (type === "message")
                    messageHandler = handler;
            },
            postMessage: message => posted.push(message),
        },
        configurable: true,
        writable: true,
    });

    // Cache-bust so every test gets an independent module instance (fresh
    // top-level `let`/`const` state), matching one worker per open database.
    const instanceUrl = `${workerModuleUrl}?instance=${loadWorkerModule.nextInstanceId++}`;
    await import(instanceUrl);

    let nextMessageId = 0;
    return {
        posted,
        async send(data) {
            const id = ++nextMessageId;
            await messageHandler({ data: { ...data, id } });
            const response = posted.find(entry => entry.id === id);
            if (!response)
                throw new Error(`No response observed for message id ${id}.`);
            if (response.error) {
                const error = new Error(`${response.error.name}: ${response.error.message}`);
                error.name = response.error.name;
                error.code = response.error.code;
                throw error;
            }
            return response.result;
        },
    };
}
loadWorkerModule.nextInstanceId = 0;
