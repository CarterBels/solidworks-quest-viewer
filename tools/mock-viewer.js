// Stand-in for the Quest viewer's HTTP server (same protocol as upstream ModelServer.cs), for testing the
// SolidWorks add-in without a headset.  node tools/mock-viewer.js [pairCode] [outDir]
// Type "send" + Enter to simulate the voice command "Marvin, send this to the Quest".
const http = require("http");
const fs = require("fs");
const path = require("path");

const PORT = 8723;
const CODE = (process.argv[2] || "TEST42").toUpperCase();
const OUT = process.argv[3] || ".";
let model = null, key = null;
let pending = [];
let waiters = [];

function json(res, status, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Content-Length": Buffer.byteLength(body), Connection: "close" });
  res.end(body);
}

function flush() {
  if (!pending.length) return;
  const cmds = pending; pending = [];
  waiters.splice(0).forEach((w) => { clearTimeout(w.t); json(w.res, 200, { commands: cmds }); });
}

http.createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    const body = Buffer.concat(chunks);
    if ((req.headers["x-pair-code"] || "").toUpperCase() !== CODE) {
      console.log(`401 ${req.method} ${req.url}`);
      return json(res, 401, { ok: false, pairing: true, message: "Wrong or missing pairing code." });
    }
    const url = req.url.split("?")[0];
    if (req.method === "GET" && url === "/status") return json(res, 200, { ok: true, model, key, loading: false });
    if (req.method === "POST" && url === "/notify") {
      console.log("notify", body.toString());
      return json(res, 200, { ok: true });
    }
    if (req.method === "GET" && url === "/commands") {
      const w = { res, t: setTimeout(() => { waiters = waiters.filter((x) => x !== w); json(res, 200, { commands: [] }); }, 20000) };
      waiters.push(w);
      return flush();
    }
    if (req.method === "POST" && url === "/model") {
      const name = decodeURIComponent(req.headers["x-model-name"] || "");
      const magic = body.slice(0, 4).toString();
      const info = req.headers["x-part-info"] ? Buffer.from(req.headers["x-part-info"], "base64").toString() : "";
      console.log(`model "${name}" ${body.length} bytes magic=${magic} hidden=${req.headers["x-hidden-parts"]} key=${req.headers["x-model-key"]}`);
      console.log(`  part info: ${info.slice(0, 300)}${info.length > 300 ? "…" : ""}`);
      if (magic !== "glTF") return json(res, 400, { ok: false, message: "Not a valid glTF." });
      fs.writeFileSync(path.join(OUT, "received.glb"), body);
      model = name; key = decodeURIComponent(req.headers["x-model-key"] || "");
      return json(res, 200, { ok: true, message: `${name}: received ${body.length} bytes` });
    }
    json(res, 404, { ok: false, message: "Use GET /status, GET /commands, POST /notify or POST /model." });
  });
}).listen(PORT, "127.0.0.1", () => console.log(`mock viewer on 127.0.0.1:${PORT}, pair code ${CODE}`));

process.stdin.on("data", (d) => {
  if (d.toString().trim() === "send") { pending.push("send"); console.log("queued voice 'send'"); flush(); }
});
