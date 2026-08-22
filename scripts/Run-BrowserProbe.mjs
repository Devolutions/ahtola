import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { spawn, spawnSync } from "node:child_process";

const targetUrl = process.argv[2];
const expectedPrefix = process.argv[3] ?? "PASS:";
const timeoutMilliseconds = Number(process.argv[4] ?? 90_000);
if (!targetUrl)
    throw new Error("Usage: node Run-BrowserProbe.mjs <url> [expected-prefix] [timeout-ms]");

function commandPath(name) {
    const command = process.platform === "win32" ? "where.exe" : "which";
    const result = spawnSync(command, [name], { encoding: "utf8" });
    return result.status === 0
        ? result.stdout.split(/\r?\n/u).find(Boolean)
        : undefined;
}

function findBrowser() {
    const explicit = process.env.AHTOLA_BROWSER_EXECUTABLE;
    const candidates = [
        explicit,
        process.platform === "win32"
            ? path.join(process.env["ProgramFiles"] ?? "", "Google/Chrome/Application/chrome.exe")
            : undefined,
        process.platform === "win32"
            ? path.join(process.env["ProgramFiles(x86)"] ?? "", "Microsoft/Edge/Application/msedge.exe")
            : undefined,
        process.platform === "darwin"
            ? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
            : undefined,
        process.platform === "darwin"
            ? "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
            : undefined,
        commandPath("google-chrome"),
        commandPath("chromium"),
        commandPath("chromium-browser"),
        commandPath("microsoft-edge"),
    ].filter(Boolean);

    const browser = candidates.find(candidate => fs.existsSync(candidate));
    if (!browser)
        throw new Error("No Chromium browser was found. Set AHTOLA_BROWSER_EXECUTABLE.");
    return browser;
}

async function reservePort() {
    const server = net.createServer();
    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", resolve);
    });
    const address = server.address();
    await new Promise(resolve => server.close(resolve));
    return address.port;
}

function delay(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function waitForExit(childProcess, timeoutMilliseconds) {
    if (childProcess.exitCode !== null)
        return Promise.resolve();
    return Promise.race([
        new Promise(resolve => childProcess.once("exit", resolve)),
        delay(timeoutMilliseconds),
    ]);
}

async function findPage(port, deadline) {
    while (Date.now() < deadline) {
        try {
            const response = await fetch(`http://127.0.0.1:${port}/json/list`);
            if (response.ok) {
                const pages = await response.json();
                const page = pages.find(candidate =>
                    candidate.type === "page"
                    && candidate.url.startsWith(targetUrl));
                if (page)
                    return page;
            }
        } catch {
        }
        await delay(100);
    }
    throw new Error("Chromium did not expose the browser test page before timeout.");
}

class DevToolsClient {
    constructor(url) {
        this.socket = new WebSocket(url);
        this.nextId = 0;
        this.pending = new Map();
    }

    async open() {
        await new Promise((resolve, reject) => {
            this.socket.addEventListener("open", resolve, { once: true });
            this.socket.addEventListener("error", reject, { once: true });
        });
        this.socket.addEventListener("message", event => {
            const message = JSON.parse(event.data);
            if (!message.id)
                return;
            const pending = this.pending.get(message.id);
            if (!pending)
                return;
            this.pending.delete(message.id);
            if (message.error)
                pending.reject(new Error(message.error.message));
            else
                pending.resolve(message.result);
        });
    }

    send(method, params = {}) {
        const id = ++this.nextId;
        return new Promise((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            this.socket.send(JSON.stringify({ id, method, params }));
        });
    }

    close() {
        this.socket.close();
    }
}

const browser = findBrowser();
const debuggingPort = await reservePort();
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "ahtola-browser-"));
const output = [];
const child = spawn(
    browser,
    [
        "--headless=new",
        "--disable-gpu",
        "--no-first-run",
        "--no-default-browser-check",
        "--no-sandbox",
        `--remote-debugging-port=${debuggingPort}`,
        `--user-data-dir=${profile}`,
        targetUrl,
    ],
    { stdio: ["ignore", "ignore", "pipe"] });
child.stderr.on("data", data => output.push(data.toString()));

let client;
try {
    const deadline = Date.now() + timeoutMilliseconds;
    const page = await findPage(debuggingPort, deadline);
    client = new DevToolsClient(page.webSocketDebuggerUrl);
    await client.open();
    await client.send("Runtime.enable");

    while (Date.now() < deadline) {
        const evaluation = await client.send("Runtime.evaluate", {
            expression: "document.querySelector('#browser-test-status')?.textContent ?? ''",
            returnByValue: true,
        });
        const status = evaluation.result?.value ?? "";
        if (status.startsWith(expectedPrefix)) {
            console.log(status);
            break;
        }
        if (status.startsWith("FAIL:"))
            throw new Error(status);
        await delay(100);
    }

    if (Date.now() >= deadline)
        throw new Error("Browser package consumer did not finish before timeout.");
} catch (error) {
    if (output.length > 0)
        console.error(output.join(""));
    throw error;
} finally {
    client?.close();
    if (child.exitCode === null)
        child.kill();
    await waitForExit(child, 5_000);

    let cleanupError;
    for (let attempt = 0; attempt < 20; attempt++) {
        try {
            fs.rmSync(profile, { recursive: true, force: true });
            cleanupError = undefined;
            break;
        } catch (error) {
            cleanupError = error;
            await delay(100);
        }
    }
    if (cleanupError)
        console.warn(`Could not remove Chromium profile '${profile}': ${cleanupError.message}`);
}
