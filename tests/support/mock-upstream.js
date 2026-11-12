"use strict";

// Local stand-in for the OpenAI-compatible upstream used by the sandbox and extension tests.
// Replies are deterministic and derived from the prompt, so tests can assert exact output.
// POST .../translate is a DeepLX-style endpoint: { text, source_lang, target_lang } -> { code, data }.
// POST .../custom-api has a made-up shape for the custom template: { q: [...], to } with an X-Api-Key
// header -> { result: { items: [{ out }] } }.
const http = require("http");
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const FIXTURE_DIR = path.join(__dirname, "fixtures");

const VISION_SOURCE = "The scheduler retries failed jobs after a short exponential backoff.";
const VISION_TRANSLATION = "调度器会在短暂的指数退避后重试失败的任务。";

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function lastUserContent(body) {
  const messages = Array.isArray(body.messages) ? body.messages : [];
  const users = messages.filter((m) => m && m.role === "user");
  return users.length ? users[users.length - 1].content : "";
}

function classify(body) {
  const messages = Array.isArray(body.messages) ? body.messages : [];
  const content = lastUserContent(body);
  if (Array.isArray(content)) return "vision";
  if (messages.some((m) => m.role === "system" && /私教|language tutor/.test(String(m.content)))) return "study";
  if (/\u3016P0\u3017/.test(String(content))) return "immersive";
  return "translate";
}

function sourceText(kind, body) {
  const content = String(lastUserContent(body) || "");
  if (kind === "translate") {
    const constrained = /【源文】\n([\s\S]*?)\n\n【约束要求】/.exec(content);
    if (constrained) return constrained[1];
    const messages = Array.isArray(body.messages) ? body.messages : [];
    if (messages.some((m) => m.role === "system")) return content;
    return content.split("\n\n").slice(1).join("\n\n");
  }
  if (kind === "study") return content.slice(content.lastIndexOf("\n") + 1);
  if (kind === "immersive") {
    const block = /【源文】\n([\s\S]*?)\n\n【约束要求】/.exec(content);
    return block ? block[1] : content;
  }
  return "";
}

function translationOf(text) {
  return "译文：" + text;
}

function studyDeck(text) {
  const words = text.match(/[A-Za-z][A-Za-z'-]+/g) || [text];
  if (words.length <= 3) {
    return {
      mode: "word",
      sourceLang: "en-US",
      grammarNote: "",
      cards: [card(words[0])]
    };
  }
  const picked = [...new Set(words.map((w) => w.toLowerCase()))].sort((a, b) => b.length - a.length).slice(0, 3);
  return {
    mode: "sentence",
    sourceLang: "en-US",
    grammarNote: "主谓宾结构的陈述句。",
    cards: picked.map(card)
  };
}

function card(term) {
  return {
    term,
    surface: "",
    reading: "/" + term + "/",
    pos: "n.",
    register: "日常高频",
    meaning: term + " 的释义",
    nuance: "",
    memoryAid: "",
    collocations: [term + " together（一起）"],
    example: { source: "An example with " + term + ".", target: "含有 " + term + " 的例句。" }
  };
}

function replyFor(kind, body) {
  const src = sourceText(kind, body);
  if (kind === "vision") return "\u3016\u8bd1\u6587\u3017" + VISION_TRANSLATION + "\u3016\u539f\u6587\u3017" + VISION_SOURCE;
  if (kind === "study") return JSON.stringify(studyDeck(src));
  if (kind === "immersive") {
    const out = [];
    const re = /\u3016P(\d+)\u3017([^\n]*)/g;
    let m;
    while ((m = re.exec(src)) !== null) out.push("\u3016P" + m[1] + "\u3017" + translationOf(m[2].trim()));
    return out.join("\n");
  }
  return translationOf(src);
}

function splitChunks(text, count = 4) {
  const chars = Array.from(text);
  const size = Math.max(1, Math.ceil(chars.length / count));
  const out = [];
  for (let i = 0; i < chars.length; i += size) out.push(chars.slice(i, i + size).join(""));
  return out;
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buf) {
  let c = 0xffffffff;
  for (const byte of buf) c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const typed = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(typed));
  return Buffer.concat([len, typed, crc]);
}

const HUGE_IMAGE_BYTES = 32 * 1024 * 1024;

// Small RGB PNG with horizontal stripes, served as the right-click image fixture.
function samplePng(width = 64, height = 32) {
  const rows = [];
  for (let y = 0; y < height; y++) {
    const row = Buffer.alloc(1 + width * 3);
    const dark = Math.floor(y / 4) % 2 === 0;
    for (let x = 0; x < width; x++) row.fill(dark ? 0x22 : 0xee, 1 + x * 3, 4 + x * 3);
    rows.push(row);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 2;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    pngChunk("IHDR", ihdr),
    pngChunk("IDAT", zlib.deflateSync(Buffer.concat(rows))),
    pngChunk("IEND", Buffer.alloc(0))
  ]);
}

function pngSize(dataUrl) {
  const buf = Buffer.from(String(dataUrl).replace(/^data:image\/png;base64,/, ""), "base64");
  if (buf.length < 24 || buf.toString("ascii", 12, 16) !== "IHDR") return null;
  return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
}

