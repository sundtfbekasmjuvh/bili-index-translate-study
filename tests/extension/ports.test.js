"use strict";

// 流式端口: 页面打开一个具名端口并发出开始消息, 后台把进度和最终结果发回.
// 进度消息合并发送 (第一条立即发, 之后每 STREAM_FLUSH_MS 最多一条, 只带最新状态), 双语对照只发有变化的段落.
const test = require("node:test");
const assert = require("node:assert/strict");
const { storageArea } = require("../support/fake-storage");

const settings = { proxy: "" };
const local = { glossary: "" };
let respond;
let failSettings = false;

const sync = storageArea(settings);
const syncGet = sync.get;
sync.get = async (keys) => {
  if (failSettings) throw new Error("storage unavailable");
  return syncGet(keys);
};
globalThis.chrome = { runtime: { id: "ext-id", getURL: (path) => "chrome-extension://ext-id/" + path }, storage: { sync: sync, local: storageArea(local) } };
globalThis.fetch = async (url, init) => respond(init);

const bg = require("../../bili-index-translate/background.js");

let seq = 0;
const unique = (label) => label + " " + ++seq;
const encoder = new TextEncoder();
const delta = (content) => "data: " + JSON.stringify({ choices: [{ delta: { content } }] }) + "\n\n";
const settle = () => new Promise((resolve) => setImmediate(resolve));

// 一条可以从测试里逐块推送的上游流.
function liveUpstream() {
  const up = { push: null, signal: null, cancelled: false };
  respond = (init) => {
    up.signal = init.signal;
    const body = new ReadableStream({
      start(controller) {
        up.push = (chunk) => controller.enqueue(encoder.encode(chunk));
        init.signal.addEventListener("abort", () => {
          try {
            controller.error(new DOMException("The operation was aborted.", "AbortError"));
          } catch (_) {}
        });
      },
      cancel() {
        up.cancelled = true;
      }
    });
    return new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } });
  };
  up.opened = async () => {
    while (!up.push) await settle();
  };
  return up;
}

function fakePort(name) {
  const port = {
    name,
    sender: { id: "ext-id", tab: { id: 1, windowId: 7 }, url: "https://www.bilibili.com/" },
    sent: [],
    onMessage: { addListener: (fn) => (port.receive = fn) },
    onDisconnect: { addListener: (fn) => (port.close = fn) },
    postMessage: (msg) => port.sent.push(msg),
    disconnect: () => (port.disconnected = true)
  };
  return port;
}

async function waitFor(port, count) {
  for (let i = 0; i < 200 && port.sent.length < count; i++) await settle();
  assert.equal(port.sent.length, count, JSON.stringify(port.sent));
}

test("划词流: 第一条增量立即发出, 间隔内的增量合并成一条最新的, 最终结果取代还没发出的增量", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout", "Date"] });
  const up = liveUpstream();
  const port = fakePort("bili-translate-stream");
  bg.handleStreamPort(port);
  port.receive({ type: "START_TRANSLATE_STREAM", text: unique("hello") });
  await up.opened();

  up.push(delta("你"));
  await waitFor(port, 1);
  assert.deepEqual(port.sent[0], { type: "DELTA", text: "你", model: "Index-Translate-9B", via: "direct", glossaryHits: 0 });

  for (const part of ["好", "世", "界"]) {
    up.push(delta(part));
    await settle();
  }
  assert.equal(port.sent.length, 1);
  t.mock.timers.tick(bg.STREAM_FLUSH_MS);
  await waitFor(port, 2);
  assert.equal(port.sent[1].type, "DELTA");
  assert.equal(port.sent[1].text, "你好世界");

  up.push(delta("！"));
  up.push("data: [DONE]\n\n");
  await waitFor(port, 3);
  assert.equal(port.sent[2].type, "DONE");
  assert.equal(port.sent[2].ok, true);
  assert.equal(port.sent[2].text, "你好世界！");

  // 被最终结果取代的那条增量之后也不会再发.
  t.mock.timers.tick(1000);
  await settle();
  assert.equal(port.sent.length, 3);
});

