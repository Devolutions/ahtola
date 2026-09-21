const retainedKeys = new Map();
let nextKeyHandle = 1;

function getSubtleCrypto() {
    const subtle = globalThis.crypto?.subtle;
    if (!subtle) {
        throw new Error("The browser does not provide the Web Crypto SubtleCrypto API.");
    }

    return subtle;
}

function retainKey(key) {
    const handle = nextKeyHandle++;
    retainedKeys.set(handle, key);
    return handle;
}

function getKey(handle) {
    const key = retainedKeys.get(handle);
    if (!key) {
        throw new Error(`Unknown or released Web Crypto key handle ${handle}.`);
    }

    return key;
}

function copyBytes(value, name) {
    if (value instanceof Uint8Array) {
        return value;
    }

    if (Array.isArray(value)) {
        return Uint8Array.from(value);
    }

    throw new TypeError(`${name} must be a byte array.`);
}

function clearBytes(original, bytes) {
    bytes.fill(0);
    if (original !== bytes && typeof original?.fill === "function") {
        original.fill(0);
    }
}

function normalizeError(error) {
    const name = error?.name ?? "Error";
    const message = error?.message ?? String(error);
    const normalized = new Error(`${name}: ${message}`);
    normalized.name = name;
    normalized.stack = `${name}: ${message}`;
    return normalized;
}

export async function importAesGcmKey(key) {
    const subtle = getSubtleCrypto();
    const keyBytes = copyBytes(key, "key");
    try {
        if (keyBytes.byteLength !== 16 && keyBytes.byteLength !== 32) {
            throw new Error("AHTLA AES-GCM keys must be exactly 16 or 32 bytes.");
        }

        const cryptoKey = await subtle.importKey(
            "raw",
            keyBytes,
            "AES-GCM",
            false,
            ["encrypt", "decrypt"]);
        return retainKey(cryptoKey);
    } finally {
        clearBytes(key, keyBytes);
    }
}

export async function encryptAesGcm(keyHandle, nonce, plaintext, associatedData) {
    const subtle = getSubtleCrypto();
    const nonceBytes = copyBytes(nonce, "nonce");
    const plaintextBytes = copyBytes(plaintext, "plaintext");
    const associatedDataBytes = copyBytes(associatedData, "associatedData");
    try {
        const encrypted = await subtle.encrypt(
            {
                name: "AES-GCM",
                iv: nonceBytes,
                additionalData: associatedDataBytes,
                tagLength: 128,
            },
            getKey(keyHandle),
            plaintextBytes);
        return new Uint8Array(encrypted);
    } finally {
        clearBytes(nonce, nonceBytes);
        clearBytes(plaintext, plaintextBytes);
        clearBytes(associatedData, associatedDataBytes);
    }
}

export async function decryptAesGcm(
    keyHandle,
    nonce,
    ciphertext,
    tag,
    associatedData) {
    const subtle = getSubtleCrypto();
    const nonceBytes = copyBytes(nonce, "nonce");
    const ciphertextBytes = copyBytes(ciphertext, "ciphertext");
    const tagBytes = copyBytes(tag, "tag");
    const associatedDataBytes = copyBytes(associatedData, "associatedData");
    const combined = new Uint8Array(ciphertextBytes.byteLength + tagBytes.byteLength);
    combined.set(ciphertextBytes);
    combined.set(tagBytes, ciphertextBytes.byteLength);
    try {
        try {
            const decrypted = await subtle.decrypt(
                {
                    name: "AES-GCM",
                    iv: nonceBytes,
                    additionalData: associatedDataBytes,
                    tagLength: 128,
                },
                getKey(keyHandle),
                combined);
            return new Uint8Array(decrypted);
        } catch (error) {
            throw normalizeError(error);
        }
    } finally {
        combined.fill(0);
        clearBytes(nonce, nonceBytes);
        clearBytes(ciphertext, ciphertextBytes);
        clearBytes(tag, tagBytes);
        clearBytes(associatedData, associatedDataBytes);
    }
}

export function consumeByteArray(value) {
    const bytes = copyBytes(value, "value");
    const result = Array.from(bytes);
    bytes.fill(0);
    queueMicrotask(() => result.fill(0));
    return result;
}

export function releaseKey(keyHandle) {
    if (!retainedKeys.delete(keyHandle)) {
        throw new Error(`Unknown or already released Web Crypto key handle ${keyHandle}.`);
    }
}

export function getRetainedKeyCount() {
    return retainedKeys.size;
}
