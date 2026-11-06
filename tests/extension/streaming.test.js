"use strict";

// 四条流式请求路径的行为: SSE 增量与分块边界, 非 SSE JSON 回退, 取消, 网络错误, 学习卡 4 张封顶.
const test = require("node:test");
const assert = require("node:assert/strict");

const settings = { proxy: "" };
const local = { glossary: "" };
const calls = [];
let respond;

globalThis.chrome = {
  storage: {
    sync: { get: async (defaults) => ({ ...defaults, ...settings }) },
    local: { get: async (defaults) => ({ ...defaults, ...local }) }
  }
};
globalThis.fetch = async (url, init) => {
  calls.push({ url: String(url), body: JSON.parse(init.body) });
  if (init.signal && init.signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
  return respond(init);
};

const bg = require("../../bili-index-translate/background.js");

let seq = 0;
const unique = (label) => label + " " + ++seq;
const encoder = new TextEncoder();
const delta = (content) => "data: " + JSON.stringify({ choices: [{ delta: { content } }] }) + "\n\n";

function reset() {
  calls.length = 0;
  settings.proxy = "";
  local.glossary = "";
}

function sseResponse(chunks, stats = {}) {
  stats.served = 0;
  stats.cancelled = false;
  const body = new ReadableStream({
    pull(controller) {
      if (stats.served < chunks.length) controller.enqueue(encoder.encode(chunks[stats.served++]));
      else controller.close();
    },
    cancel() {
      stats.cancelled = true;
    }
  });
  return new Response(body, { status: 200, headers: { "content-type": "text/event-stream; charset=utf-8" } });
}

function jsonResponse(content) {
  return new Response(JSON.stringify({ choices: [{ message: { content } }] }), {
    status: 200,
    headers: { "content-type": "application/json" }
  });
}

test("streamTranslation 处理跨分块的 SSE 行, CRLF 和无法解析的行", async () => {
  reset();
  const line = delta("世界");
  respond = () =>
    sseResponse([
      delta("你好") + ": keep-alive\r\n\r\n",
      "data: {not json}\n\n",
      line.slice(0, 9),
      line.slice(9).replace(/\n/g, "\r\n"),
      "data: [DONE]\n\n"
    ]);
  const seen = [];
  const res = await bg.streamTranslation(unique("hello"), (d) => seen.push(d.text));
  assert.equal(res.ok, true);
  assert.equal(res.text, "你好世界");
  assert.deepEqual(seen, ["你好", "你好世界"]);
});

test("streamTranslation 在上游返回非 SSE 的 JSON 时直接解析, 并去掉思维链", async () => {
  reset();
  respond = () => jsonResponse("<think>plan</think>译文");
  const seen = [];
  const text = unique("hello");
  const res = await bg.streamTranslation(text, (d) => seen.push(d));
  assert.equal(res.ok, true);
  assert.equal(res.text, "译文");
  assert.equal(res.via, "direct");
  assert.deepEqual(seen, []);
  const again = await bg.streamTranslation(text, () => {});
  assert.equal(again.cached, true);
  assert.equal(calls.length, 1);
});

test("streamTranslation 对空输出和非 JSON 正文返回错误", async () => {
  reset();
  respond = () => sseResponse([delta("<think>only thinking"), "data: [DONE]\n\n"]);
  const empty = await bg.streamTranslation(unique("hello"), () => {});
  assert.deepEqual(empty, { ok: false, status: 200, body: "Empty translation.", via: "direct" });

  respond = () => new Response("<html>ok</html>", { status: 200, headers: { "content-type": "text/html" } });
  const html = await bg.streamTranslation(unique("hello"), () => {});
  assert.equal(html.ok, false);
  assert.equal(html.status, 0);
  assert.match(html.body, /JSON/);
});

test("streamTranslation 把网络异常报告为 status 0", async () => {
  reset();
  respond = () => {
    throw new TypeError("fetch failed");
  };
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.deepEqual(res, { ok: false, status: 0, body: "fetch failed" });
});

test("streamTranslation 在调用方中止时返回 cancelled, 不再推送增量", async () => {
  reset();
  respond = (init) => {
    const body = new ReadableStream({
      start(controller) {
        controller.enqueue(encoder.encode(delta("部分")));
        init.signal.addEventListener("abort", () => controller.error(new DOMException("aborted", "AbortError")));
      }
    });
    return new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } });
  };
  const ctrl = new AbortController();
  const seen = [];
  const res = await bg.streamTranslation(
    unique("hello"),
    (d) => {
      seen.push(d.text);
      ctrl.abort();
    },
    ctrl.signal
  );
  assert.deepEqual(res, { ok: false, cancelled: true, status: 0, body: "Cancelled" });
  assert.deepEqual(seen, ["部分"]);

  const pre = new AbortController();
  pre.abort();
  const early = await bg.streamTranslation(unique("hello"), () => {}, pre.signal);
  assert.equal(early.cancelled, true);
});

test("streamVisionTranslation 只在译文变化时推送, 结束时带原文并按原文统计术语命中", async () => {
  reset();
  local.glossary = "scheduler => 调度器";
  respond = () =>
    sseResponse([
      delta("\u3016\u8bd1\u6587\u3017调度器"),
      delta("重试\u3016"),
      delta("\u539f\u6587\u3017The scheduler"),
      delta(" retries."),
      "data: [DONE]\n\n"
    ]);
  const seen = [];
  const res = await bg.streamVisionTranslation("data:image/png;base64,AAAA" + ++seq, "", (d) => seen.push([d.text, d.sourceText]));
  assert.equal(res.ok, true);
  assert.equal(res.text, "调度器重试");
  assert.equal(res.sourceText, "The scheduler retries.");
  assert.equal(res.vision, true);
  assert.equal(res.glossaryHits, 1);
  assert.deepEqual(seen, [
    ["调度器", ""],
    ["调度器重试", ""]
  ]);
  assert.equal(calls[0].body.messages[0].content[1].image_url.url.startsWith("data:image/png;base64,AAAA"), true);
});

