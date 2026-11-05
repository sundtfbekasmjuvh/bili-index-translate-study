"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");

const BASE = "https://index-translate.bilibili.com/v1";
const RELAY = "https://relay.example.test/https://index-translate.bilibili.com/v1";
const IMAGE = "data:image/png;base64,iVBORw0KGgo=";

const settings = { proxy: "" };
const calls = [];
let respond;

globalThis.chrome = {
  storage: {
    sync: { get: async (defaults) => ({ ...defaults, ...settings }) },
    local: { get: async (defaults) => ({ ...defaults }) }
  }
};
globalThis.fetch = async (url, init) => {
  calls.push({ url: String(url), body: JSON.parse(init.body) });
  return respond();
};

const bg = require("../../bili-index-translate/background.js");

let seq = 0;
const unique = (label) => label + " " + ++seq;

function reset(proxy) {
  calls.length = 0;
  settings.proxy = proxy;
  respond = () => new Response("<html>blocked</html>", { status: 412, headers: { "content-type": "text/html" } });
}

const routes = [
  ["callChatCompletion", () => bg.callChatCompletion([{ role: "user", content: unique("hello") }], { base: BASE, proxy: settings.proxy, key: "bili", model: "Index-Translate-9B" })],
  ["streamTranslation", () => bg.streamTranslation(unique("hello"), () => {})],
  ["streamVisionTranslation", () => bg.streamVisionTranslation(IMAGE + ++seq, "", () => {})],
  ["streamImmersiveBatch", () => bg.streamImmersiveBatch([unique("first"), unique("second")], "", () => {})],
  ["streamStudyCard", () => bg.streamStudyCard(unique("hello"), "", () => {})]
];

for (const [name, run] of routes) {
  test(name + ": 直连 412 只发一次请求, 不自动改走中继", async () => {
    reset("");
    const res = await run();
    assert.equal(res.ok, false);
    assert.equal(res.status, 412);
    assert.equal(res.via, "direct");
    assert.match(res.body, /blocked/);
    assert.deepEqual(calls.map((c) => c.url), [BASE + "/chat/completions"]);
  });

  test(name + ": 显式配置的中继只请求中继地址一次", async () => {
    reset(RELAY + "/");
    const res = await run();
    assert.equal(res.ok, false);
    assert.equal(res.status, 412);
    assert.equal(res.via, "relay");
    assert.deepEqual(calls.map((c) => c.url), [RELAY + "/chat/completions"]);
  });
}

test("streamTranslation 解析 SSE 增量, 去掉思维链, 并按原文缓存", async () => {
  reset("");
  const sse = [
    'data: {"choices":[{"delta":{"content":"<think>x</think>你"}}]}',
    'data: {"choices":[{"delta":{"content":"好"}}]}',
    "data: [DONE]",
    ""
  ].join("\n");
  respond = () => new Response(sse, { status: 200, headers: { "content-type": "text/event-stream" } });

  const deltas = [];
  const text = unique("hello");
  const res = await bg.streamTranslation(text, (d) => deltas.push(d.text));
  assert.equal(res.ok, true);
  assert.equal(res.text, "你好");
  assert.equal(res.via, "direct");
  assert.equal(deltas.at(-1), "你好");
  assert.equal(calls.length, 1);
  assert.equal(calls[0].body.stream, true);

  const again = await bg.streamTranslation(text, () => {});
  assert.equal(again.cached, true);
  assert.equal(calls.length, 1);
});