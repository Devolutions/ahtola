import { test } from "node:test";
import assert from "node:assert/strict";
import { FakeDirectoryHandle, FakeLockManager, loadWorkerModule } from "./fake-opfs.mjs";

const workerModuleUrl = new URL(
    "../../../src/Ahtola.Data.Sqlite.Browser/wwwroot/ahtola-opfs-worker.mjs",
    import.meta.url
).href;

const SHARED_BYTES = new ArrayBuffer(64 * 1024);
const CONTROL_BUFFER = new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 2);

async function initializeWorker(root, lockName, lockManager) {
    const worker = await loadWorkerModule(workerModuleUrl, root, lockManager);
    await worker.send({
        type: "initialize",
        lockName,
        shared: SHARED_BYTES,
        control: CONTROL_BUFFER,
    });
    return worker;
}

// --- Finding 1: reserved ".ahtola-replace-" namespace boundary -------------

test("rejects opening a top-level path whose segment starts with the reserved prefix", async () => {
    const worker = await initializeWorker(new FakeDirectoryHandle(), "db-a");
    await assert.rejects(
        () => worker.send({ type: "open", path: ".ahtola-replace-not-a-slot", mode: 2, readOnly: false }),
        error => {
            assert.equal(error.name, "NotAllowedError");
            return true;
        }
    );
});

test("rejects a nested path whose final segment starts with the reserved prefix", async () => {
    const worker = await initializeWorker(new FakeDirectoryHandle(), "db-a");
    await assert.rejects(
        () => worker.send({
            type: "open",
            path: "app-data/.ahtola-replace-nested",
            mode: 2,
            readOnly: false,
        }),
        error => {
            assert.equal(error.name, "NotAllowedError");
            return true;
        }
    );
});

test("rejects exists/delete/list/replace operations on a reserved-prefixed path", async () => {
    const worker = await initializeWorker(new FakeDirectoryHandle(), "db-a");

    await assert.rejects(() => worker.send({ type: "exists", path: ".ahtola-replace-x" }));
    await assert.rejects(() => worker.send({ type: "delete", path: ".ahtola-replace-x" }));
    await assert.rejects(() => worker.send({ type: "list", directoryPath: ".ahtola-replace-x" }));
    await assert.rejects(() => worker.send({
        type: "replace",
        sourcePath: ".ahtola-replace-x",
        destinationPath: "app-data/dest.db",
        replaceEmptyDestination: false,
        operationId: 0,
    }));

    // A legitimate replacement source must still work; only the destination
    // side is reserved-prefixed here, proving both source and destination
    // are checked independently.
    await worker.send({ type: "open", path: "app-data/source.db", mode: 2, readOnly: false });
    await assert.rejects(() => worker.send({
        type: "replace",
        sourcePath: "app-data/source.db",
        destinationPath: ".ahtola-replace-y",
        replaceEmptyDestination: false,
        operationId: 0,
    }));
});

test("allows a real name that merely contains, but does not start with, the reserved prefix", async () => {
    const worker = await initializeWorker(new FakeDirectoryHandle(), "db-a");
    const handleId = await worker.send({
        type: "open",
        path: "app-data/not.ahtola-replace-mine.db",
        mode: 2,
        readOnly: false,
    });
    assert.equal(typeof handleId, "number");
    assert.equal(await worker.send({ type: "exists", path: "app-data/not.ahtola-replace-mine.db" }), true);
});

test("list hides only the exact reserved slot filename shape, not any reserved-prefixed name", async () => {
    const root = new FakeDirectoryHandle();
    const worker = await initializeWorker(root, "db-a");

    await worker.send({ type: "open", path: "app-data/app.db", mode: 2, readOnly: false });

    // Simulate a pre-existing file that merely starts with the reserved
    // prefix but does not match the exact <prefix><64-hex>.<0|1> slot shape
    // (for example left over from a different Ahtola version). Seeded
    // directly because the public "open" boundary now rejects creating one.
    const appData = await root.getDirectoryHandle("app-data");
    appData.seedFile(".ahtola-replace-legacy-name", new Uint8Array([1]));

    // A real slot-shaped name should still be hidden even if it ends up
    // inside a listed directory instead of at the true OPFS root.
    appData.seedFile(".ahtola-replace-journal.0", new Uint8Array([2]));

    const listing = await worker.send({ type: "list", directoryPath: "app-data" });
    const files = listing ? listing.split("\n") : [];
    assert.ok(files.includes("app-data/app.db"));
    assert.ok(files.includes("app-data/.ahtola-replace-legacy-name"));
    assert.ok(!files.includes("app-data/.ahtola-replace-journal.0"));
});

