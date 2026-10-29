// bili-study-sandbox/server.js
"use strict";

const http = require("http");
const fs = require("fs");
const path = require("path");

const PORT = Number(process.env.PORT) || 3001;
const EXT_DIR = path.resolve(__dirname, "../bili-index-translate");
const DEMO_HTML = path.join(__dirname, "index.html");

const DEFAULT_BASE = "https://index-translate.bilibili.com/v1";
const DEFAULT_RELAY = "https://seep.eu.org/";
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

function loadBg() {
  const p = path.join(EXT_DIR, "background.js");
  delete require.cache[require.resolve(p)];
  return require(p);
}

async function openUpstreamStream(body, signal) {
  let via = "direct";
  const headers = {
    "Content-Type": "application/json",
    "User-Agent": "Index-Translate-Client/1.0",
    Accept: "text/event-stream, application/json",
    Authorization: "Bearer " + DEFAULT_KEY
  };
  let res = await fetch(DEFAULT_BASE + "/chat/completions", {
    method: "POST",
    headers,
    body: JSON.stringify(body),
    signal
  });
  if (res.status === 412) {
    via = "relay";
    res = await fetch(DEFAULT_RELAY + DEFAULT_BASE + "/chat/completions", {
      method: "POST",
      headers,
      body: JSON.stringify(body),
      signal
    });
  }
  return { res, via };
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
  let raw = "";
  for await (const chunk of req) {
    raw += chunk;
    if (raw.length > 12 * 1024 * 1024) throw new Error("Payload too large");
  }
  return JSON.parse(raw || "{}");
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, "http://localhost");

    if (req.method === "GET" && (url.pathname === "/" || url.pathname === "/index.html")) {
      res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
      res.end(fs.readFileSync(DEMO_HTML, "utf8"));
      return;
    }

    if (req.method === "GET" && url.pathname === "/ext/content.css") {
      res.writeHead(200, { "Content-Type": "text/css; charset=utf-8", "Cache-Control": "no-store" });
      res.end(fs.readFileSync(path.join(EXT_DIR, "content.css"), "utf8"));
      return;
    }

    if (req.method === "GET" && url.pathname === "/ext/content.js") {
      res.writeHead(200, { "Content-Type": "application/javascript; charset=utf-8", "Cache-Control": "no-store" });
      res.end(fs.readFileSync(path.join(EXT_DIR, "content.js"), "utf8"));
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

      res.writeHead(200, {
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-cache, no-transform",
        Connection: "keep-alive"
      });

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

      const abortCtrl = new AbortController();
      res.on("close", () => { if (!res.writableEnded) abortCtrl.abort(); });
      const timer = setTimeout(() => abortCtrl.abort(), TIMEOUT_MS);
      const t0 = Date.now();
      const isIndex = bg.isIndexModelOrHost(model, DEFAULT_BASE);

      try {
        const reqBody = bg.attachIndexTemplateKwargs(
          {
            model,
            stream: true,
            temperature: isIndex ? 0.0 : 0.2,
            messages: bg.buildTranslateMessages(text, target, model, DEFAULT_BASE, hits)
          },
          model,
          DEFAULT_BASE
        );
        const { res: upRes, via } = await openUpstreamStream(reqBody, abortCtrl.signal);

        if (!upRes.ok) {
          const errRaw = await upRes.text();
          writeSse(res, { type: "DONE", ok: false, status: upRes.status, body: errRaw.slice(0, 1000), via });
          res.end();
          return;
        }

        const reader = upRes.body.getReader();
        const decoder = new TextDecoder();
        let sseBuf = "";
        let full = "";

        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          sseBuf += decoder.decode(value, { stream: true });
          const lines = sseBuf.split(/\r?\n/);
          sseBuf = lines.pop() || "";

          for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed.startsWith("data:")) continue;
            const payload = trimmed.slice(5).trim();
            if (!payload || payload === "[DONE]") continue;
            try {
              const j = JSON.parse(payload);
              const delta = j?.choices?.[0]?.delta?.content || "";
              if (delta) {
                full += delta;
                const visible = bg.stripThink(full);
                if (visible) {
                  writeSse(res, {
                    type: "DELTA",
                    text: visible,
                    model,
                    via,
                    glossaryHits: hits.length,
                    elapsedMs: Date.now() - t0
                  });
                }
              }
            } catch (_) {}
          }
        }

        const clean = bg.stripThink(full);
        if (!clean) {
          writeSse(res, { type: "DONE", ok: false, status: 200, body: "Empty translation.", via });
        } else {
          const entry = { text: clean, model, via, glossaryHits: hits.length };
          lruSet(translateMemCache, cacheKey, entry);
          writeSse(res, { type: "DONE", ok: true, ...entry, elapsedMs: Date.now() - t0 });
        }
        res.end();
      } catch (err) {
        if (!res.writableEnded) {
          writeSse(res, { type: "DONE", ok: false, status: 0, body: String((err && err.message) || err) });
          res.end();
        }
      } finally {
        clearTimeout(timer);
      }
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

      res.writeHead(200, {
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-cache, no-transform",
        Connection: "keep-alive"
      });

      if (items.length === 0) {
        writeSse(res, { type: "BATCH_DONE", ok: false, status: 0, body: "Empty batch." });
        res.end();
        return;
      }

      const entries = bg.parseGlossary(glossary);
      const combined = items.join("\n");
      const batchHits = bg.matchGlossaryHits(entries, combined);
      const abortCtrl = new AbortController();
      res.on("close", () => { if (!res.writableEnded) abortCtrl.abort(); });
      const timer = setTimeout(() => abortCtrl.abort(), TIMEOUT_MS);
      const t0 = Date.now();
      const isIndex = bg.isIndexModelOrHost(model, DEFAULT_BASE);

      try {
        const messages =
          items.length === 1
            ? bg.buildTranslateMessages(items[0], target, model, DEFAULT_BASE, batchHits)
            : bg.buildImmersiveBatchMessages(items, target, model, DEFAULT_BASE, batchHits);

        const reqBody = bg.attachIndexTemplateKwargs(
          {
            model,
            stream: true,
            temperature: isIndex ? 0.0 : 0.2,
            messages
          },
          model,
          DEFAULT_BASE
        );
        const { res: upRes, via } = await openUpstreamStream(reqBody, abortCtrl.signal);

        if (!upRes.ok) {
          const errRaw = await upRes.text();
          writeSse(res, { type: "BATCH_DONE", ok: false, status: upRes.status, body: errRaw.slice(0, 1000), via });
          res.end();
          return;
        }

        const reader = upRes.body.getReader();
        const decoder = new TextDecoder();
        let sseBuf = "";
        let full = "";

        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          sseBuf += decoder.decode(value, { stream: true });
          const lines = sseBuf.split(/\r?\n/);
          sseBuf = lines.pop() || "";

          for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed.startsWith("data:")) continue;
            const payload = trimmed.slice(5).trim();
            if (!payload || payload === "[DONE]") continue;
            try {
              const j = JSON.parse(payload);
              const delta = j?.choices?.[0]?.delta?.content || "";
              if (delta) {
                full += delta;
                const visible = bg.stripThink(full);
                if (!visible) continue;
                const segments =
                  items.length === 1
                    ? { 0: visible.replace(/^\u3016P0\u3017\s*/, "").trim() }
                    : bg.parseAnchoredSegments(visible, items.length);
                writeSse(res, {
                  type: "BATCH_DELTA",
                  segments,
                  model,
                  via,
                  glossaryHits: batchHits.length,
                  elapsedMs: Date.now() - t0
                });
              }
            } catch (_) {}
          }
        }

        const clean = bg.stripThink(full);
        const segments =
          items.length === 1
            ? { 0: clean.replace(/^\u3016P0\u3017\s*/, "").trim() }
            : bg.parseAnchoredSegments(clean, items.length);
        writeSse(res, {
          type: "BATCH_DONE",
          ok: true,
          segments,
          model,
          via,
          glossaryHits: batchHits.length,
          elapsedMs: Date.now() - t0
        });
        res.end();
      } catch (err) {
        if (!res.writableEnded) {
          writeSse(res, { type: "BATCH_DONE", ok: false, status: 0, body: String((err && err.message) || err) });
          res.end();
        }
      } finally {
        clearTimeout(timer);
      }
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

      res.writeHead(200, {
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-cache, no-transform",
        Connection: "keep-alive"
      });

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

      const abortCtrl = new AbortController();
      res.on("close", () => { if (!res.writableEnded) abortCtrl.abort(); });
      const timer = setTimeout(() => abortCtrl.abort(), TIMEOUT_MS);
      const t0 = Date.now();

      try {
        const reqBody = bg.attachIndexTemplateKwargs(
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
        const { res: upRes, via } = await openUpstreamStream(reqBody, abortCtrl.signal);

        if (!upRes.ok) {
          const errRaw = await upRes.text();
          writeSse(res, { type: "DONE", ok: false, status: upRes.status, body: errRaw.slice(0, 1000), via });
          res.end();
          return;
        }

        const reader = upRes.body.getReader();
        const decoder = new TextDecoder();
        let sseBuf = "";
        let full = "";
        let lastSig = "";

        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          sseBuf += decoder.decode(value, { stream: true });
          const lines = sseBuf.split(/\r?\n/);
          sseBuf = lines.pop() || "";

          for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed.startsWith("data:")) continue;
            const payload = trimmed.slice(5).trim();
            if (!payload || payload === "[DONE]") continue;
            try {
              const j = JSON.parse(payload);
              const delta = j?.choices?.[0]?.delta?.content || "";
              if (delta) {
                full += delta;
                const partial = bg.extractPartialStudyDeck(full, text);
                if (partial) {
                  const sig = partial.grammarNote.length + ":" + partial.cards.length;
                  if (sig !== lastSig) {
                    lastSig = sig;
                    writeSse(res, {
                      type: "PARTIAL",
                      deck: partial,
                      model,
                      via,
                      elapsedMs: Date.now() - t0
                    });
                  }
                  if (partial.cards.length >= 4) {
                    try { await reader.cancel(); } catch (_) {}
                    const cappedDeck = {
                      mode: partial.mode,
                      sourceLang: partial.sourceLang,
                      grammarNote: partial.grammarNote,
                      cards: partial.cards
                    };
                    const entry = { deck: cappedDeck, model, via };
                    lruSet(studyMemCache, cacheKey, entry);
                    writeSse(res, {
                      type: "DONE",
                      ok: true,
                      ...entry,
                      elapsedMs: Date.now() - t0
                    });
                    res.end();
                    return;
                  }
                }
              }
            } catch (_) {}
          }
        }

        const finalDeck = bg.parseStudyCardJson(full, text);
        if (!finalDeck) {
          writeSse(res, { type: "DONE", ok: false, status: 200, body: "Empty study card response.", via });
        } else {
          const entry = { deck: finalDeck, model, via };
          lruSet(studyMemCache, cacheKey, entry);
          writeSse(res, {
            type: "DONE",
            ok: true,
            ...entry,
            elapsedMs: Date.now() - t0
          });
        }
        res.end();
      } catch (err) {
        if (!res.writableEnded) {
          writeSse(res, { type: "DONE", ok: false, status: 0, body: String((err && err.message) || err) });
          res.end();
        }
      } finally {
        clearTimeout(timer);
      }
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

      res.writeHead(200, {
        "Content-Type": "text/event-stream; charset=utf-8",
        "Cache-Control": "no-cache, no-transform",
        Connection: "keep-alive"
      });

      if (!dataUrl.startsWith("data:image/")) {
        writeSse(res, { type: "DONE", ok: false, status: 0, body: "Invalid image payload." });
        res.end();
        return;
      }

      const cacheKey = `${model}|${target}|${glossary.length}:${dataUrl.length}:${dataUrl.slice(-64)}`;
      const hit = lruGet(visionMemCache, cacheKey);
      if (hit) {
        writeSse(res, { type: "DONE", ok: true, ...hit, cached: true, elapsedMs: 0 });
        res.end();
        return;
      }

      const abortCtrl = new AbortController();
      res.on("close", () => { if (!res.writableEnded) abortCtrl.abort(); });
      const timer = setTimeout(() => abortCtrl.abort(), TIMEOUT_MS);
      const t0 = Date.now();

      try {
        const reqBody = bg.attachIndexTemplateKwargs(
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
        const { res: upRes, via } = await openUpstreamStream(reqBody, abortCtrl.signal);

        if (!upRes.ok) {
          const errRaw = await upRes.text();
          writeSse(res, { type: "DONE", ok: false, status: upRes.status, body: errRaw.slice(0, 1000), via });
          res.end();
          return;
        }

        const reader = upRes.body.getReader();
        const decoder = new TextDecoder();
        let sseBuf = "";
        let full = "";
        let lastShown = "";

        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          sseBuf += decoder.decode(value, { stream: true });
          const lines = sseBuf.split(/\r?\n/);
          sseBuf = lines.pop() || "";

          for (const line of lines) {
            const trimmed = line.trim();
            if (!trimmed.startsWith("data:")) continue;
            const payload = trimmed.slice(5).trim();
            if (!payload || payload === "[DONE]") continue;
            try {
              const j = JSON.parse(payload);
              const delta = j?.choices?.[0]?.delta?.content || "";
              if (delta) {
                full += delta;
                const parsed = bg.parseVisionDualOutput(full);
                if (parsed.translation && parsed.translation !== lastShown) {
                  lastShown = parsed.translation;
                  writeSse(res, {
                    type: "DELTA",
                    text: parsed.translation,
                    sourceText: parsed.sourceText,
                    model,
                    via,
                    vision: true,
                    elapsedMs: Date.now() - t0
                  });
                }
              }
            } catch (_) {}
          }
        }

        const finalParsed = bg.parseVisionDualOutput(full);
        if (!finalParsed.translation) {
          writeSse(res, { type: "DONE", ok: false, status: 200, body: "No readable text found in image.", via });
        } else {
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
          writeSse(res, { type: "DONE", ok: true, ...entry, elapsedMs: Date.now() - t0 });
        }
        res.end();
      } catch (err) {
        if (!res.writableEnded) {
          writeSse(res, { type: "DONE", ok: false, status: 0, body: String((err && err.message) || err) });
          res.end();
        }
      } finally {
        clearTimeout(timer);
      }
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
    sendJson(res, 200, { ok: false, status: 0, body: String((err && err.message) || err) });
  }
});

server.listen(PORT, "0.0.0.0", () => {
  console.log(`[bili-study-demo] listening on http://0.0.0.0:${PORT}`);
});
