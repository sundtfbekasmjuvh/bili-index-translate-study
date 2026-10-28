// /home/user/bili-index-translate-demo/server.js
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
const MEM_LIMIT = 24;
const translateMemCache = new Map();
const studyMemCache = new Map();

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

function stripSlashes(url) {
  return String(url || "").trim().replace(/\/+$/, "");
}

function translateSystemPrompt(target) {
  return (
    "You are a translation engine. Translate the user text into " +
    target +
    ". Output ONLY the translation \u2014 no explanation, no quotes, no original text."
  );
}

async function openUpstreamStream(body, signal) {
  let via = "direct";
  let res = await fetch(DEFAULT_BASE + "/chat/completions", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Accept: "text/event-stream, application/json",
      Authorization: "Bearer " + DEFAULT_KEY
    },
    body: JSON.stringify(body),
    signal
  });
  if (res.status === 412) {
    via = "relay";
    res = await fetch(DEFAULT_RELAY + DEFAULT_BASE + "/chat/completions", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "text/event-stream, application/json",
        Authorization: "Bearer " + DEFAULT_KEY
      },
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
    if (raw.length > 64000) throw new Error("Payload too large");
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

    // 1. Primary Translation SSE Stream + Volatile LRU Hit
    if (req.method === "POST" && url.pathname === "/api/translate-stream") {
      const msg = await readJsonBody(req);
      const text = String(msg.text || "").trim();
      const target = String(msg.target || "中文").trim();
      const model = String(msg.model || "Index-Translate-9B").trim();

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

      const cacheKey = `${model}|${target}|${text}`;
      const hit = lruGet(translateMemCache, cacheKey);
      if (hit) {
        writeSse(res, { type: "DONE", ok: true, ...hit, cached: true, elapsedMs: 0 });
        res.end();
        return;
      }

      const abortCtrl = new AbortController();
      req.on("close", () => abortCtrl.abort());
      const timer = setTimeout(() => abortCtrl.abort(), TIMEOUT_MS);
      const t0 = Date.now();

      try {
        const { res: upRes, via } = await openUpstreamStream(
          {
            model,
            stream: true,
            temperature: 0.2,
            messages: [
              { role: "system", content: translateSystemPrompt(target) },
              { role: "user", content: text }
            ]
          },
          abortCtrl.signal
        );

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
                writeSse(res, {
                  type: "DELTA",
                  text: full.trimStart(),
                  model,
                  via,
                  elapsedMs: Date.now() - t0
                });
              }
            } catch (_) {}
          }
        }

        const clean = full.trim();
        if (!clean) {
          writeSse(res, { type: "DONE", ok: false, status: 200, body: "Empty translation.", via });
        } else {
          const entry = { text: clean, model, via };
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

    // 2. Companion Study Card SSE Stream + Volatile LRU Hit
    if (req.method === "POST" && url.pathname === "/api/study-stream") {
      const msg = await readJsonBody(req);
      const text = String(msg.text || "").trim();
      const target = String(msg.target || "中文").trim();
      const model = String(msg.modelOverride || msg.model || "Index-Translate-9B").trim();
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

      const cacheKey = `${model}|${target}|${text}`;
      const hit = lruGet(studyMemCache, cacheKey);
      if (hit) {
        writeSse(res, { type: "DONE", ok: true, ...hit, cached: true, elapsedMs: 0 });
        res.end();
        return;
      }

      const abortCtrl = new AbortController();
      req.on("close", () => abortCtrl.abort());
      const timer = setTimeout(() => abortCtrl.abort(), TIMEOUT_MS);
      const t0 = Date.now();

      try {
        const { res: upRes, via } = await openUpstreamStream(
          {
            model,
            stream: true,
            temperature: 0.2,
            max_tokens: 1100,
            messages: bg.buildStudyMessages(text, target)
          },
          abortCtrl.signal
        );

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

    if (req.method === "POST" && url.pathname === "/api/runtime") {
      const msg = await readJsonBody(req);
      if (msg.type === "BILI_PURGE_VOLATILE") {
        translateMemCache.clear();
        studyMemCache.clear();
        sendJson(res, 200, { ok: true });
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
