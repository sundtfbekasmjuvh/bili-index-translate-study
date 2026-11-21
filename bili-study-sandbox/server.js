// bili-study-sandbox/server.js
"use strict";

const http = require("http");
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");

const PORT = Number(process.env.PORT) || 3001;
const HOST = process.env.HOST || "127.0.0.1";
const EXT_DIR = path.resolve(__dirname, "../bili-index-translate");
const DEMO_HTML = path.join(__dirname, "index.html");
const EXT_FILES = {
  "/ext/content.css": { name: "content.css", type: "text/css; charset=utf-8" },
  "/ext/i18n.js": { name: "i18n.js", type: "application/javascript; charset=utf-8" },
  "/ext/content.js": { name: "content.js", type: "application/javascript; charset=utf-8" },
  "/ext/icons/icon32.png": { name: "icons/icon32.png", type: "image/png" }
};

const DEFAULT_BASE = "https://index-translate.bilibili.com/v1";
const DEFAULT_KEY = "bili";
const TIMEOUT_MS = 30000;

// Strictly volatile in-RAM session LRU micro-cache (wiped on server restart or purge)
const MEM_LIMIT = 64;
const translateMemCache = new Map();
const studyMemCache = new Map();
const visionMemCache = new Map();

function lruGet(map, k) {
  if (!map.has(k)) return null;
  const v = map.get(k);
  map.delete(k);
  map.set(k, v);
  return v;
}

function lruSet(map, k, v) {
  if (map.has(k)) map.delete(k);
  map.set(k, v);
  if (map.size > MEM_LIMIT) map.delete(map.keys().next().value);
}

// Hash the full glossary and image so entries of equal length never share a cache slot.
function sha256Hex(str) {
  return crypto.createHash("sha256").update(String(str || ""), "utf8").digest("hex");
}

// The prompt builders and parsers come from the extension itself. They are loaded again when one of
// the files has changed, so an edit shows up on the next request without restarting the server.
const BG_FILES = ["background.js", "i18n.js"].map((name) => path.join(EXT_DIR, name));
let bgModule = null;
let bgStamp = "";

function loadBg() {
  const stamp = BG_FILES.map((file) => fs.statSync(file).mtimeMs).join(":");
  if (!bgModule || stamp !== bgStamp) {
    for (const file of BG_FILES) delete require.cache[require.resolve(file)];
    bgModule = require(BG_FILES[0]);
    bgStamp = stamp;
  }
  return bgModule;
}

// While the server listens on this machine only, it answers requests addressed to it by a local
// name and, for the API, sent by its own page. A page on another site can make the browser send a
// request here (its host name may even resolve to this machine); such a request is refused.
const LOCAL_NAMES = new Set(["localhost", "127.0.0.1", "::1"]);

function hostName(value) {
  try {
    return new URL("http://" + String(value || "")).hostname.replace(/^\[|\]$/g, "").toLowerCase();
  } catch (_) {
    return "";
  }
}

// HOST is a bind address, not a URL host: the IPv6 loopback is written "::1" here, without brackets.
const LOCAL_ONLY = LOCAL_NAMES.has(String(HOST).replace(/^\[|\]$/g, "").toLowerCase());

function isAllowedRequest(req) {
  if (!LOCAL_ONLY) return true; // HOST was set to serve other machines on purpose
  if (!LOCAL_NAMES.has(hostName(req.headers.host))) return false;
  const origin = req.headers.origin;
  if (req.method === "POST" && origin) {
    try {
      return new URL(origin).host === req.headers.host;
    } catch (_) {
      return false;
    }
  }
  return true;
}

async function openUpstreamStream(body, signal) {
  const headers = {
    "Content-Type": "application/json",
    "User-Agent": "Index-Translate-Client/1.0",
    Accept: "text/event-stream, application/json",
    Authorization: "Bearer " + DEFAULT_KEY
  };
  const res = await fetch(DEFAULT_BASE + "/chat/completions", {
    method: "POST",
    headers,
    body: JSON.stringify(body),
    signal
  });
  return { res, via: "direct" };
}

function sendJson(res, status, obj) {
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store"
  });
  res.end(JSON.stringify(obj));
}

function writeSse(res, obj) {
  res.write("data: " + JSON.stringify(obj) + "\n\n");
}

async function readJsonBody(req) {
  const chunks = [];
  let bytes = 0;
  for await (const chunk of req) {
    bytes += chunk.length;
    if (bytes > 12 * 1024 * 1024) throw new Error("Payload too large");
    chunks.push(chunk);
  }
  return JSON.parse(Buffer.concat(chunks).toString("utf8") || "{}");
}

