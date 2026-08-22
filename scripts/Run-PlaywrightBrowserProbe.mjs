import process from "node:process";
import { chromium, firefox, webkit } from "playwright";

const engineName = process.argv[2];
const targetUrl = process.argv[3];
const expectedPrefix = process.argv[4] ?? "PASS:";
const timeoutMilliseconds = Number(process.argv[5] ?? 90_000);
if (!engineName || !targetUrl) {
    throw new Error(
        "Usage: node Run-PlaywrightBrowserProbe.mjs <chromium|firefox|webkit> <url> "
        + "[expected-prefix] [timeout-ms]");
}

const engines = { chromium, firefox, webkit };
const engine = engines[engineName];
if (!engine)
    throw new Error(`Unsupported Playwright browser engine '${engineName}'.`);

const launchOptions = { headless: true };
if (process.env.AHTOLA_BROWSER_EXECUTABLE)
    launchOptions.executablePath = process.env.AHTOLA_BROWSER_EXECUTABLE;

const browser = await engine.launch(launchOptions);
try {
    const page = await browser.newPage();
    await page.goto(targetUrl, {
        waitUntil: "domcontentloaded",
        timeout: timeoutMilliseconds,
    });

    const deadline = Date.now() + timeoutMilliseconds;
    let passed = false;
    while (Date.now() < deadline) {
        const status = await page
            .locator("#browser-test-status")
            .textContent({ timeout: Math.min(5_000, timeoutMilliseconds) })
            .catch(() => "");
        if (status?.startsWith(expectedPrefix)) {
            console.log(status);
            passed = true;
            break;
        }
        if (status?.startsWith("FAIL:"))
            throw new Error(status);
        await page.waitForTimeout(100);
    }

    if (!passed)
        throw new Error(`${engineName} browser package consumer did not finish before timeout.`);
} finally {
    await browser.close();
}
