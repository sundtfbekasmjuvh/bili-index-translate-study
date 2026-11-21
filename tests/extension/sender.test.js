"use strict";

// 来源校验: 每种消息只接受约定的来源 (扩展页面 / 标签页里的页面脚本), 端口只接受标签页里的页面脚本.
const test = require("node:test");
const assert = require("node:assert/strict");
const { storageArea } = require("../support/fake-storage");

const settings = { proxy: "" };
const local = { glossary: "" };
let reads = 0;
let writes = 0;
let fetches = 0;
let listener;
let connectListener;
let captured = [];

function counted(area) {
  for (const name of ["get", "set", "remove"]) {
    const fn = area[name];
    area[name] = (...args) => {
      if (name === "get") reads++;
      else writes++;
      return fn.apply(area, args);
    };
  }
  return area;
}

globalThis.chrome = {
  runtime: {
    id: "ext-id",
    getURL: (path) => "chrome-extension://ext-id/" + path,
    onMessage: { addListener: (fn) => (listener = fn) },
    onConnect: { addListener: (fn) => (connectListener = fn) },
    onInstalled: { addListener() {} }
  },
  storage: {
    sync: counted(storageArea(settings)),
    local: counted(storageArea(local)),
    onChanged: { addListener() {} }
  },
  tabs: {
    captureVisibleTab: async (windowId, opts) => {
      captured.push([windowId, opts]);
      return "data:image/png;base64,AAAA";
    }
  },
  action: { onClicked: { addListener() {} } },
  contextMenus: { removeAll() {}, create() {}, onClicked: { addListener() {} } },
  commands: { onCommand: { addListener() {} } }
};
globalThis.fetch = async () => {
  fetches++;
  return new Response(JSON.stringify({ choices: [{ message: { content: "你好" } }] }), {
    status: 200,
    headers: { "content-type": "application/json" }
  });
};

require("../../bili-index-translate/background.js");

const EXT_PAGE = { id: "ext-id", url: "chrome-extension://ext-id/options.html" };
const TAB_SCRIPT = { id: "ext-id", url: "https://www.bilibili.com/", tab: { id: 5, windowId: 9 } };
const FORBIDDEN = { ok: false, status: 0, body: "Forbidden sender" };

const EXT_ONLY = ["BILI_CHECK_API_CONFIG", "BILI_MIGRATE_SETTINGS"];
const TAB_ONLY = ["BILI_STUDY_CARD", "BILI_CAPTURE_TAB", "BILI_GET_IMMERSIVE_PREFS", "BILI_PURGE_VOLATILE"];
const BOTH = ["BILI_TRANSLATE"];

const makeMsg = (type) => ({ type, text: "hello " + type, settings: { uiLang: "en" } });

// 返回 { sync: 同步返回值, res: 响应 (被拒绝时同步得到, 否则等异步响应) }.
async function send(type, sender) {
  let resolve;
  const got = new Promise((r) => (resolve = r));
  let responded = false;
  const ret = listener(makeMsg(type), sender, (r) => {
    responded = true;
    resolve(r);
  });
  const syncResponded = responded;
  const res = await Promise.race([got, new Promise((r) => setTimeout(() => r("timeout"), 2000))]);
  return { ret, syncResponded, res };
}

function resetCounters() {
  reads = writes = fetches = 0;
  captured = [];
}

async function assertRejected(type, sender) {
  resetCounters();
  const out = await send(type, sender);
  assert.equal(out.syncResponded, true, type + " 应同步回复");
  assert.equal(out.ret, false, type);
  assert.deepEqual(out.res, FORBIDDEN, type);
  assert.deepEqual([reads, writes, fetches, captured.length], [0, 0, 0, 0], type + " 不应有副作用");
}

async function assertAccepted(type, sender) {
  resetCounters();
  const out = await send(type, sender);
  assert.notEqual(out.res, "timeout", type);
  assert.notDeepEqual(out.res, FORBIDDEN, type);
}