test("双语对照流: 进度只带有变化的段落, 最终结果带全部段落", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout", "Date"] });
  const up = liveUpstream();
  const port = fakePort("bili-immersive-stream");
  bg.handleStreamPort(port);
  port.receive({ type: "START_IMMERSIVE_BATCH", items: [unique("first"), unique("second")], modelOverride: "" });
  await up.opened();

  up.push(delta("\u3016P0\u3017甲"));
  await waitFor(port, 1);
  assert.equal(port.sent[0].type, "BATCH_DELTA");
  assert.deepEqual(port.sent[0].segments, { 0: "甲" });

  up.push(delta("\n\u3016P1\u3017乙"));
  await settle();
  t.mock.timers.tick(bg.STREAM_FLUSH_MS);
  await waitFor(port, 2);
  assert.deepEqual(port.sent[1].segments, { 1: "乙" });

  up.push(delta("丙"));
  await settle();
  t.mock.timers.tick(bg.STREAM_FLUSH_MS);
  await waitFor(port, 3);
  assert.deepEqual(port.sent[2].segments, { 1: "乙丙" });

  up.push("data: [DONE]\n\n");
  await waitFor(port, 4);
  assert.equal(port.sent[3].type, "BATCH_DONE");
  assert.deepEqual(port.sent[3].segments, { 0: "甲", 1: "乙丙" });
});

test("请求发出前就失败 (读不到设置) 也会收到最终消息, 页面不会一直等待", async () => {
  failSettings = true;
  try {
    for (const [name, start, done] of [
      ["bili-translate-stream", { type: "START_TRANSLATE_STREAM", text: "hello" }, "DONE"],
      ["bili-study-stream", { type: "START_STUDY_STREAM", text: "hello" }, "DONE"],
      ["bili-image-stream", { type: "START_IMAGE_STREAM", dataUrl: "data:image/png;base64,AAAA" }, "DONE"],
      ["bili-immersive-stream", { type: "START_IMMERSIVE_BATCH", items: ["hello"] }, "BATCH_DONE"]
    ]) {
      const port = fakePort(name);
      bg.handleStreamPort(port);
      port.receive(start);
      await waitFor(port, 1);
      assert.deepEqual(port.sent[0], { type: done, ok: false, status: 0, body: "storage unavailable" });
    }
  } finally {
    failSettings = false;
  }
});

test("端口断开时取消上游请求, 之后不再发消息; 同一端口的第二条开始消息被忽略", async () => {
  const up = liveUpstream();
  let requests = 0;
  const base = respond;
  respond = (init) => {
    requests++;
    return base(init);
  };
  const port = fakePort("bili-translate-stream");
  bg.handleStreamPort(port);
  const text = unique("hello");
  port.receive({ type: "START_TRANSLATE_STREAM", text });
  port.receive({ type: "START_TRANSLATE_STREAM", text });
  await up.opened();
  up.push(delta("你"));
  await waitFor(port, 1);
  assert.equal(requests, 1);

  port.close();
  assert.equal(up.signal.aborted, true);
  for (let i = 0; i < 20; i++) await settle();
  assert.equal(port.sent.length, 1);
});

test("名字不认识的端口不处理", () => {
  const port = fakePort("something-else");
  bg.handleStreamPort(port);
  assert.equal(port.receive, undefined);
});

// ---- 只注释生词端口 (bili-annotate-stream) ----

// 注释请求 (提示词里有【词表) 回一份 JSON, 其余的整段翻译请求走可以逐块推送的流.
function annotateUpstream(onAnnotate) {
  const up = liveUpstream();
  const streamRespond = respond;
  respond = (init) => {
    const body = JSON.parse(init.body);
    if (!/【词表/.test(body.messages[0].content)) return streamRespond(init);
    const content = onAnnotate(body);
    const reply = { choices: [{ message: { content }, finish_reason: "stop" }] };
    return new Response(JSON.stringify(reply), { status: 200, headers: { "content-type": "application/json" } });
  };
  return up;
}

// 提示词【词表】下面的 "编号. 词" 行, 回成 "编号. 词 => 释词".
function glossAll(body) {
  const lines = [];
  let inList = false;
  for (const line of body.messages[0].content.split("\n")) {
    if (line.startsWith("【词表")) inList = true;
    else if (inList) {
      const m = /^(\d+)\. (.+)$/.exec(line);
      if (!m) break;
      lines.push(m[1] + ". " + m[2] + " => 释" + m[2]);
    }
  }
  return lines.join("\n");
}

function withAnnotate(enabled, body) {
  return async () => {
    Object.assign(local, { annotateEnabled: enabled, annotateLevels: { zh: 4, en: 2, es: 2 } });
    bg.setAnnotateWordlists({ zh: new Map(), en: "the\nand\n", es: "el\nla\n" });
    try {
      await body();
    } finally {
      delete local.annotateEnabled;
      delete local.annotateLevels;
    }
  };
}

