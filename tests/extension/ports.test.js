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