// --- Finding 2: intent-journal location/identity binding --------------------

function envelopeBytes(record) {
    const encoder = new TextEncoder();
    function checksum(value) {
        const bytes = encoder.encode(value);
        let hash = 0x811c9dc5;
        for (const byte of bytes) {
            hash ^= byte;
            hash = Math.imul(hash, 0x01000193);
        }
        return hash >>> 0;
    }
    const body = JSON.stringify(record);
    return encoder.encode(JSON.stringify({ body, checksum: checksum(body) }));
}

test("two different lock names never share journal slots, even under the same OPFS root", async () => {
    const root = new FakeDirectoryHandle();

    // Tenant A leaves an abandoned "prepared" atomic-replace intent (as if
    // it crashed mid-replace) inside its OWN directory.
    const tenantA = await root.getDirectoryHandle("tenant-a", { create: true });
    tenantA.seedFile("source.db", new Uint8Array([1, 2, 3, 4]));
    tenantA.seedFile(".ahtola-replace-journal.0", envelopeBytes({
        version: 2,
        generation: 1,
        lockName: "tenant-a",
        payload: {
            type: "replace",
            sourcePath: "tenant-a/source.db",
            destinationPath: "tenant-a/dest.db",
            destinationExisted: false,
            phase: "prepared",
        },
    }));
    tenantA.seedFile(".ahtola-replace-journal.1", new Uint8Array(0));

    // Tenant B has its own, entirely separate directory and journal slots;
    // nothing about its initialization can even see tenant A's slot files.
    const workerB = await initializeWorker(root, "tenant-b");
    assert.equal(tenantA.hasFile("source.db"), true, "tenant A's abandoned source must be untouched");
    assert.equal(tenantA.hasFile("dest.db"), false, "tenant A's intent must not have been recovered by tenant B");

    const sourceHandle = await workerB.send({ type: "open", path: "tenant-b/source.db", mode: 2, readOnly: false });
    await workerB.send({ type: "close", handleId: sourceHandle });
    await workerB.send({
        type: "replace",
        sourcePath: "tenant-b/source.db",
        destinationPath: "tenant-b/dest.db",
        replaceEmptyDestination: false,
        operationId: 0,
    });
    assert.equal(await workerB.send({ type: "exists", path: "tenant-b/dest.db" }), true);

    // Recovery preserved: reopening as tenant A still recovers its own
    // abandoned intent, unaffected by tenant B ever having existed.
    const workerA = await initializeWorker(root, "tenant-a");
    assert.equal(tenantA.hasFile("source.db"), false);
    assert.equal(await workerA.send({ type: "exists", path: "tenant-a/dest.db" }), true);
});

test("a well-formed record naming a different lock is isolated as defense in depth, not recovered", async () => {
    // Even though the directory-scoped slot location already makes this
    // scenario unreachable in practice, readIntentSlot's identity check must
    // still hold if a slot ever ends up with another lock's well-formed
    // record in it (for example a bug elsewhere, or a manually copied file).
    const root = new FakeDirectoryHandle();
    const directory = await root.getDirectoryHandle("app-data", { create: true });
    directory.seedFile("victim.db", new Uint8Array([9, 9]));
    directory.seedFile(".ahtola-replace-journal.0", envelopeBytes({
        version: 2,
        generation: 1,
        lockName: "some-other-lock",
        payload: {
            type: "replace",
            sourcePath: "app-data/victim.db",
            destinationPath: "app-data/clobbered.db",
            destinationExisted: false,
            phase: "prepared",
        },
    }));
    directory.seedFile(".ahtola-replace-journal.1", new Uint8Array(0));

    await initializeWorker(root, "app-data");

    assert.equal(directory.hasFile("victim.db"), true, "the foreign intent must not have been acted on");
    assert.equal(directory.hasFile("clobbered.db"), false);
});

test("both slots genuinely corrupted still fails initialization", async () => {
    const root = new FakeDirectoryHandle();
    const directory = await root.getDirectoryHandle("app-data", { create: true });
    directory.seedFile(".ahtola-replace-journal.0", new TextEncoder().encode("not json at all"));
    directory.seedFile(".ahtola-replace-journal.1", new TextEncoder().encode("also not json"));

    await assert.rejects(
        () => initializeWorker(root, "app-data"),
        error => {
            assert.equal(error.name, "DataError");
            return true;
        }
    );
});