test("只允许扩展页面的消息: 扩展页面照常处理, 标签页里的页面脚本被拒绝且没有副作用", async () => {
  for (const type of EXT_ONLY) {
    await assertAccepted(type, EXT_PAGE);
    await assertRejected(type, TAB_SCRIPT);
  }
  // 照常处理的确产生了效果.
  resetCounters();
  await send("BILI_MIGRATE_SETTINGS", EXT_PAGE);
  assert.ok(reads > 0);
});

test("只允许标签页的消息: 标签页照常处理, 没有 tab 的扩展页面被拒绝", async () => {
  for (const type of TAB_ONLY) {
    await assertAccepted(type, TAB_SCRIPT);
    await assertRejected(type, EXT_PAGE);
  }
  await assertRejected("BILI_CAPTURE_TAB", { id: "ext-id", url: "https://x/", tab: {} });
});

test("BILI_TRANSLATE: 扩展页面和标签页都接受", async () => {
  for (const type of BOTH) {
    for (const sender of [EXT_PAGE, TAB_SCRIPT]) {
      resetCounters();
      const out = await send(type, sender);
      assert.equal(out.res.ok, true, JSON.stringify(out.res));
      assert.ok(fetches >= 1 || out.res.cached);
    }
  }
});

test("sender.id 不是本扩展时, 任何一种消息都被拒绝", async () => {
  const foreignPage = { id: "other-id", url: "chrome-extension://ext-id/options.html" };
  const foreignTab = { id: "other-id", url: "https://www.bilibili.com/", tab: { id: 5, windowId: 9 } };
  const noId = { url: "chrome-extension://ext-id/options.html", tab: { id: 5, windowId: 9 } };
  const wrongUrl = { id: "ext-id", url: "https://evil.example/" };
  for (const type of [...EXT_ONLY, ...TAB_ONLY, ...BOTH]) {
    for (const sender of [foreignPage, foreignTab, noId, undefined]) await assertRejected(type, sender);
  }
  for (const type of [...EXT_ONLY]) await assertRejected(type, wrongUrl);
});

test("不在表里的消息类型保持原行为 (不新增拒绝)", async () => {
  let responded = false;
  const ret = listener({ type: "SOMETHING_ELSE" }, undefined, () => (responded = true));
  assert.equal(ret, false);
  assert.equal(responded, false);
});

test("BILI_CAPTURE_TAB 用的是 sender.tab.windowId", async () => {
  resetCounters();
  const out = await send("BILI_CAPTURE_TAB", TAB_SCRIPT);
  assert.deepEqual(out.res, { ok: true, dataUrl: "data:image/png;base64,AAAA" });
  assert.deepEqual(captured, [[9, { format: "png" }]]);
});

function port(sender) {
  const p = {
    name: "bili-translate-stream",
    sender,
    sent: [],
    disconnected: false,
    onMessage: { addListener: (fn) => (p.receive = fn) },
    onDisconnect: { addListener: (fn) => (p.close = fn) },
    postMessage: (m) => p.sent.push(m),
    disconnect: () => (p.disconnected = true)
  };
  return p;
}

test("端口: 没有 tab 或 sender.id 不对时被断开, 之后的开始消息不触发任何请求", async () => {
  for (const sender of [EXT_PAGE, { ...TAB_SCRIPT, id: "other-id" }, { id: "ext-id", tab: {} }, undefined]) {
    resetCounters();
    const p = port(sender);
    connectListener(p);
    assert.equal(p.disconnected, true);
    assert.equal(p.receive, undefined);
    assert.equal(p.close, undefined);
    if (p.receive) p.receive({ type: "START_TRANSLATE_STREAM", text: "hi" });
    await new Promise((r) => setTimeout(r, 20));
    assert.deepEqual([reads, writes, fetches, p.sent.length], [0, 0, 0, 0]);
  }
});

