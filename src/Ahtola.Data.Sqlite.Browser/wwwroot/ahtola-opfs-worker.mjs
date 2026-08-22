let root;
let sharedBytes;
let control;
let lockRelease;
let lockRequest;
let nextHandleId = 0;
let intentGeneration = 0;
let currentLockName;

const entriesByPath = new Map();
const viewsByHandle = new Map();
const intentSlots = [];
const textEncoder = new TextEncoder();
const textDecoder = new TextDecoder();

// Filenames using this prefix are reserved for the worker's own atomic-
// replacement intent journal (see initializeIntentJournal). No public path
// operation may create, open, delete, or list a path whose last segment
// starts with it, so a real database, WAL, journal, or backup destination
// can never collide with an internal journal slot - which would let one
// database's reopen or replay hide or corrupt another database that merely
// happens to share the reserved prefix.
const RESERVED_PATH_PREFIX = ".ahtola-replace-";
// The exact filenames of the two live journal slots (see
// initializeIntentJournal/INTENT_SLOT_NAMES). Only these exact names are
// hidden from directory listings, so a reserved-prefixed name that isn't one
// of the two live slots is never silently swallowed by an over-broad filter.
const RESERVED_INTENT_SLOT_PATTERN = /^\.ahtola-replace-journal\.[01]$/u;

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
    if (segments.some(segment =>
        segment === ""
        || segment === "."
        || segment === ".."
        || /[\0\r\n]/u.test(segment)))
        throw new TypeError("OPFS paths cannot contain empty, current, or parent segments.");
    if (segments.some(segment => segment.startsWith(RESERVED_PATH_PREFIX))) {
        throw new DOMException(
            `OPFS path segments cannot start with the reserved prefix '${RESERVED_PATH_PREFIX}'.`,
            "NotAllowedError");
    }
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

async function resolveDirectory(path, create = false) {
    const segments = normalizePath(path);
    let directory = root;
    for (const segment of segments)
        directory = await directory.getDirectoryHandle(segment, { create });
    return directory;
}

async function listFiles(path) {
    const directory = await resolveDirectory(path, true);
    const files = [];
    async function visit(current, prefix) {
        for await (const [name, handle] of current.entries()) {
            const relative = `${prefix}/${name}`;
            if (handle.kind === "directory")
                await visit(handle, relative);
            else if (!RESERVED_INTENT_SLOT_PATTERN.test(name))
                files.push(relative);
        }
    }
    await visit(directory, path);
    files.sort();
    return files.join("\n");
}

function checksum(value) {
    const bytes = textEncoder.encode(value);
    let hash = 0x811c9dc5;
    for (const byte of bytes) {
        hash ^= byte;
        hash = Math.imul(hash, 0x01000193);
    }
    return hash >>> 0;
}

// Fixed slot filenames used inside the lock's OWN resolved directory (see
// initializeIntentJournal). Nesting the journal there - rather than hashing
// the lock name into a filename shared by every database at the OPFS root -
// makes two different locks sharing a slot structurally impossible instead
// of merely unlikely: normalizePath/resolveDirectory resolve distinct lock
// names to distinct directories, and the exclusive Web Lock already
// guarantees only one worker touches a given directory's slots at a time.
const INTENT_SLOT_NAMES = [".ahtola-replace-journal.0", ".ahtola-replace-journal.1"];

async function openIntentSlot(directory, name) {
    const file = await directory.getFileHandle(name, { create: true });
    return file.createSyncAccessHandle();
}

function readIntentSlot(access, expectedLockName) {
    const size = access.getSize();
    if (size === 0)
        return { empty: true };

    try {
        const bytes = new Uint8Array(size);
        if (access.read(bytes, { at: 0 }) !== size)
            return { invalid: true };
        const envelope = JSON.parse(textDecoder.decode(bytes));
        if (typeof envelope?.body !== "string"
            || !Number.isInteger(envelope.checksum)
            || checksum(envelope.body) !== envelope.checksum) {
            return { invalid: true };
        }

        const record = JSON.parse(envelope.body);
        if (record?.version !== 2
            || !Number.isSafeInteger(record.generation)
            || record.generation < 1
            || typeof record.lockName !== "string") {
            return { invalid: true };
        }
        // A well-formed record for a different lock identity means this
        // slot was somehow shared with another lock. That is not
        // corruption: leave the foreign record alone for its owner and
        // treat this lock's journal as empty. Kept as defense in depth on
        // top of the directory-scoped, per-lock slot location above.
        if (record.lockName !== expectedLockName)
            return { foreign: true };
        return { record };
    } catch {
        return { invalid: true };
    }
}