test("streamVisionTranslation 解析 JSON 回退, 无可读文本时报错", async () => {
  reset();
  respond = () => jsonResponse("\u3016\u8bd1\u6587\u3017你好\u3016\u539f\u6587\u3017Hello");
  const res = await bg.streamVisionTranslation("data:image/png;base64,BBBB" + ++seq, "", () => {});
  assert.equal(res.ok, true);
  assert.equal(res.text, "你好");
  assert.equal(res.sourceText, "Hello");

  respond = () => jsonResponse("");
  const none = await bg.streamVisionTranslation("data:image/png;base64,CCCC" + ++seq, "", () => {});
  assert.deepEqual(none, { ok: false, status: 200, body: "No readable text found in image.", via: "direct" });
});

test("streamImmersiveBatch 按锚点流式分段, 结束时写入缓存", async () => {
  reset();
  const items = [unique("first"), unique("second")];
  respond = () =>
    sseResponse([delta("\u3016P0\u3017第一"), delta("段\n\u3016P1\u3017第"), delta("二段"), "data: [DONE]\n\n"]);
  const progress = [];
  const res = await bg.streamImmersiveBatch(items, "", (p) => progress.push(p.segments));
  assert.equal(res.ok, true);
  assert.deepEqual(res.segments, { 0: "第一段", 1: "第二段" });
  assert.deepEqual(progress.at(-1), { 0: "第一段", 1: "第二段" });
  assert.ok(progress.length >= 2);

  const again = await bg.streamImmersiveBatch(items, "", () => {});
  assert.equal(again.cached, true);
  assert.deepEqual(again.segments, { 0: "第一段", 1: "第二段" });
  assert.equal(calls.length, 1);
});

test("streamImmersiveBatch 单段请求去掉 〖P0〗 前缀, 部分命中缓存时只请求未缓存段", async () => {
  reset();
  const cachedItem = unique("cached");
  respond = () => jsonResponse("\u3016P0\u3017已缓存");
  const first = await bg.streamImmersiveBatch([cachedItem], "", () => {});
  assert.deepEqual(first.segments, { 0: "已缓存" });

  const fresh = unique("fresh");
  respond = () => sseResponse([delta("新段落"), "data: [DONE]\n\n"]);
  const progress = [];
  const res = await bg.streamImmersiveBatch([cachedItem, "", fresh], "", (p) => progress.push(p));
  assert.equal(res.ok, true);
  assert.deepEqual(res.segments, { 0: "已缓存", 1: "", 2: "新段落" });
  assert.equal(progress[0].via, "cache");
  assert.equal(calls.length, 2);
  assert.match(calls[1].body.messages[0].content, new RegExp(fresh));
  assert.doesNotMatch(calls[1].body.messages[0].content, new RegExp(cachedItem));
});

test("streamStudyCard 拿到 4 张卡后取消上游流并缓存封顶结果", async () => {
  reset();
  const card = (i) => JSON.stringify({ term: "term" + i, meaning: "释义" + i });
  const stats = {};
  respond = () =>
    sseResponse(
      [
        delta('{"mode":"sentence","sourceLang":"en-US","grammarNote":"句法","cards":['),
        delta(card(1) + ","),
        delta(card(2) + ","),
        delta(card(3) + ","),
        delta(card(4) + ","),
        delta(card(5) + ","),
        delta(card(6) + "]}"),
        "data: [DONE]\n\n"
      ],
      stats
    );
  const partials = [];
  const text = unique("A sentence long enough to study");
  const res = await bg.streamStudyCard(text, "", (p) => partials.push(p.deck.cards.length));
  assert.equal(res.ok, true);
  assert.deepEqual(res.deck.cards.map((c) => c.term), ["term1", "term2", "term3", "term4"]);
  assert.equal(res.deck.streaming, undefined);
  assert.equal(stats.cancelled, true);
  assert.ok(stats.served < 8, "stream was read to the end: " + stats.served);
  assert.deepEqual(partials, [0, 1, 2, 3, 4]);

  const again = await bg.streamStudyCard(text, "", () => {});
  assert.equal(again.cached, true);
  assert.equal(again.deck.cards.length, 4);
  assert.equal(calls.length, 1);
});

test("streamStudyCard 正常结束时解析完整 JSON, 非 SSE 时走 JSON 回退", async () => {
  reset();
  const json = JSON.stringify({ mode: "word", sourceLang: "en-US", grammarNote: "", cards: [{ term: "ephemeral", meaning: "短暂的" }] });
  respond = () => sseResponse([delta(json.slice(0, 20)), delta(json.slice(20)), "data: [DONE]\n\n"]);
  const res = await bg.streamStudyCard(unique("ephemeral"), "", () => {});
  assert.equal(res.ok, true);
  assert.equal(res.deck.mode, "word");
  assert.equal(res.deck.cards[0].term, "ephemeral");

  respond = () => jsonResponse(json);
  const fallback = await bg.streamStudyCard(unique("ephemeral"), "", () => {});
  assert.equal(fallback.ok, true);
  assert.equal(fallback.deck.cards[0].meaning, "短暂的");
});