test("端口: 来自标签页的端口照常工作", async () => {
  resetCounters();
  const p = port(TAB_SCRIPT);
  connectListener(p);
  assert.equal(p.disconnected, false);
  p.receive({ type: "START_TRANSLATE_STREAM", text: "tab port hello" });
  for (let i = 0; i < 200 && !p.sent.some((m) => m.type === "DONE"); i++) await new Promise((r) => setTimeout(r, 5));
  assert.ok(p.sent.some((m) => m.type === "DONE" && m.ok === true), JSON.stringify(p.sent));
  assert.ok(fetches >= 1);
});

// ---- 只注释生词: 新消息和新端口 ----

const ANNOTATE_EXT_ONLY = ["BILI_ANNOTATE_REMOVE", "BILI_ANNOTATE_CLEAR", "BILI_ANNOTATE_LIST"];
const ANNOTATE_TAB_ONLY = ["BILI_ANNOTATE_MARK"];

test("注释消息: CLEAR 和 LIST 只接受扩展页面, MARK 只接受标签页, 被拒绝时没有副作用", async () => {
  for (const type of ANNOTATE_EXT_ONLY) {
    await assertAccepted(type, EXT_PAGE);
    await assertRejected(type, TAB_SCRIPT);
  }
  for (const type of ANNOTATE_TAB_ONLY) {
    await assertAccepted(type, TAB_SCRIPT);
    await assertRejected(type, EXT_PAGE);
  }
});

test("注释消息: sender.id 不是本扩展时被拒绝", async () => {
  const foreignPage = { id: "other-id", url: "chrome-extension://ext-id/options.html" };
  const foreignTab = { id: "other-id", url: "https://www.bilibili.com/", tab: { id: 5, windowId: 9 } };
  const noId = { url: "chrome-extension://ext-id/options.html", tab: { id: 5, windowId: 9 } };
  for (const type of [...ANNOTATE_EXT_ONLY, ...ANNOTATE_TAB_ONLY]) {
    for (const sender of [foreignPage, foreignTab, noId, undefined]) await assertRejected(type, sender);
  }
  await assertRejected("BILI_ANNOTATE_CLEAR", { id: "ext-id", url: "https://evil.example/" });
  await assertRejected("BILI_ANNOTATE_MARK", { id: "ext-id", url: "https://x/", tab: {} });
});

test("bili-annotate-stream 端口: 非标签页的来源被断开, 标签页照常工作", async () => {
  const annotatePort = (sender) => {
    const p = {
      name: "bili-annotate-stream",
      sender,
      sent: [],
      disconnected: false,
      onMessage: { addListener: (fn) => (p.receive = fn) },
      onDisconnect: { addListener: (fn) => (p.close = fn) },
      postMessage: (m) => p.sent.push(m),
      disconnect: () => (p.disconnected = true)
    };
    return p;
  };
  for (const sender of [EXT_PAGE, { ...TAB_SCRIPT, id: "other-id" }, { id: "ext-id", tab: {} }, undefined]) {
    resetCounters();
    const p = annotatePort(sender);
    connectListener(p);
    assert.equal(p.disconnected, true);
    assert.equal(p.receive, undefined);
    await new Promise((r) => setTimeout(r, 20));
    assert.deepEqual([reads, writes, fetches, p.sent.length], [0, 0, 0, 0]);
  }

  resetCounters();
  const p = annotatePort(TAB_SCRIPT);
  connectListener(p);
  assert.equal(p.disconnected, false);
  p.receive({ type: "START_ANNOTATE_BATCH", items: [{ text: "hello annotate port", lang: "en" }], exclude: { zh: [], en: [], es: [] } });
  for (let i = 0; i < 200 && !p.sent.some((m) => m.type === "BATCH_DONE"); i++) await new Promise((r) => setTimeout(r, 5));
  assert.ok(p.sent.some((m) => m.type === "BATCH_DONE" && m.ok === true), JSON.stringify(p.sent));
  assert.ok(fetches >= 1);
});