function writeIntent(payload) {
    const generation = intentGeneration + 1;
    const body = JSON.stringify({
        version: 2,
        generation,
        lockName: currentLockName,
        payload,
    });
    const bytes = textEncoder.encode(JSON.stringify({
        body,
        checksum: checksum(body),
    }));
    for (const access of intentSlots) {
        access.truncate(0);
        const written = access.write(bytes, { at: 0 });
        if (written !== bytes.length)
            throw new DOMException("The OPFS replacement intent was only partially written.", "DataError");
        access.truncate(bytes.length);
        access.flush();
    }
    intentGeneration = generation;
}

async function initializeIntentJournal(lockName) {
    currentLockName = lockName;
    const directory = await resolveDirectory(lockName, true);
    intentSlots.push(
        await openIntentSlot(directory, INTENT_SLOT_NAMES[0]),
        await openIntentSlot(directory, INTENT_SLOT_NAMES[1]));

    const decoded = intentSlots.map(access => readIntentSlot(access, lockName));
    const valid = decoded
        .filter(value => value.record)
        .map(value => value.record)
        .sort((left, right) => right.generation - left.generation);
    if (valid.length === 0 && decoded.some(value => value.invalid)) {
        throw new DOMException(
            "Both OPFS atomic-replacement intent records are invalid.",
            "DataError");
    }

    const latest = valid[0];
    intentGeneration = latest?.generation ?? 0;
    if (latest?.payload)
        await recoverReplacement(latest.payload);
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

async function getOrOpenEntry(path, create) {
    const normalized = normalizePath(path).join("/");
    let entry = entriesByPath.get(normalized);
    if (entry)
        return entry;

    const existing = await tryGetFile(normalized);
    if (!existing && !create)
        return undefined;

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
    return entry;
}

async function openFile(path, mode, readOnly) {
    if (!Number.isInteger(mode) || mode < 0 || mode > 2)
        throw new RangeError("The OPFS open mode is invalid.");

    const segments = normalizePath(path);
    const normalized = segments.join("/");
    const existing = entriesByPath.get(normalized) ?? await getOrOpenEntry(normalized, false);
    if (mode === 0 && !existing)
        throw new DOMException(`The OPFS file '${normalized}' does not exist.`, "NotFoundError");
    if (mode === 2 && existing)
        throw new DOMException(`The OPFS file '${normalized}' already exists.`, "InvalidModificationError");
    const entry = existing ?? await getOrOpenEntry(normalized, true);

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

function requireUnreferenced(entry) {
    if (entry.references !== 0) {
        throw new DOMException(
            `The OPFS file '${entry.path}' is still open.`,
            "InvalidStateError");
    }
}

async function copyEntry(source, destinationPath, operationId = 0) {
    requireUnreferenced(source);
    const destination = await getOrOpenEntry(destinationPath, true);
    requireUnreferenced(destination);
    const sourceSize = source.access.getSize();
    destination.access.truncate(0);

    const buffer = new Uint8Array(Math.min(sharedBytes.length, 1024 * 1024));
    let position = 0;
    while (position < sourceSize) {
        if (operationId !== 0 && Atomics.load(control, 1) === operationId)
            throw new DOMException("The OPFS operation was cancelled.", "AbortError");
        const count = Math.min(buffer.length, sourceSize - position);
        const target = buffer.subarray(0, count);
        const read = source.access.read(target, { at: position });
        if (read !== count) {
            throw new DOMException(
                `Atomic replacement read ${read} of ${count} bytes from '${source.path}'.`,
                "DataError");
        }
        const written = destination.access.write(target, { at: position });
        if (written !== count) {
            throw new DOMException(
                `Atomic replacement wrote ${written} of ${count} bytes to '${destination.path}'.`,
                "DataError");
        }
        position += count;
    }

    destination.access.truncate(sourceSize);
    destination.access.flush();
}

async function removeReplacementSource(path) {
    const normalized = normalizePath(path).join("/");
    const entry = entriesByPath.get(normalized);
    if (entry) {
        requireUnreferenced(entry);
        entry.access.close();
        entriesByPath.delete(normalized);
        await entry.directory.removeEntry(entry.name);
        return;
    }

    const existing = await tryGetFile(normalized);
    if (existing)
        await existing.directory.removeEntry(existing.name);
}

async function recoverReplacement(payload) {
    if (payload?.type !== "replace")
        throw new DOMException("The OPFS replacement intent type is invalid.", "DataError");

    if (payload.phase === "prepared") {
        const source = await getOrOpenEntry(payload.sourcePath, false);
        if (!source) {
            await rollbackPreparedReplacement(payload);
            return;
        }
        await copyEntry(source, payload.destinationPath);
        payload = { ...payload, phase: "destination-flushed" };
        writeIntent(payload);
    } else if (payload.phase !== "destination-flushed") {
        throw new DOMException("The OPFS replacement intent phase is invalid.", "DataError");
    }

    await removeReplacementSource(payload.sourcePath);
    writeIntent(null);
}

async function rollbackPreparedReplacement(payload) {
    if (payload.destinationExisted) {
        const destination = await getOrOpenEntry(payload.destinationPath, false);
        if (destination) {
            requireUnreferenced(destination);
            destination.access.truncate(0);
            destination.access.flush();
        }
    } else {
        await removeReplacementSource(payload.destinationPath);
    }
    writeIntent(null);
}

async function replaceFileAtomically(
    sourcePath,
    destinationPath,
    replaceEmptyDestination,
    operationId) {
    const sourceNormalized = normalizePath(sourcePath).join("/");
    const destinationNormalized = normalizePath(destinationPath).join("/");
    if (sourceNormalized === destinationNormalized)
        throw new DOMException("Atomic replacement requires distinct paths.", "InvalidModificationError");

    const source = await getOrOpenEntry(sourceNormalized, false);
    if (!source)
        throw new DOMException(`The OPFS file '${sourceNormalized}' does not exist.`, "NotFoundError");
    requireUnreferenced(source);

    const destination = await getOrOpenEntry(destinationNormalized, false);
    if (destination) {
        requireUnreferenced(destination);
        if (!replaceEmptyDestination || destination.access.getSize() !== 0) {
            throw new DOMException(
                `The OPFS destination '${destinationNormalized}' is not replaceable.`,
                "InvalidModificationError");
        }
    }

    source.access.flush();
    const intent = {
        type: "replace",
        sourcePath: sourceNormalized,
        destinationPath: destinationNormalized,
        destinationExisted: Boolean(destination),
        phase: "prepared",
    };
    writeIntent(intent);
    try {
        await copyEntry(source, destinationNormalized, operationId);
    } catch (error) {
        await rollbackPreparedReplacement(intent);
        throw error;
    }
    writeIntent({ ...intent, phase: "destination-flushed" });
    await removeReplacementSource(sourceNormalized);
    writeIntent(null);
}

async function dispose() {
    viewsByHandle.clear();
    for (const entry of entriesByPath.values())
        entry.access.close();
    entriesByPath.clear();
    for (const access of intentSlots)
        access.close();
    intentSlots.length = 0;

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
                control = new Int32Array(event.data.control);
                root = await navigator.storage.getDirectory();
                await acquireLock(event.data.lockName);
                await initializeIntentJournal(event.data.lockName);
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
            case "list":
                result = await listFiles(event.data.directoryPath);
                break;
            case "replace":
                await replaceFileAtomically(
                    event.data.sourcePath,
                    event.data.destinationPath,
                    event.data.replaceEmptyDestination,
                    event.data.operationId);
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