const NO_EXCLUDE = { zh: [], en: [], es: [] };
const HARD_WORDS = "ephemeral cacophony quixotic";

test("注释端口: BATCH_DONE 带完整的 notes (w / gloss / key / lang), 没有整段译文", withAnnotate(true, async () => {
  annotateUpstream(glossAll);
  const port = fakePort("bili-annotate-stream");
  bg.handleStreamPort(port);
  const text = "The " + HARD_WORDS + " and the " + unique("paragraph");
  port.receive({ type: "START_ANNOTATE_BATCH", items: [{ text, lang: "en" }], exclude: NO_EXCLUDE });
  await waitFor(port, 1);
  const done = port.sent[0];
  assert.equal(done.type, "BATCH_DONE");
  assert.equal(done.ok, true);
  assert.deepEqual(done.segments, {});
  assert.deepEqual(Object.keys(done.notes), ["0"]);
  const keys = done.notes[0].map((n) => n.key);
  for (const word of HARD_WORDS.split(" ")) assert.ok(keys.includes(word), word + " in " + keys);
  for (const n of done.notes[0]) {
    assert.deepEqual(Object.keys(n).sort(), ["gloss", "key", "lang", "w"]);
    assert.equal(n.lang, "en");
    assert.equal(n.gloss, "释" + n.w);
  }
}));

test("注释端口: 进度消息里没有 notes, 只有整段翻译的段落 (按原编号); notes 只在最终消息里", withAnnotate(true, async () => {
  const up = annotateUpstream(glossAll);
  const port = fakePort("bili-annotate-stream");
  bg.handleStreamPort(port);
  port.receive({
    type: "START_ANNOTATE_BATCH",
    items: [
      { text: "The " + HARD_WORDS + " and the " + unique("paragraph"), lang: "en" },
      { text: "これは日本語の文章です。" + unique("x"), lang: "ja" }
    ],
    exclude: NO_EXCLUDE
  });
  await up.opened();
  up.push(delta("译文"));
  await waitFor(port, 1);
  assert.equal(port.sent[0].type, "BATCH_DELTA");
  assert.deepEqual(port.sent[0].segments, { 1: "译文" });
  up.push("data: [DONE]\n\n");
  await waitFor(port, 2);
  for (const msg of port.sent.slice(0, -1)) {
    assert.equal(msg.type, "BATCH_DELTA");
    assert.equal("notes" in msg, false);
  }
  const done = port.sent[1];
  assert.equal(done.type, "BATCH_DONE");
  assert.deepEqual(done.segments, { 1: "译文" });
  assert.ok(done.notes[0].length >= 3);
  assert.equal("1" in done.notes, false);
}));

test("注释端口: 功能关闭时整段翻译, BATCH_DONE 没有 notes, 也不发注释请求", withAnnotate(false, async () => {
  let annotateRequests = 0;
  const up = annotateUpstream((body) => {
    annotateRequests++;
    return glossAll(body);
  });
  const port = fakePort("bili-annotate-stream");
  bg.handleStreamPort(port);
  port.receive({
    type: "START_ANNOTATE_BATCH",
    items: [{ text: "The " + HARD_WORDS + " and the " + unique("paragraph"), lang: "en" }],
    exclude: NO_EXCLUDE
  });
  await up.opened();
  up.push(delta("整段译文"));
  up.push("data: [DONE]\n\n");
  await waitFor(port, 2);
  assert.equal(port.sent[0].type, "BATCH_DELTA");
  const done = port.sent[1];
  assert.equal(done.type, "BATCH_DONE");
  assert.equal(done.ok, true);
  assert.deepEqual(done.segments, { 0: "整段译文" });
  assert.equal("notes" in done, false);
  assert.equal(annotateRequests, 0);
}));

test("注释端口: 读不到设置时也会发出最终消息", async () => {
  failSettings = true;
  try {
    const port = fakePort("bili-annotate-stream");
    bg.handleStreamPort(port);
    port.receive({ type: "START_ANNOTATE_BATCH", items: [{ text: "hello", lang: "en" }], exclude: NO_EXCLUDE });
    await waitFor(port, 1);
    assert.deepEqual(port.sent[0], { type: "BATCH_DONE", ok: false, status: 0, body: "storage unavailable" });
  } finally {
    failSettings = false;
  }
});