function openSse(res) {
  res.writeHead(200, {
    "Content-Type": "text/event-stream; charset=utf-8",
    "Cache-Control": "no-cache, no-transform",
    Connection: "keep-alive"
  });
}

// Shared lifecycle of the streaming endpoints: abort on client disconnect or timeout, and report
// upstream HTTP errors and exceptions as the final event of the given type.
async function proxyChatStream(res, doneType, buildBody, handleResponse) {
  const abortCtrl = new AbortController();
  res.on("close", () => { if (!res.writableEnded) abortCtrl.abort(); });
  const timer = setTimeout(() => abortCtrl.abort(), TIMEOUT_MS);
  const t0 = Date.now();
  const elapsed = () => Date.now() - t0;

  try {
    const { res: upRes, via } = await openUpstreamStream(buildBody(), abortCtrl.signal);
    if (!upRes.ok) {
      const errRaw = await upRes.text();
      writeSse(res, { type: doneType, ok: false, status: upRes.status, body: errRaw.slice(0, 1000), via });
    } else {
      await handleResponse(upRes, via, elapsed);
    }
    res.end();
  } catch (err) {
    if (!res.writableEnded) {
      writeSse(res, { type: doneType, ok: false, status: 0, body: String((err && err.message) || err) });
      res.end();
    }
  } finally {
    clearTimeout(timer);
  }
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://localhost");

    if (!isAllowedRequest(req)) {
      res.writeHead(403, { "Content-Type": "text/plain; charset=utf-8" });
      res.end("Forbidden");
      return;
    }

    if (req.method === "GET" && (url.pathname === "/" || url.pathname === "/index.html")) {
      res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
      res.end(fs.readFileSync(DEMO_HTML, "utf8"));
      return;
    }

    // The extension files the demo page uses: the content script, its stylesheet, the interface text it needs and the icon.
    const extFile = req.method === "GET" ? EXT_FILES[url.pathname] : undefined;
    if (extFile) {
      res.writeHead(200, { "Content-Type": extFile.type, "Cache-Control": "no-store" });
      res.end(fs.readFileSync(path.join(EXT_DIR, extFile.name)));
      return;
    }

    // 1. Primary Translation SSE Stream + Dynamic Glossary + Volatile LRU Hit
    if (req.method === "POST" && url.pathname === "/api/translate-stream") {
      const msg = await readJsonBody(req);
      const text = String(msg.text || "").trim();
      const target = String(msg.target || "中文").trim();
      const model = String(msg.model || "Index-Translate-9B").trim();
      const glossary = String(msg.glossary || "");
      const bg = loadBg();

      openSse(res);

      if (!text) {
        writeSse(res, { type: "DONE", ok: false, status: 0, body: "Nothing selected." });
        res.end();
        return;
      }

      const hits = bg.matchGlossaryHits(glossary, text);
      const gSig = hits.map((h) => h.source + "->" + h.target).join(";");
      const cacheKey = `${model}|${target}|${gSig}|${text}`;
      const hit = lruGet(translateMemCache, cacheKey);
      if (hit) {
        writeSse(res, { type: "DONE", ok: true, ...hit, glossaryHits: hits.length, cached: true, elapsedMs: 0 });
        res.end();
        return;
      }

      const isIndex = bg.isIndexModelOrHost(model, DEFAULT_BASE);
      const buildBody = () =>
        bg.attachIndexTemplateKwargs(
          {
            model,
            stream: true,
            temperature: isIndex ? 0.0 : 0.2,
            messages: bg.buildTranslateMessages(text, target, model, DEFAULT_BASE, hits)
          },
          model,
          DEFAULT_BASE
        );

      await proxyChatStream(res, "DONE", buildBody, async (upRes, via, elapsed) => {
        const full = await bg.readChatContent(upRes, (partial) => {
          const visible = bg.stripThink(partial);
          if (visible) {
            writeSse(res, {
              type: "DELTA",
              text: visible,
              model,
              via,
              glossaryHits: hits.length,
              elapsedMs: elapsed()
            });
          }
        });

        const clean = bg.stripThink(full);
        if (!clean) {
          writeSse(res, { type: "DONE", ok: false, status: 200, body: "Empty translation.", via });
          return;
        }
        const entry = { text: clean, model, via, glossaryHits: hits.length };
        lruSet(translateMemCache, cacheKey, entry);
        writeSse(res, { type: "DONE", ok: true, ...entry, elapsedMs: elapsed() });
      });
      return;
    }

    // 2. Immersive Bilingual Multi-Paragraph SSE Batch Stream (Alt+A)
    if (req.method === "POST" && url.pathname === "/api/immersive-stream") {
      const msg = await readJsonBody(req);
      const items = Array.isArray(msg.items) ? msg.items.map((x) => String(x || "").trim()) : [];
      const target = String(msg.target || "中文").trim();
      const model = String(msg.modelOverride || msg.model || "Index-Translate-2B").trim();
      const glossary = String(msg.glossary || "");
      const bg = loadBg();

      openSse(res);

      if (items.length === 0) {
        writeSse(res, { type: "BATCH_DONE", ok: false, status: 0, body: "Empty batch." });
        res.end();
        return;
      }

      const entries = bg.parseGlossary(glossary);
      const combined = items.join("\n");
      const batchHits = bg.matchGlossaryHits(entries, combined);
      const isIndex = bg.isIndexModelOrHost(model, DEFAULT_BASE);
      const buildBody = () =>
        bg.attachIndexTemplateKwargs(
          {
            model,
            stream: true,
            temperature: isIndex ? 0.0 : 0.2,
            messages:
              items.length === 1
                ? bg.buildTranslateMessages(items[0], target, model, DEFAULT_BASE, batchHits)
                : bg.buildImmersiveBatchMessages(items, target, model, DEFAULT_BASE, batchHits)
          },
          model,
          DEFAULT_BASE
        );
      const parseBatch = (visible) =>
        items.length === 1
          ? { 0: visible.replace(/^\u3016P0\u3017\s*/, "").trim() }
          : bg.parseAnchoredSegments(visible, items.length);

      await proxyChatStream(res, "BATCH_DONE", buildBody, async (upRes, via, elapsed) => {
        const full = await bg.readChatContent(upRes, (partial) => {
          const visible = bg.stripThink(partial);
          if (!visible) return;
          writeSse(res, {
            type: "BATCH_DELTA",
            segments: parseBatch(visible),
            model,
            via,
            glossaryHits: batchHits.length,
            elapsedMs: elapsed()
          });
        });

        writeSse(res, {
          type: "BATCH_DONE",
          ok: true,
          segments: parseBatch(bg.stripThink(full)),
          model,
          via,
          glossaryHits: batchHits.length,
          elapsedMs: elapsed()
        });
      });
      return;
    }

    // 3. Companion Study Card SSE Stream + Dynamic Glossary + Volatile LRU Hit
    if (req.method === "POST" && url.pathname === "/api/study-stream") {
      const msg = await readJsonBody(req);
      const text = String(msg.text || "").trim();
      const target = String(msg.target || "中文").trim();
      const glossary = String(msg.glossary || "");
      const bg = loadBg();
      const model = bg.resolveStudyModel(msg.model || "Index-Translate-9B", msg.modelOverride);

      openSse(res);

      if (!text) {
        writeSse(res, { type: "DONE", ok: false, status: 0, body: "Nothing selected." });
        res.end();
        return;
      }

      const hits = bg.matchGlossaryHits(glossary, text);
      const gSig = hits.map((h) => h.source + "->" + h.target).join(";");
      const cacheKey = `${model}|${target}|${gSig}|${text}`;
      const hit = lruGet(studyMemCache, cacheKey);
      if (hit) {
        writeSse(res, { type: "DONE", ok: true, ...hit, cached: true, elapsedMs: 0 });
        res.end();
        return;
      }

      const buildBody = () =>
        bg.attachIndexTemplateKwargs(
          {
            model,
            stream: true,
            temperature: 0.2,
            max_tokens: 1100,
            messages: bg.buildStudyMessages(text, target, hits)
          },
          model,
          DEFAULT_BASE
        );

      await proxyChatStream(res, "DONE", buildBody, async (upRes, via, elapsed) => {
        let lastSig = "";
        let cappedDeck = null;
        const full = await bg.readChatContent(upRes, (partial) => {
          const deck = bg.extractPartialStudyDeck(partial, text);
          if (!deck) return;
          const sig = deck.grammarNote.length + ":" + deck.cards.length;
          if (sig !== lastSig) {
            lastSig = sig;
            writeSse(res, { type: "PARTIAL", deck, model, via, elapsedMs: elapsed() });
          }
          if (deck.cards.length >= 4) {
            cappedDeck = {
              mode: deck.mode,
              sourceLang: deck.sourceLang,
              grammarNote: deck.grammarNote,
              cards: deck.cards
            };
            return false;
          }
        });

        const finalDeck = cappedDeck || bg.parseStudyCardJson(full, text);
        if (!finalDeck) {
          writeSse(res, { type: "DONE", ok: false, status: 200, body: "Empty study card response.", via });
          return;
        }
        const entry = { deck: finalDeck, model, via };
        lruSet(studyMemCache, cacheKey, entry);
        writeSse(res, { type: "DONE", ok: true, ...entry, elapsedMs: elapsed() });
      });
      return;
    }

    // 4. Zero-OCR Multimodal Vision Image Stream (Alt+S / Image Context Menu)
    if (req.method === "POST" && url.pathname === "/api/vision-stream") {
      const msg = await readJsonBody(req);
      const dataUrl = String(msg.dataUrl || "").trim();
      const target = String(msg.target || "中文").trim();
      const glossary = String(msg.glossary || "");
      const bg = loadBg();
      const model = bg.resolveVisionModel(msg.visionModel || "Index-Translate-35B-A3B", msg.modelOverride);

      openSse(res);

      if (!dataUrl.startsWith("data:image/")) {
        writeSse(res, { type: "DONE", ok: false, status: 0, body: "Invalid image payload." });
        res.end();
        return;
      }

      const cacheKey = `${model}|${target}|${sha256Hex(glossary)}|${sha256Hex(dataUrl)}`;
      const hit = lruGet(visionMemCache, cacheKey);
      if (hit) {
        writeSse(res, { type: "DONE", ok: true, ...hit, cached: true, elapsedMs: 0 });
        res.end();
        return;
      }

      const buildBody = () =>
        bg.attachIndexTemplateKwargs(
          {
            model,
            stream: true,
            temperature: 0.0,
            max_tokens: 1024,
            messages: bg.buildVisionTranslateMessages(dataUrl, target, model, DEFAULT_BASE, glossary)
          },
          model,
          DEFAULT_BASE
        );

      await proxyChatStream(res, "DONE", buildBody, async (upRes, via, elapsed) => {
        let lastShown = "";
        const full = await bg.readChatContent(upRes, (partial) => {
          const parsed = bg.parseVisionDualOutput(partial);
          if (parsed.translation && parsed.translation !== lastShown) {
            lastShown = parsed.translation;
            writeSse(res, {
              type: "DELTA",
              text: parsed.translation,
              sourceText: parsed.sourceText,
              model,
              via,
              vision: true,
              elapsedMs: elapsed()
            });
          }
        });

        const finalParsed = bg.parseVisionDualOutput(full);
        if (!finalParsed.translation) {
          writeSse(res, { type: "DONE", ok: false, status: 200, body: "No readable text found in image.", via });
          return;
        }
        const hits = finalParsed.sourceText ? bg.matchGlossaryHits(glossary, finalParsed.sourceText).length : 0;
        const entry = {
          text: finalParsed.translation,
          sourceText: finalParsed.sourceText,
          model,
          via,
          vision: true,
          glossaryHits: hits
        };
        lruSet(visionMemCache, cacheKey, entry);
        writeSse(res, { type: "DONE", ok: true, ...entry, elapsedMs: elapsed() });
      });
      return;
    }

    if (req.method === "POST" && url.pathname === "/api/runtime") {
      const msg = await readJsonBody(req);
      if (msg.type === "BILI_PURGE_VOLATILE") {
        translateMemCache.clear();
        studyMemCache.clear();
        visionMemCache.clear();
        sendJson(res, 200, { ok: true });
        return;
      }
      if (msg.type === "BILI_CAPTURE_TAB") {
        sendJson(res, 200, { ok: false, body: "Sandbox uses direct <canvas>/<img> region crop" });
        return;
      }
      if (msg.type === "BILI_GET_IMMERSIVE_PREFS") {
        sendJson(res, 200, { ok: true, immersiveModel: "Index-Translate-2B", target: "中文" });
        return;
      }
      sendJson(res, 400, { ok: false, status: 400, body: "Use streaming endpoints" });
      return;
    }

    res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
    res.end("Not found");
  } catch (err) {
    const failure = { ok: false, status: 0, body: String((err && err.message) || err) };
    if (!res.headersSent) {
      sendJson(res, 200, failure);
    } else if (!res.writableEnded) {
      // The event stream was already open: report the failure as its last event.
      writeSse(res, { type: "DONE", ...failure });
      res.end();
    }
  }
});

server.listen(PORT, HOST, () => {
  console.log(`[bili-study-demo] listening on http://${HOST}:${PORT}`);
  if (!LOCAL_ONLY) {
    console.warn(`[bili-study-demo] WARNING: listening on ${HOST}, so anyone on your network can use the API keys configured on this server through this service.`);
  }
});
