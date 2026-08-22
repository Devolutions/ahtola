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
