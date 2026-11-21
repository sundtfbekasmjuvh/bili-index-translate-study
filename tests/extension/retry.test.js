"use strict";

// 限流和临时服务器错误的自动重试: 流式, DeepLX, 等待时间, 取消, 以及不该重试的情形.
const test = require("node:test");
const assert = require("node:assert/strict");
const { storageArea } = require("../support/fake-storage");

const settings = { proxy: "" };
const local = { glossary: "" };
const calls = [];
let respond;

globalThis.chrome = { storage: { sync: storageArea(settings), local: storageArea(local) } };
globalThis.fetch = async (url, init) => {
  calls.push({ url: String(url), body: JSON.parse(init.body) });
  if (init.signal && init.signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
  return respond(init, calls.length);
};

const bg = require("../../bili-index-translate/background.js");

let seq = 0;
const unique = (label) => label + " " + ++seq;
const encoder = new TextEncoder();
const delta = (content) => "data: " + JSON.stringify({ choices: [{ delta: { content } }] }) + "\n\n";
const settle = () => new Promise((resolve) => setImmediate(resolve));
const settleAll = async () => {
  for (let i = 0; i < 5; i++) await settle();
};

function reset(overrides = {}) {
  calls.length = 0;
  for (const k of Object.keys(settings)) delete settings[k];
  Object.assign(settings, { proxy: "" }, overrides);
  local.glossary = "";
}

// 把等待缩到几毫秒, 测试结束后恢复原值.
function fastRetry(t) {
  const saved = { ...bg.RETRY_POLICY };
  Object.assign(bg.RETRY_POLICY, { baseDelayMs: 1, maxDelayMs: 5 });
  t.after(() => Object.assign(bg.RETRY_POLICY, saved));
}

function sse(chunks) {
  const body = new ReadableStream({
    start(controller) {
      for (const c of chunks) controller.enqueue(encoder.encode(c));
      controller.close();
    }
  });
  return new Response(body, { status: 200, headers: { "content-type": "text/event-stream" } });
}

const okStream = () => sse([delta("你好"), "data: [DONE]\n\n"]);
const failure = (status, headers = {}) => new Response("busy " + status, { status, headers });

function jsonResponse(payload, status = 200) {
  return new Response(JSON.stringify(payload), { status, headers: { "content-type": "application/json" } });
}

test("默认策略符合约定", () => {
  assert.deepEqual(bg.RETRY_POLICY, { maxRetries: 2, baseDelayMs: 500, maxDelayMs: 8000, statuses: [429, 500, 502, 503, 504] });
});

test("流式: 第一次 429, 第二次 200, 结果成功, fetch 两次", async (t) => {
  fastRetry(t);
  reset();
  respond = (init, n) => (n === 1 ? failure(429) : okStream());
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.equal(res.ok, true);
  assert.equal(res.text, "你好");
  assert.equal(calls.length, 2);
});

test("Retry-After: 2 被遵守, 等待结束前不发第二次请求", async (t) => {
  reset();
  t.mock.timers.enable({ apis: ["setTimeout"] });
  respond = (init, n) => (n === 1 ? failure(429, { "Retry-After": "2" }) : okStream());
  const pending = bg.streamTranslation(unique("hello"), () => {});
  await settleAll();
  assert.equal(calls.length, 1);
  t.mock.timers.tick(1999);
  await settleAll();
  assert.equal(calls.length, 1);
  t.mock.timers.tick(1);
  const res = await pending;
  assert.equal(calls.length, 2);
  assert.equal(res.ok, true);
});

test("Retry-After 为 HTTP 日期时按与当前时间之差等待", async (t) => {
  reset();
  t.mock.timers.enable({ apis: ["setTimeout"] });
  // HTTP 日期精确到秒, 所以实际等待在 4 到 5 秒之间.
  respond = (init, n) => (n === 1 ? failure(429, { "Retry-After": new Date(Date.now() + 5000).toUTCString() }) : okStream());
  const pending = bg.streamTranslation(unique("hello"), () => {});
  await settleAll();
  t.mock.timers.tick(3000);
  await settleAll();
  assert.equal(calls.length, 1);
  t.mock.timers.tick(2100);
  assert.equal((await pending).ok, true);
  assert.equal(calls.length, 2);
});

test("Retry-After 无法解析时当作没有这个头, 按指数退避 (首次不超过 600 毫秒)", async (t) => {
  reset();
  t.mock.timers.enable({ apis: ["setTimeout"] });
  respond = (init, n) => (n === 1 ? failure(429, { "Retry-After": "soon" }) : okStream());
  const pending = bg.streamTranslation(unique("hello"), () => {});
  await settleAll();
  assert.equal(calls.length, 1);
  t.mock.timers.tick(600);
  assert.equal((await pending).ok, true);
  assert.equal(calls.length, 2);
});

test("Retry-After: 1.5 等 1500 毫秒", async (t) => {
  reset();
  t.mock.timers.enable({ apis: ["setTimeout"] });
  respond = (init, n) => (n === 1 ? failure(429, { "Retry-After": "1.5" }) : okStream());
  const pending = bg.streamTranslation(unique("hello"), () => {});
  await settleAll();
  t.mock.timers.tick(1499);
  await settleAll();
  assert.equal(calls.length, 1);
  t.mock.timers.tick(1);
  assert.equal((await pending).ok, true);
  assert.equal(calls.length, 2);
});

test('Retry-After 为 "1,5" 或 "2;foo" 这类既不是数字也不含字母的值时, 当作没有这个头', async (t) => {
  for (const value of ["1,5", "2;foo"]) {
    reset();
    t.mock.timers.enable({ apis: ["setTimeout"] });
    respond = (init, n) => (n === 1 ? failure(429, { "Retry-After": value }) : okStream());
    const pending = bg.streamTranslation(unique("hello"), () => {});
    await settleAll();
    assert.equal(calls.length, 1);
    t.mock.timers.tick(600);
    assert.equal((await pending).ok, true, value);
    assert.equal(calls.length, 2);
    t.mock.timers.reset();
  }
});

test("Retry-After: 60 超过上限, 不重试, fetch 一次, 返回 429", async () => {
  reset();
  respond = () => failure(429, { "Retry-After": "60" });
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.deepEqual(res, { ok: false, status: 429, body: "busy 429", via: "direct" });
  assert.equal(calls.length, 1);
});

test("连续 3 次 503: fetch 正好 3 次, 返回 503", async (t) => {
  fastRetry(t);
  reset();
  respond = () => failure(503);
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.deepEqual(res, { ok: false, status: 503, body: "busy 503", via: "direct" });
  assert.equal(calls.length, 3);
});

test("400 和 401 不重试, fetch 各一次", async (t) => {
  fastRetry(t);
  for (const status of [400, 401]) {
    reset();
    respond = () => failure(status);
    const res = await bg.streamTranslation(unique("hello"), () => {});
    assert.equal(res.status, status);
    assert.equal(calls.length, 1);
  }
});

test("等待期间取消: 返回 cancelled, 之后没有新的 fetch", async (t) => {
  reset();
  t.mock.timers.enable({ apis: ["setTimeout"] });
  respond = () => failure(429);
  const ctrl = new AbortController();
  const pending = bg.streamTranslation(unique("hello"), () => {}, ctrl.signal);
  await settleAll();
  assert.equal(calls.length, 1);
  ctrl.abort();
  assert.deepEqual(await pending, { ok: false, cancelled: true, status: 0, body: "Cancelled" });
  t.mock.timers.tick(60000);
  await settleAll();
  assert.equal(calls.length, 1);
});

test("流已开始后出错 (200 加流内 error 事件) 不重试, fetch 一次", async (t) => {
  fastRetry(t);
  reset();
  respond = () => sse([delta("你"), 'data: {"error":"quota exceeded"}\n\n']);
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.equal(res.ok, false);
  assert.equal(res.body, "quota exceeded");
  assert.equal(calls.length, 1);
});

test("网络异常不重试", async (t) => {
  fastRetry(t);
  reset();
  respond = () => {
    throw new TypeError("fetch failed");
  };
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.deepEqual(res, { ok: false, status: 0, body: "fetch failed" });
  assert.equal(calls.length, 1);
});

test("DeepLX: 429 之后 200, 结果成功, fetch 两次", async (t) => {
  fastRetry(t);
  reset({ apiType: "deeplx", base: "https://deeplx.example.test/translate", target: "中文" });
  respond = (init, n) => (n === 1 ? jsonResponse({ code: 429 }, 429) : jsonResponse({ code: 200, data: "你好" }));
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.equal(res.ok, true);
  assert.equal(res.text, "你好");
  assert.equal(calls.length, 2);
});

test("自定义接口: 503 之后 200, 同一请求原样重发", async (t) => {
  fastRetry(t);
  reset({
    apiType: "custom",
    base: "https://custom.example.test/api",
    customMethod: "POST",
    customBody: '{"q": "{text}"}',
    customResultPath: "out"
  });
  respond = (init, n) => (n === 1 ? jsonResponse({ error: "x" }, 503) : jsonResponse({ out: "你好" }));
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.equal(res.ok, true);
  assert.equal(calls.length, 2);
  assert.deepEqual(calls[0], calls[1]);
});

test("非流式的设置页测试路径遇到 429: fetch 一次, 不重试", async (t) => {
  fastRetry(t);
  reset();
  respond = () => failure(429);
  const res = await bg.translate(unique("hello"));
  assert.equal(res.ok, false);
  assert.equal(res.status, 429);
  assert.equal(calls.length, 1);
});

test("逐条降级: 某一条持续 429 时循环停止, fetch 总次数有上界", async (t) => {
  fastRetry(t);
  // 自带换行的段落在 DeepLX 下逐条请求. 第 2 条持续 429: 第 1 条 1 次 + 第 2 条 3 次, 之后不再发.
  reset({ apiType: "deeplx", base: "https://deeplx.example.test/translate", target: "中文" });
  const [a, b, c, d] = [unique("one\nmore"), unique("two\nmore"), unique("three\nmore"), unique("four\nmore")];
  respond = (init) => {
    const text = JSON.parse(init.body).text;
    return text === b ? jsonResponse({ code: 429 }, 429) : jsonResponse({ code: 200, data: "译" + text });
  };
  const res = await bg.streamImmersiveBatch([a, b, c, d], "", () => {});
  assert.equal(res.ok, true);
  assert.deepEqual(Object.keys(res.segments), ["0"]);
  assert.deepEqual(calls.map((x) => x.body.text), [a, b, b, b]);

  // 全部持续 429: 总共 3 次 (maxRetries + 1), 返回 429.
  reset({ apiType: "deeplx", base: "https://deeplx.example.test/translate", target: "中文" });
  respond = () => jsonResponse({ code: 429 }, 429);
  const failed = await bg.streamImmersiveBatch([unique("x\nmore"), unique("y\nmore"), unique("z\nmore")], "", () => {});
  assert.equal(failed.ok, false);
  assert.equal(failed.status, 429);
  assert.equal(calls.length, 3);
});

test("逐条降级: 某一条持续 500 时重试用尽后继续发后面的条目, 后面的译文保留", async (t) => {
  fastRetry(t);
  reset({ apiType: "deeplx", base: "https://deeplx.example.test/translate", target: "中文" });
  const [a, b, c] = [unique("one\nmore"), unique("two\nmore"), unique("three\nmore")];
  respond = (init) => {
    const text = JSON.parse(init.body).text;
    return text === a ? jsonResponse({ code: 500 }, 500) : jsonResponse({ code: 200, data: "译" + text });
  };
  const res = await bg.streamImmersiveBatch([a, b, c], "", () => {});
  assert.equal(res.ok, true);
  assert.deepEqual(Object.keys(res.segments), ["1", "2"]);
  assert.deepEqual(calls.map((x) => x.body.text), [a, a, a, b, c]);
});
