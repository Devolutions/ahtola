// A minimal, dedicated worker used only to probe real support for
// createSyncAccessHandle (see ahtola-opfs.mjs's
// runSynchronousAccessHandleProbeInWorker). Kept as its own packaged,
// same-origin script - not a blob: URL - so the probe is allowed under the
// same restrictive Content-Security-Policy (for example
// "worker-src 'self'", with no "blob:") that already allows the real
// storage worker (ahtola-opfs-worker.mjs) to load. A blob:-URL worker would
// be blocked by such a CSP even though the real worker works fine under it,
// making the probe report a false negative.
self.addEventListener("message", async () => {
    const name = `.ahtola-capability-probe-${crypto.randomUUID()}`;
    try {
        const root = await navigator.storage.getDirectory();
        const fileHandle = await root.getFileHandle(name, { create: true });
        try {
            const access = await fileHandle.createSyncAccessHandle();
            access.close();
        } finally {
            await root.removeEntry(name).catch(() => {});
        }
        self.postMessage({ ok: true });
    } catch (error) {
        self.postMessage({
            ok: false,
            name: error?.name ?? "Error",
            message: error?.message ?? String(error),
        });
    }
});