async function startMockUpstream(options = {}) {
  const requests = [];
  const downloads = [];
  const overrides = [];
  const delayMs = options.delayMs ?? 25;
  const png = samplePng();

  async function stream(entry, res, text, hold) {
    res.writeHead(200, { "Content-Type": "text/event-stream; charset=utf-8", "Cache-Control": "no-cache" });
    const parts = splitChunks(text);
    for (let i = 0; i < parts.length; i++) {
      if (res.destroyed) return;
      res.write("data: " + JSON.stringify({ choices: [{ delta: { content: parts[i] } }] }) + "\n\n");
      if (i === 0 && hold) await hold;
      else await sleep(delayMs);
    }
    if (res.destroyed) return;
    res.write("data: [DONE]\n\n");
    res.end();
    entry.completed = true;
  }

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, "http://localhost");
    if (req.method === "GET" && url.pathname === "/sample.png") {
      res.writeHead(200, { "Content-Type": "image/png", "Cache-Control": "no-store" });
      res.end(png);
      return;
    }
    // A download far beyond the extension's 8 MB image limit. With ?declared=1 the size is announced
    // in Content-Length; without it the body is chunked and the size only shows while downloading.
    if (req.method === "GET" && url.pathname === "/huge.png") {
      const entry = { path: url.pathname + url.search, sent: 0, total: HUGE_IMAGE_BYTES, closedEarly: false };
      downloads.push(entry);
      const headers = { "Content-Type": "image/png", "Cache-Control": "no-store" };
      if (url.searchParams.get("declared")) headers["Content-Length"] = String(entry.total);
      res.writeHead(200, headers);
      res.on("close", () => {
        if (!res.writableEnded) entry.closedEarly = true;
      });
      const chunk = Buffer.alloc(64 * 1024, 0x5a);
      while (entry.sent < entry.total && !res.destroyed) {
        entry.sent += chunk.length;
        if (!res.write(chunk)) {
          await new Promise((resolve) => {
            const resume = () => {
              res.off("drain", resume);
              res.off("close", resume);
              resolve();
            };
            res.on("drain", resume);
            res.on("close", resume);
          });
        }
      }
      if (!res.destroyed) res.end();
      return;
    }
    if (req.method === "GET" && url.pathname.endsWith(".html")) {
      const file = path.join(FIXTURE_DIR, path.basename(url.pathname));
      if (fs.existsSync(file)) {
        res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
        res.end(fs.readFileSync(file));
        return;
      }
    }
    const isDeeplx = url.pathname.endsWith("/translate");
    const isCustom = url.pathname.endsWith("/custom-api");
    if (req.method !== "POST" || !(isDeeplx || isCustom || url.pathname.endsWith("/chat/completions"))) {
      res.writeHead(404, { "Content-Type": "text/plain" });
      res.end("not found");
      return;
    }

    let raw = "";
    for await (const chunk of req) raw += chunk;
    const body = JSON.parse(raw || "{}");
    const customItems = isCustom ? [].concat(body.q || []).map(String) : [];
    const kind = isDeeplx ? "deeplx" : isCustom ? "custom" : classify(body);
    const entry = {
      kind,
      path: url.pathname,
      token: url.searchParams.get("token") || "",
      body,
      source: isDeeplx ? String(body.text || "") : isCustom ? customItems.join("\n") : sourceText(kind, body),
      authorization: req.headers.authorization || "",
      apiKeyHeader: req.headers["x-api-key"] || "",
      aborted: false,
      completed: false
    };
    requests.push(entry);
    res.on("close", () => {
      if (!res.writableEnded) entry.aborted = true;
    });

    const idx = overrides.findIndex((o) => o.kind === kind && (!o.match || o.match(entry)));
    const override = idx >= 0 ? overrides.splice(idx, 1)[0] : null;

    if (override && override.status) {
      res.writeHead(override.status, { "Content-Type": override.contentType || "text/html; charset=utf-8" });
      res.end(override.body || "");
      return;
    }

    if (isCustom) {
      await sleep(delayMs);
      if (res.destroyed) return;
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ result: { items: customItems.map((q) => ({ out: translationOf(q) })) } }));
      entry.completed = true;
      return;
    }

    if (isDeeplx) {
      // Like a real DeepLX service, translate line by line and keep the line breaks.
      const data =
        override && typeof override.reply === "string"
          ? override.reply
          : entry.source.split("\n").map(translationOf).join("\n");
      await sleep(delayMs);
      if (res.destroyed) return;
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ code: 200, id: requests.length, data, source_lang: "EN", target_lang: body.target_lang }));
      entry.completed = true;
      return;
    }

    const text = override && typeof override.reply === "string" ? override.reply : replyFor(kind, body);
    if (body.stream === false) {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ choices: [{ message: { role: "assistant", content: text } }] }));
      entry.completed = true;
      return;
    }
    await stream(entry, res, text, override && override.hold);
  });

  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const origin = "http://127.0.0.1:" + server.address().port;

  return {
    origin,
    base: origin + "/v1",
    requests,
    // One entry per /huge.png download: bytes written so far and whether the client hung up early.
    downloads,
    // Applies to the next request of this kind (optionally filtered by match). Supports
    // { status, body } for HTTP errors, { reply } for custom text, and { hold } to pause the
    // stream after its first chunk until the promise resolves.
    override(kind, spec) {
      overrides.push({ kind, ...spec });
    },
    hold(kind, match) {
      let release;
      const hold = new Promise((resolve) => (release = resolve));
      overrides.push({ kind, match, hold });
      return release;
    },
    reset() {
      requests.length = 0;
      downloads.length = 0;
      overrides.length = 0;
    },
    close() {
      server.closeAllConnections();
      return new Promise((resolve) => server.close(resolve));
    }
  };
}

module.exports = {
  startMockUpstream,
  translationOf,
  studyDeck,
  pngSize,
  samplePng,
  VISION_SOURCE,
  VISION_TRANSLATION
};