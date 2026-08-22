import { test } from "node:test";
import assert from "node:assert/strict";

const opfsModuleUrl = new URL(
    "../../../src/Ahtola.Data.Sqlite.Browser/wwwroot/ahtola-opfs.mjs",
    import.meta.url
).href;

// hasSynchronousAccessHandlePrerequisites() (a fast-path guard inside
// evaluateSynchronousAccessHandleSupport) checks `typeof Worker` and
// `navigator.storage?.getDirectory`. Importing ahtola-opfs.mjs itself never
// touches these at module-evaluation time (only when a function that reads
// them is called), so a single shared navigator/Worker stub is enough for
// every test in this file - no per-test module reload is required here,
// unlike the OPFS worker tests.
Object.defineProperty(globalThis, "navigator", {
    value: { storage: { getDirectory: async () => ({}) } },
    configurable: true,
    writable: true,
});
if (typeof globalThis.Worker === "undefined") {
    Object.defineProperty(globalThis, "Worker", {
        value: class {},
        configurable: true,
        writable: true,
    });
}

const { evaluateSynchronousAccessHandleSupport } = await import(opfsModuleUrl);

function prober(...outcomes) {
    const calls = [];
    return {
        calls,
        run: async () => {
            calls.push(Date.now());
            return outcomes[Math.min(calls.length - 1, outcomes.length - 1)];
        },
    };
}

test("returns true immediately when the probe succeeds on the first attempt", async () => {
    const probe = prober({ ok: true });
    const result = await evaluateSynchronousAccessHandleSupport(probe.run, 3, 0);
    assert.equal(result, true);
    assert.equal(probe.calls.length, 1);
});

test("returns false without retrying for a non-transient error", async () => {
    const probe = prober({ ok: false, name: "NotSupportedError" });
    const result = await evaluateSynchronousAccessHandleSupport(probe.run, 3, 0);
    assert.equal(result, false);
    assert.equal(probe.calls.length, 1, "a definitive unsupported result must not be retried");
});

test("retries a transient error and succeeds once the handle becomes available", async () => {
    const probe = prober(
        { ok: false, name: "NoModificationAllowedError" },
        { ok: false, name: "NoModificationAllowedError" },
        { ok: true }
    );
    const result = await evaluateSynchronousAccessHandleSupport(probe.run, 3, 0);
    assert.equal(result, true);
    assert.equal(probe.calls.length, 3);
});

test("gives up and returns false after exhausting the retry budget on transient errors", async () => {
    const probe = prober({ ok: false, name: "InvalidStateError" });
    const result = await evaluateSynchronousAccessHandleSupport(probe.run, 3, 0);
    assert.equal(result, false);
    assert.equal(probe.calls.length, 3, "must attempt exactly the configured number of times, no more");
});

test("skips probing entirely when prerequisites are absent", async () => {
    const originalWorker = globalThis.Worker;
    Object.defineProperty(globalThis, "Worker", { value: undefined, configurable: true, writable: true });
    try {
        const probe = prober({ ok: true });
        const result = await evaluateSynchronousAccessHandleSupport(probe.run, 3, 0);
        assert.equal(result, false);
        assert.equal(probe.calls.length, 0, "must not spin up a worker when Worker itself is unavailable");
    } finally {
        Object.defineProperty(globalThis, "Worker", { value: originalWorker, configurable: true, writable: true });
    }
});

test("real probeSynchronousAccessHandleSupport uses the retry-policy default and reports false without Worker/OPFS", async () => {
    const { probeSynchronousAccessHandleSupport } = await import(opfsModuleUrl);
    const originalWorker = globalThis.Worker;
    Object.defineProperty(globalThis, "Worker", { value: undefined, configurable: true, writable: true });
    try {
        assert.equal(await probeSynchronousAccessHandleSupport(), false);
    } finally {
        Object.defineProperty(globalThis, "Worker", { value: originalWorker, configurable: true, writable: true });
    }
});

// Regression test: the synchronous-access-handle probe must load its worker
// from a real, packaged, same-origin script URL - exactly like the real
// storage worker does - and must never construct a Blob or call
// URL.createObjectURL. A Content-Security-Policy of `worker-src 'self'`
// (with no `blob:`) blocks blob: workers while still allowing the real,
// same-origin ahtola-opfs-worker.mjs; a blob-based probe would therefore
// report the browser as unsupported even though Ahtola OPFS storage itself
// works fine under that exact policy.
test("the real capability probe uses a same-origin packaged worker, never Blob/createObjectURL (CSP worker-src 'self' safety)", async () => {
    const { probeSynchronousAccessHandleSupport } = await import(opfsModuleUrl);

    const originalWorker = globalThis.Worker;
    const originalBlob = globalThis.Blob;
    const hadCreateObjectURL = Object.prototype.hasOwnProperty.call(URL, "createObjectURL");
    const originalCreateObjectURL = URL.createObjectURL;

    let capturedUrl;
    let capturedOptions;
    class FakeWorker {
        constructor(url, options) {
            capturedUrl = url;
            capturedOptions = options;
        }

        addEventListener(type, listener) {
            if (type === "message")
                this._onMessage = listener;
        }

        postMessage() {
            // Simulate the real probe worker reporting success.
            queueMicrotask(() => this._onMessage?.({ data: { ok: true } }));
        }

        terminate() {}
    }

    Object.defineProperty(globalThis, "Worker", { value: FakeWorker, configurable: true, writable: true });
    Object.defineProperty(globalThis, "Blob", {
        value: class {
            constructor() {
                throw new Error(
                    "Blob must never be constructed for the capability probe: a CSP of "
                    + "worker-src 'self' blocks blob: workers.");
            }
        },
        configurable: true,
        writable: true,
    });
    URL.createObjectURL = () => {
        throw new Error(
            "URL.createObjectURL must never be called for the capability probe: a CSP of "
            + "worker-src 'self' blocks blob: workers.");
    };

    try {
        const supported = await probeSynchronousAccessHandleSupport();
        assert.equal(supported, true);
        assert.ok(
            capturedUrl instanceof URL,
            "the probe worker must be constructed from a real URL, not an inline script string"
        );
        assert.notEqual(capturedUrl.protocol, "blob:");
        assert.ok(
            capturedUrl.pathname.endsWith("ahtola-opfs-capability-probe-worker.mjs"),
            `expected the packaged probe worker file, got '${capturedUrl.href}'`
        );
        assert.equal(capturedOptions?.type, "module");
    } finally {
        Object.defineProperty(globalThis, "Worker", { value: originalWorker, configurable: true, writable: true });
        Object.defineProperty(globalThis, "Blob", { value: originalBlob, configurable: true, writable: true });
        if (hadCreateObjectURL)
            URL.createObjectURL = originalCreateObjectURL;
        else
            delete URL.createObjectURL;
    }
});
