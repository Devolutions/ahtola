import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import process from "node:process";

const root = path.resolve(process.argv[2]);
const port = Number(process.argv[3] ?? 8123);
const contentTypes = new Map([
    [".css", "text/css"],
    [".dll", "application/octet-stream"],
    [".html", "text/html"],
    [".js", "text/javascript"],
    [".json", "application/json"],
    [".mjs", "text/javascript"],
    [".wasm", "application/wasm"],
]);

const server = http.createServer((request, response) => {
    const requestPath = decodeURIComponent(
        new URL(request.url, `http://${request.headers.host}`).pathname);
    let filePath = path.join(root, requestPath === "/" ? "index.html" : requestPath);
    if (!filePath.startsWith(root)
        || !fs.existsSync(filePath)
        || fs.statSync(filePath).isDirectory()) {
        filePath = path.join(root, "index.html");
    }

    response.setHeader("Cross-Origin-Opener-Policy", "same-origin");
    response.setHeader("Cross-Origin-Embedder-Policy", "require-corp");
    response.setHeader("Cross-Origin-Resource-Policy", "same-origin");
    response.setHeader("Cache-Control", "no-store");
    response.setHeader(
        "Content-Type",
        contentTypes.get(path.extname(filePath)) ?? "application/octet-stream");
    fs.createReadStream(filePath).pipe(response);
});

server.listen(port, "127.0.0.1", () => {
    console.log(`READY:http://127.0.0.1:${port}`);
});
