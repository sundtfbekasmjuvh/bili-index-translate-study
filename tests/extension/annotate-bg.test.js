"use strict";

// "只注释生词"的 background 接线: 可用条件, 挑词后的请求 (合并 / 逐段 / 整段翻译) 与退回, 缓存, 已会词过滤,
// 标记 / 清空 / 列表消息, 串行写入, 存储形状, storage.onChanged 不误清缓存.
const test = require("node:test");
const assert = require("node:assert/strict");
const { storageArea } = require("../support/fake-storage");

const settings = {};
const local = {};
const calls = [];
const hooks = {};
let chatHandler;
let wordlistHandler = null;
let storageDelay = false;
let failSets = 0;

const tick = () => new Promise((resolve) => setTimeout(resolve, 1));
const localArea = storageArea(local);
const rawGet = localArea.get;
const rawSet = localArea.set;
const getLog = []; // 每次读 local 时请求的键名
localArea.get = async (keys) => {
  getLog.push(keys && typeof keys === "object" && !Array.isArray(keys) ? Object.keys(keys) : [].concat(keys));
  if (storageDelay) await tick();
  return rawGet(keys);
};
localArea.set = async (items) => {
  if (failSets > 0) {
    failSets--;
    throw new Error("quota exceeded");
  }
  if (storageDelay) await tick();
  return rawSet(items);
};

globalThis.chrome = {
  runtime: {
    id: "ext-id",
    getURL: (path) => "chrome-extension://ext-id/" + path,
    onMessage: { addListener: (fn) => (hooks.message = fn) },
    onConnect: { addListener: (fn) => (hooks.connect = fn) },
    onInstalled: { addListener() {} }
  },
  storage: {
    sync: storageArea(settings),
    local: localArea,
    onChanged: { addListener: (fn) => (hooks.changed = fn) }
  },
  tabs: {},
  action: { onClicked: { addListener() {} } },
  contextMenus: { removeAll() {}, create() {}, onClicked: { addListener() {} } },
  commands: { onCommand: { addListener() {} } }
};

globalThis.fetch = async (url, init) => {
  const u = String(url);
  if (u.startsWith("chrome-extension://")) return wordlistHandler(u);
  const body = JSON.parse(init.body);
  calls.push({ url: u, body });
  if (init.signal && init.signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
  const out = await chatHandler(body, calls.length);
  if (out instanceof Response) return out;
  const spec = typeof out === "string" ? { content: out } : out;
  return new Response(
    JSON.stringify({ choices: [{ message: { content: spec.content }, finish_reason: spec.finish_reason || "stop" }] }),
    { status: 200, headers: { "content-type": "application/json" } }
  );
};

const bg = require("../../bili-index-translate/background.js");

const TAB = { id: "ext-id", url: "https://www.bilibili.com/", tab: { id: 5, windowId: 9 } };
const EXT = { id: "ext-id", url: "chrome-extension://ext-id/options.html" };
const FORBIDDEN = { ok: false, status: 0, body: "Forbidden sender" };
const OFFICIAL = "https://index-translate.bilibili.com/v1";

// 小词表: en 排名 = 行号, 不在表里的词一律算生词.
const EN_LIST = "the\nand\nwith\nwere\nanother\nthings\nrare\nvery\nsame\n";
const ES_LIST = "el\nla\nde\ny\n";
const tables = () => ({ zh: new Map([["我们", 1]]), en: EN_LIST, es: ES_LIST });

const A = "The ephemeral cacophony and serendipity were rare."; // 生词: ephemeral cacophony serendipity
const B = "Another quixotic zeitgeist with ephemeral things."; // 生词: quixotic zeitgeist ephemeral
const C = "The very rare things were the same."; // 没有生词
const JA = "これは日本語の文章です。";
const ZH = "我们讨论了量子纠缠和区块链";
const P8 = [
  "zorblax quenzel fribbit glorpnax snivvle trundox wibbler yarplon",
  "plonkus vrindle crazzik duffelt hobnixy jorvane kelmora lumbrix",
  "mintrel nopsiel orvanth pelvick quorbat rindal sulvane tarvix",
  "ulmoran vexling wuzzlet xandrel yobbick zumthor aldrenic bexholt"
];
const en = (text) => ({ text, lang: "en" });

function promptWords(content) {
  const words = [];
  let inList = false;
  for (const line of String(content).split("\n")) {
    if (line.startsWith("【词表")) {
      inList = true;
      continue;
    }
    if (!inList) continue;
    const m = /^(\d+)\. (.+)$/.exec(line);
    if (m) words.push([Number(m[1]), m[2]]);
    else inList = false;
  }
  return words;
}
const isAnnotate = (body) => /【词表/.test(body.messages[0].content);
const goodReply = (body) =>
  promptWords(body.messages[0].content)
    .map(([n, w]) => n + ". " + w + " => 释" + w)
    .join("\n");

// 整段翻译请求 (Index 提示词): 〖Pk〗译:原文; 单段没有标记.
function translateReply(body) {
  const content = body.messages[body.messages.length - 1].content;
  const m = /【源文】\n([\s\S]*?)\n\n【约束要求】/.exec(content);
  const src = m ? m[1] : content.split("\n\n").slice(1).join("\n\n");
  if (!/〖P0〗/.test(src)) return "译:" + src;
  return src
    .split("\n")
    .map((line) => line.replace(/^(〖P\d+〗)/, "$1译:"))
    .join("\n");
}
const defaultHandler = (body) => (isAnnotate(body) ? goodReply(body) : translateReply(body));

const annotateCalls = () => calls.filter((c) => isAnnotate(c.body));
const translateCalls = () => calls.filter((c) => !isAnnotate(c.body));

function send(type, fields, sender) {
  return new Promise((resolve) => {
    const ret = hooks.message({ type, ...fields }, sender, resolve);
    if (ret === false) return; // 已同步回复
  });
}
const mark = (lang, key, known, sender = TAB) => send("BILI_ANNOTATE_MARK", { lang, key, known }, sender);

async function reset() {
  calls.length = 0;
  for (const key of Object.keys(settings)) delete settings[key];
  for (const key of Object.keys(local)) delete local[key];
  local.annotateEnabled = true;
  local.annotateLevels = { zh: 4, en: 20, es: 20 };
  storageDelay = false;
  failSets = 0;
  wordlistHandler = null;
  chatHandler = defaultHandler;
  bg.setAnnotateWordlists(tables());
  await send("BILI_PURGE_VOLATILE", {}, TAB);
}

const noteKeys = (res, i) => res.notes[i].map((n) => n.key);

test("annotateAvailable: 开关, DeepLX / 自定义接口, 释义语言同时满足才可用", () => {
  const on = { annotateEnabled: true };
  assert.equal(bg.annotateAvailable({ target: "中文" }, on), true);
  assert.equal(bg.annotateAvailable({ target: "English" }, on), true);
  assert.equal(bg.annotateAvailable({ target: "中文" }, { annotateEnabled: false }), false);
  assert.equal(bg.annotateAvailable({ target: "中文" }, null), false);
  assert.equal(bg.annotateAvailable({ target: "中文", apiType: "deeplx", base: "https://x/translate" }, on), false);
  assert.equal(bg.annotateAvailable({ target: "中文", apiType: "custom", base: "https://x/api" }, on), false);
  assert.equal(bg.annotateAvailable({ target: "Français" }, on), false);
  assert.equal(bg.annotateAvailable({ target: "繁體中文" }, on), false);
  assert.equal(bg.annotateAvailable({ target: "" }, on), false);
  // 任何 OpenAI 兼容地址都可以.
  assert.equal(bg.annotateAvailable({ target: "中文", base: "https://api.example.test/v1", model: "gpt-4o" }, on), true);
});

test("annotateModelFor: 官方地址固定 9B, 其他地址用 s.model", () => {
  assert.equal(bg.annotateModelFor({ base: OFFICIAL, model: "gpt-4o" }), "Index-Translate-9B");
  assert.equal(bg.annotateModelFor({ base: OFFICIAL + "/", model: "Index-Translate-2B" }), "Index-Translate-9B");
  assert.equal(bg.annotateModelFor({ base: "https://api.example.test/v1", model: "my-model" }), "my-model");
  assert.equal(bg.annotateModelFor({ base: "not a url", model: "my-model" }), "my-model");
});

test("getAnnotateConfig: 缺键用默认值, 类型不对和旧格式被容忍", async () => {
  await reset();
  for (const key of Object.keys(local)) delete local[key];
  const def = await bg.getAnnotateConfig();
  assert.deepEqual(def, {
    annotateEnabled: false,
    annotateLevels: { zh: 4, en: 5000, es: 5000 },
    annotateKnown: { v: 1, zh: [], en: [], es: [] }
  });
  assert.deepEqual(bg.ANNOTATE_DEFAULTS.annotateKnown, { v: 1, zh: [], en: [], es: [] });

  local.annotateEnabled = "yes";
  local.annotateLevels = { zh: "x", en: -5, es: 1e9 };
  local.annotateKnown = {
    zh: "nope",
    en: ["ok", "", 5, "a\nb", "x".repeat(33), "ok", "y".repeat(32), "Upper", "two words", "w1", "https://a.example/?t=1", "don't"],
    es: ["  ", "palabra", "Casa", "año"]
  };
  const bad = await bg.getAnnotateConfig();
  assert.equal(bad.annotateEnabled, false);
  assert.deepEqual(bad.annotateLevels, { zh: 4, en: 1, es: 50000 });
  assert.deepEqual(bad.annotateKnown, { v: 1, zh: [], en: ["ok", "y".repeat(32), "don't"], es: ["palabra", "año"] });
  local.annotateKnown = { zh: ["量子", "abc", "量子1", "量 子", "区块链"], en: [], es: [] };
  assert.deepEqual((await bg.getAnnotateConfig()).annotateKnown.zh, ["量子", "区块链"]);

  for (const old of [["a", "b"], "text", 7, null]) {
    local.annotateKnown = old;
    assert.deepEqual((await bg.getAnnotateConfig()).annotateKnown, { v: 1, zh: [], en: [], es: [] });
  }
  local.annotateLevels = "high";
  assert.deepEqual((await bg.getAnnotateConfig()).annotateLevels, { zh: 4, en: 5000, es: 5000 });
});

test("不可用 (开关关闭 / 目标语言不支持) 时整批交给整段翻译, 结果里没有 notes", async () => {
  for (const setup of [() => (local.annotateEnabled = false), () => (settings.target = "Français")]) {
    await reset();
    setup();
    const res = await bg.streamAnnotateBatch([en(A), en(B)], {}, null);
    assert.equal(res.ok, true);
    assert.equal("notes" in res, false);
    assert.deepEqual(Object.keys(res.segments), ["0", "1"]);
    assert.equal(annotateCalls().length, 0);
    assert.equal(translateCalls().length, 1);
  }
});

test("日文段落走整段翻译, 其余段落照常注释", async () => {
  await reset();
  const res = await bg.streamAnnotateBatch([en(A), { text: JA, lang: "ja" }], {}, null);
  assert.equal(res.ok, true);
  assert.deepEqual(noteKeys(res, 0), ["ephemeral", "cacophony", "serendipity"]);
  assert.equal("1" in res.notes, false);
  assert.equal(res.segments[1], "译:" + JA);
  assert.equal("0" in res.segments, false);
  assert.equal(annotateCalls().length, 1);
  assert.equal(translateCalls().length, 1);
  assert.match(translateCalls()[0].body.messages[0].content, /これは日本語/);
  assert.doesNotMatch(translateCalls()[0].body.messages[0].content, /ephemeral/);
});

test("语言判断不出来的段落 (没有字母, 没有 lang 属性) 也整段翻译", async () => {
  await reset();
  const res = await bg.streamAnnotateBatch([{ text: "12345 !!!", lang: "" }], {}, null);
  assert.equal(res.ok, true);
  assert.equal(res.segments[0], "译:12345 !!!");
  assert.deepEqual(res.notes, {});
  assert.equal(annotateCalls().length, 0);
});

test("中文段落: 用 HSK 表挑词, 注释带 lang: zh", async () => {
  await reset();
  const res = await bg.streamAnnotateBatch([{ text: ZH, lang: "zh-CN" }], {}, null);
  assert.equal(res.ok, true);
  const keys = noteKeys(res, 0);
  assert.ok(keys.includes("量子") && keys.includes("纠缠"), keys.join());
  assert.ok(!keys.includes("我们"));
  assert.ok(res.notes[0].every((n) => n.lang === "zh" && n.gloss === "释" + n.w));
});

test("没有候选词的段落不发请求, notes 为空数组", async () => {
  await reset();
  const res = await bg.streamAnnotateBatch([en(C)], {}, null);
  assert.deepEqual(res, { ok: true, segments: {}, notes: { 0: [] }, model: "Index-Translate-9B" });
  assert.equal(calls.length, 0);
});

test("合并请求: 一次发出, 请求参数正确, 整页去重, 每段只出现在 notes", async () => {
  await reset();
  const res = await bg.streamAnnotateBatch([en(A), en(B), en(C)], {}, null);
  assert.equal(res.ok, true);
  assert.equal(calls.length, 1);
  const { body, url } = calls[0];
  assert.equal(url, OFFICIAL + "/chat/completions");
  assert.equal(body.model, "Index-Translate-9B");
  assert.equal(body.temperature, 0);
  assert.equal(body.stream, false);
  assert.equal(body.max_tokens, 1500);
  assert.deepEqual(body.chat_template_kwargs, { enable_thinking: false });
  assert.deepEqual(promptWords(body.messages[0].content).map((x) => x[1]), [
    "ephemeral", "cacophony", "serendipity", "quixotic", "zeitgeist"
  ]);
  // 第二段的 ephemeral 在第一段已经注过.
  assert.deepEqual(noteKeys(res, 0), ["ephemeral", "cacophony", "serendipity"]);
  assert.deepEqual(noteKeys(res, 1), ["quixotic", "zeitgeist"]);
  assert.deepEqual(res.notes[2], []);
  assert.deepEqual(res.notes[0][0], { w: "ephemeral", gloss: "释ephemeral", key: "ephemeral", lang: "en" });
  assert.deepEqual(res.segments, {});
  assert.equal(res.model, "Index-Translate-9B");
});

test("单段请求: A3, max_tokens 600; exclude 里的 key 不再挑", async () => {
  await reset();
  const res = await bg.streamAnnotateBatch([en(B)], { en: ["quixotic", "ephemeral"], zh: null }, null);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].body.max_tokens, 600);
  assert.deepEqual(noteKeys(res, 0), ["zeitgeist"]);
  // 提示词里只有一个词表.
  assert.doesNotMatch(calls[0].body.messages[0].content, /【词表1】/);
});

test("词数超过 24 时按顺序切成多组, 组内合并, 落单的用单段请求", async () => {
  await reset();
  const res = await bg.streamAnnotateBatch(P8.map(en), {}, null);
  assert.equal(res.ok, true);
  assert.equal(calls.length, 2);
  assert.equal(promptWords(calls[0].body.messages[0].content).length, 24);
  assert.equal(calls[0].body.max_tokens, 1500);
  assert.equal(promptWords(calls[1].body.messages[0].content).length, 8);
  assert.equal(calls[1].body.max_tokens, 600);
  for (let i = 0; i < 4; i++) assert.equal(res.notes[i].length, 8);
});

test("合并请求格式不合格: 退回逐段请求", async () => {
  await reset();
  chatHandler = (body, n) => (n === 1 ? "sorry, cannot do that" : goodReply(body));
  const res = await bg.streamAnnotateBatch([en(A), en(B)], {}, null);
  assert.equal(res.ok, true);
  assert.deepEqual(calls.map((c) => c.body.max_tokens), [1500, 600, 600]);
  assert.deepEqual(noteKeys(res, 0), ["ephemeral", "cacophony", "serendipity"]);
  assert.deepEqual(noteKeys(res, 1), ["quixotic", "zeitgeist"]);
  assert.deepEqual(res.segments, {});
});

test("单段格式不合格 (或 finish_reason 是 length): 这一段交给整段翻译", async () => {
  await reset();
  chatHandler = (body, n) => (isAnnotate(body) ? (n === 1 ? "garbage" : goodReply(body)) : translateReply(body));
  let res = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(res.ok, true);
  assert.deepEqual(res.notes, {});
  assert.equal(res.segments[0], "译:" + A);
  assert.equal(annotateCalls().length, 1);
  assert.equal(translateCalls().length, 1);

  // 合并失败, 第一段单段也失败, 第二段单段成功.
  await reset();
  chatHandler = (body, n) => (isAnnotate(body) ? (n <= 2 ? "garbage" : goodReply(body)) : translateReply(body));
  res = await bg.streamAnnotateBatch([en(A), en(B)], {}, null);
  assert.equal(res.ok, true);
  assert.deepEqual(Object.keys(res.segments), ["0"]);
  assert.deepEqual(Object.keys(res.notes), ["1"]);
  // 第一段虽然退回整段翻译, 它挑过的 ephemeral 仍算本页已排除 (整页去重在发请求前做).
  assert.deepEqual(noteKeys(res, 1), ["quixotic", "zeitgeist"]);

  await reset();
  chatHandler = (body) => (isAnnotate(body) ? { content: goodReply(body), finish_reason: "length" } : translateReply(body));
  res = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(res.segments[0], "译:" + A);
  assert.deepEqual(res.notes, {});
});

test("整段翻译的进度按原来的序号送出, 进度里没有 notes", async () => {
  await reset();
  const progress = [];
  chatHandler = (body) => {
    if (isAnnotate(body)) return goodReply(body);
    const chunk = (content) => "data: " + JSON.stringify({ choices: [{ delta: { content } }] }) + "\n\n";
    return new Response(chunk("译:") + chunk("日文") + "data: [DONE]\n\n", {
      status: 200,
      headers: { "content-type": "text/event-stream" }
    });
  };
  const res = await bg.streamAnnotateBatch([en(C), { text: JA, lang: "ja" }, en(C)], {}, (p) => progress.push(p));
  assert.equal(res.segments[1], "译:日文");
  assert.ok(progress.length > 0);
  assert.ok(progress.every((p) => !("notes" in p)));
  assert.ok(progress.every((p) => Object.keys(p.segments).every((k) => k === "1")));
  assert.equal(progress[progress.length - 1].segments[1], "译:日文");
});

test("HTTP 错误和网络错误不逐级退回: 整批失败; 限流按 RETRY_POLICY 重试, 但不改发逐段请求", async () => {
  const savedDelay = bg.RETRY_POLICY.baseDelayMs;
  bg.RETRY_POLICY.baseDelayMs = 1;
  await reset();
  chatHandler = () => new Response("slow down", { status: 429 });
  let res;
  try {
    res = await bg.streamAnnotateBatch([en(A), en(B)], {}, null);
  } finally {
    bg.RETRY_POLICY.baseDelayMs = savedDelay;
  }
  assert.equal(res.ok, false);
  assert.equal(res.status, 429);
  // 同一个合并请求: 第一次加上重试次数, 每次内容相同; 没有退回成两段各自请求.
  assert.equal(calls.length, 1 + bg.RETRY_POLICY.maxRetries);
  assert.ok(calls.every((c) => c.body.messages[0].content === calls[0].body.messages[0].content));
  assert.match(calls[0].body.messages[0].content, /【词表2】/);

  await reset();
  chatHandler = () => {
    throw new TypeError("network down");
  };
  res = await bg.streamAnnotateBatch([en(A), en(B)], {}, null);
  assert.equal(res.ok, false);
  assert.equal(res.status, 0);
  assert.match(res.body, /network down/);
  assert.equal(calls.length, 1);

  // 整段翻译那部分的 HTTP 错误同样整批失败.
  await reset();
  chatHandler = (body) => (isAnnotate(body) ? goodReply(body) : new Response("bad", { status: 500 }));
  res = await bg.streamAnnotateBatch([en(A), { text: JA, lang: "ja" }], {}, null);
  assert.equal(res.ok, false);
  assert.equal(res.status, 500);
});

test("限流一次后重试成功: 照常出注释", async () => {
  const savedDelay = bg.RETRY_POLICY.baseDelayMs;
  bg.RETRY_POLICY.baseDelayMs = 1;
  await reset();
  chatHandler = (body, n) => (n === 1 ? new Response("slow down", { status: 429 }) : defaultHandler(body));
  let res;
  try {
    res = await bg.streamAnnotateBatch([en(A)], {}, null);
  } finally {
    bg.RETRY_POLICY.baseDelayMs = savedDelay;
  }
  assert.equal(res.ok, true);
  assert.equal(calls.length, 2);
  assert.deepEqual(noteKeys(res, 0).sort(), ["cacophony", "ephemeral", "serendipity"]);
});

test("signal 中止: 立即停止, 不再发后续请求", async () => {
  await reset();
  const ctrl = new AbortController();
  ctrl.abort();
  let res = await bg.streamAnnotateBatch(P8.map(en), {}, null, ctrl.signal);
  assert.equal(res.ok, false);
  assert.equal(res.cancelled, true);
  assert.equal(calls.length, 0);

  await reset();
  const mid = new AbortController();
  chatHandler = (body) => {
    mid.abort(); // 第一组请求进行中中止
    return goodReply(body);
  };
  res = await bg.streamAnnotateBatch(P8.map(en), {}, null, mid.signal);
  assert.equal(res.cancelled, true);
  assert.equal(calls.length, 1);
});

test("callChatCompletion: 返回 finishReason, 可选 signal; 老的调用写法不变", async () => {
  await reset();
  chatHandler = () => ({ content: "hi", finish_reason: "length" });
  const s = { base: OFFICIAL, proxy: "", key: "k", model: "Index-Translate-9B" };
  const r = await bg.callChatCompletion([{ role: "user", content: "x" }], s);
  assert.equal(r.ok, true);
  assert.equal(r.finishReason, "length");
  assert.equal(r.content, "hi");
  const ctrl = new AbortController();
  ctrl.abort();
  await assert.rejects(bg.callChatCompletion([{ role: "user", content: "x" }], s, 0, "", 10, { signal: ctrl.signal }), /Cancelled/);
});

test("缓存: 全部命中不再请求, 部分命中重新请求并合并, 清空消息一起清", async () => {
  await reset();
  await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(calls.length, 1);
  const again = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(calls.length, 1);
  assert.deepEqual(noteKeys(again, 0), ["ephemeral", "cacophony", "serendipity"]);
  assert.equal(again.notes[0][0].gloss, "释ephemeral");

  // 部分命中: 先把 cacophony 标成已会, 缓存里就没有它; 取消后这次挑出的词不全在缓存里, 要重新请求.
  await reset();
  local.annotateKnown = { v: 1, zh: [], en: ["cacophony"], es: [] };
  await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(calls.length, 1);
  assert.deepEqual(promptWords(calls[0].body.messages[0].content).map((x) => x[1]), ["ephemeral", "serendipity"]);
  local.annotateKnown = { v: 1, zh: [], en: [], es: [] };
  await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(calls.length, 2);
  assert.equal(promptWords(calls[1].body.messages[0].content).length, 3);
  const [entry] = [...bg.volatileAnnotateCache.values()];
  assert.deepEqual(Object.keys(entry).sort(), ["cacophony", "ephemeral", "serendipity"]);
  assert.deepEqual(entry.ephemeral, { w: "ephemeral", gloss: "释ephemeral" });
  // 之后挑出的词是缓存的子集 (ephemeral 已会): 命中.
  local.annotateKnown = { v: 1, zh: [], en: ["ephemeral"], es: [] };
  const hit = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(calls.length, 2);
  assert.deepEqual(noteKeys(hit, 0), ["cacophony", "serendipity"]);

  assert.ok(bg.volatileAnnotateCache.size > 0);
  await send("BILI_PURGE_VOLATILE", {}, TAB);
  assert.equal(bg.volatileAnnotateCache.size, 0);
});

test("缓存键含模型, 目标语言, 接口地址和释义语言", async () => {
  await reset();
  await bg.streamAnnotateBatch([en(A)], {}, null);
  settings.target = "English";
  await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(calls.length, 2); // 释义语言不同, 不命中
  assert.match(calls[1].body.messages[0].content, /英语/);
  assert.match(calls[0].body.messages[0].content, /简体中文/);
  settings.base = "https://api.example.test/v1";
  settings.model = "my-model";
  await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(calls.length, 3);
  assert.equal(calls[2].url, "https://api.example.test/v1/chat/completions");
  assert.equal(calls[2].body.model, "my-model");
  assert.equal("chat_template_kwargs" in calls[2].body, false);
  assert.deepEqual([...bg.volatileAnnotateCache.keys()].map((k) => k.split("|")[0]), [
    "Index-Translate-9B", "Index-Translate-9B", "my-model"
  ]);
});

test("结果按当前存储里的已会词再过滤一次; 已会词不会出现在请求里", async () => {
  await reset();
  chatHandler = (body) => {
    local.annotateKnown = { v: 1, zh: [], en: ["serendipity"], es: [] }; // 请求期间用户标了"会了"
    return goodReply(body);
  };
  const res = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.deepEqual(noteKeys(res, 0), ["ephemeral", "cacophony"]);

  await reset();
  local.annotateKnown = { v: 1, zh: [], en: ["cacophony", "ephemeral"], es: [] };
  const res2 = await bg.streamAnnotateBatch([en(A), en(B)], {}, null);
  assert.deepEqual(promptWords(calls[0].body.messages[0].content).map((x) => x[1]), ["serendipity", "quixotic", "zeitgeist"]);
  assert.deepEqual(noteKeys(res2, 1), ["quixotic", "zeitgeist"]);
});

test("开关关闭时不读取已存的词", async () => {
  await reset();
  local.annotateEnabled = false;
  let reads = [];
  const get = localArea.get;
  localArea.get = async (keys) => {
    reads.push(Object.keys(keys));
    return get(keys);
  };
  try {
    await bg.streamAnnotateBatch([en(A)], {}, null);
  } finally {
    localArea.get = get;
  }
  assert.ok(reads.length > 0);
  assert.ok(reads.every((keys) => !keys.includes("annotateKnown")), JSON.stringify(reads));
});

test("词表懒加载: 第一次需要时 fetch, 之后复用; 加载失败时这一批改走整段翻译, 下一批重试", async () => {
  await reset();
  bg.setAnnotateWordlists(null);
  const fetched = [];
  let fail = true;
  wordlistHandler = async (url) => {
    fetched.push(url);
    return fail ? new Response("nope", { status: 404 }) : new Response(EN_LIST, { status: 200 });
  };
  let res = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(res.ok, true);
  assert.equal(res.segments[0], "译:" + A);
  assert.deepEqual(res.notes, {});
  assert.deepEqual(fetched, ["chrome-extension://ext-id/data/freq-en.txt"]);

  fail = false;
  res = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.deepEqual(noteKeys(res, 0), ["ephemeral", "cacophony", "serendipity"]);
  await bg.streamAnnotateBatch([en(B)], {}, null);
  assert.equal(fetched.length, 2); // 成功之后不再 fetch
  // 只加载这批用到的语言.
  assert.ok(fetched.every((u) => u.endsWith("freq-en.txt")));
});

test("BILI_ANNOTATE_MARK: 开关关闭时拒绝, 不写存储, 也不读已存的词", async () => {
  await reset();
  local.annotateEnabled = false;
  local.annotateKnown = { v: 1, zh: [], en: ["kept"], es: [] };
  getLog.length = 0;
  assert.deepEqual(await mark("en", "ephemeral", true), { ok: false });
  assert.deepEqual(await mark("en", "kept", false), { ok: false });
  assert.deepEqual(local.annotateKnown.en, ["kept"]);
  assert.ok(getLog.length > 0 && getLog.every((keys) => !keys.includes("annotateKnown")), JSON.stringify(getLog));
});

test("BILI_ANNOTATE_REMOVE: 只接受设置页 (含在标签页里打开的设置页); 开关关闭时也能删", async () => {
  await reset();
  local.annotateEnabled = false;
  local.annotateKnown = { v: 1, zh: ["量子"], en: ["alpha", "beta", "Bad Old Entry"], es: [] };
  assert.deepEqual(await send("BILI_ANNOTATE_REMOVE", { lang: "en", key: "alpha" }, TAB), FORBIDDEN);
  assert.deepEqual(await send("BILI_ANNOTATE_REMOVE", { lang: "en", key: "alpha" }, EXT), { ok: true, count: 1 });
  // 设置页 open_in_tab: sender 带 tab, 仍按扩展页面放行.
  const extInTab = { ...EXT, tab: { id: 7, windowId: 1 } };
  assert.deepEqual(await send("BILI_ANNOTATE_REMOVE", { lang: "zh", key: "量子" }, extInTab), { ok: true, count: 0 });
  assert.deepEqual(await send("BILI_ANNOTATE_REMOVE", { lang: "en", key: "missing" }, EXT), { ok: true, count: 1 });
  assert.equal((await send("BILI_ANNOTATE_REMOVE", { lang: "ja", key: "x" }, EXT)).ok, false);
  assert.equal((await send("BILI_ANNOTATE_REMOVE", { lang: "en", key: "" }, EXT)).ok, false);
  // 读取时已被丢掉的旧格式条目不再出现在存储里.
  assert.deepEqual(local.annotateKnown, { v: 1, zh: [], en: ["beta"], es: [] });
});

test("状态 200 但回复不可用 (content 为 null, 不是 JSON): 按格式不合格逐级退回, 不整批失败", async () => {
  await reset();
  chatHandler = (body) =>
    isAnnotate(body)
      ? new Response(JSON.stringify({ choices: [{ message: { content: null } }] }), {
          status: 200,
          headers: { "content-type": "application/json" }
        })
      : translateReply(body);
  let res = await bg.streamAnnotateBatch([en(A), en(B)], {}, null);
  assert.equal(res.ok, true);
  assert.deepEqual(Object.keys(res.notes), []);
  assert.deepEqual(Object.keys(res.segments).sort(), ["0", "1"]);
  assert.equal(annotateCalls().length, 3); // 合并 1 次 + 逐段 2 次

  await reset();
  chatHandler = (body) => (isAnnotate(body) ? new Response("<html>oops</html>", { status: 200 }) : translateReply(body));
  res = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(res.ok, true);
  assert.equal(res.segments[0], "译:" + A);
});

test("缓存: 释义被丢掉的词也记进缓存, 再来同一段不重新请求", async () => {
  await reset();
  // ephemeral 的释义为空字符串 (丢掉), 其余正常.
  chatHandler = (body) =>
    promptWords(body.messages[0].content)
      .map(([n, w]) => n + ". " + w + " => " + (w === "ephemeral" ? "ephemeral" : "释" + w))
      .join("\n");
  local.annotateLevels = { zh: 4, en: 20, es: 20 };
  settings.target = "中文";
  const first = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(first.ok, true);
  assert.deepEqual(noteKeys(first, 0).sort(), ["cacophony", "serendipity"]);
  const before = calls.length;
  const second = await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(calls.length, before);
  assert.deepEqual(noteKeys(second, 0).sort(), ["cacophony", "serendipity"]);
});

test("BILI_ANNOTATE_MARK: 校验 lang, key, known; 标记和撤销", async () => {
  await reset();
  const bad = [
    ["ja", "word"],
    [undefined, "word"],
    ["en", ""],
    ["en", "   "],
    ["en", "x".repeat(33)],
    ["en", "two\nlines"],
    ["en", "cr\rhere"],
    ["en", 42],
    ["en", undefined],
    // 只收单个词的 key: 不收句子片段, 网址, 大写, 数字
    ["en", "two words"],
    ["en", "https://a.example/?token=1"],
    ["en", "Word"],
    ["en", "w1"],
    ["zh", "abc"],
    ["zh", "量子1"],
    ["zh", "量子，纠缠"]
  ];
  for (const [lang, key] of bad) {
    const res = await mark(lang, key, true);
    assert.equal(res.ok, false, JSON.stringify([lang, key]));
  }
  assert.equal((await mark("en", "word", "yes")).ok, false);
  assert.equal("annotateKnown" in local, false);

  assert.deepEqual(await mark("en", "x".repeat(32), true), { ok: true, count: 1 });
  assert.deepEqual(await mark("en", "ephemeral", true), { ok: true, count: 2 });
  assert.deepEqual(await mark("en", "ephemeral", true), { ok: true, count: 2 }); // 重复标记不重复存
  assert.deepEqual(await mark("zh", "量子", true), { ok: true, count: 1 });
  assert.deepEqual(await send("BILI_ANNOTATE_MARK", { lang: "es", key: "efímera" }, TAB), { ok: true, count: 1 }); // known 缺省按标记
  assert.deepEqual(local.annotateKnown, { v: 1, zh: ["量子"], en: ["x".repeat(32), "ephemeral"], es: ["efímera"] });

  assert.deepEqual(await mark("en", "ephemeral", false), { ok: true, count: 1 });
  assert.deepEqual(await mark("en", "nevermarked", false), { ok: true, count: 1 });
  assert.deepEqual(local.annotateKnown.en, ["x".repeat(32)]);
});

// 0 -> "a", 25 -> "z", 26 -> "ba" ...: 只含小写字母的合法 key.
const wordKey = (i) => {
  let s = "";
  do {
    s = String.fromCharCode(97 + (i % 26)) + s;
    i = Math.floor(i / 26);
  } while (i > 0);
  return "w" + s;
};
const hanKey = (i) => String.fromCodePoint(0x4e00 + i) + "字";

test("BILI_ANNOTATE_MARK: 每种语言最多 50000 个词", async () => {
  await reset();
  local.annotateKnown = { v: 1, zh: [], en: Array.from({ length: 50000 }, (_, i) => wordKey(i)), es: [] };
  assert.equal(new Set(local.annotateKnown.en).size, 50000);
  assert.deepEqual(await mark("en", "onemore", true), { ok: false, error: "limit" });
  assert.equal(local.annotateKnown.en.length, 50000);
  assert.deepEqual(await mark("en", wordKey(1), true), { ok: true, count: 50000 }); // 已有的词不受影响
  assert.deepEqual(await mark("en", wordKey(1), false), { ok: true, count: 49999 });
  assert.deepEqual(await mark("es", "palabra", true), { ok: true, count: 1 }); // 别的语言不受影响
});

test("并发标记 20 次不丢; 链上某一步失败不卡住后面的操作", async () => {
  await reset();
  storageDelay = true;
  const langs = ["en", "es", "zh"];
  const keyFor = (i) => (langs[i % 3] === "zh" ? hanKey(i) : wordKey(i));
  const results = await Promise.all(Array.from({ length: 20 }, (_, i) => mark(langs[i % 3], keyFor(i), true)));
  assert.ok(results.every((r) => r.ok), JSON.stringify(results));
  const total = langs.reduce((n, l) => n + local.annotateKnown[l].length, 0);
  assert.equal(total, 20);
  assert.deepEqual(new Set(local.annotateKnown.en), new Set([0, 3, 6, 9, 12, 15, 18].map(wordKey)));
  assert.deepEqual(new Set(local.annotateKnown.zh), new Set([2, 5, 8, 11, 14, 17].map(hanKey)));

  // 第 2 次写入失败 (配额), 第 1 和第 3 次不受影响, 失败的那次回复 ok: false.
  await reset();
  storageDelay = true;
  assert.equal((await mark("en", "alpha", true)).ok, true);
  failSets = 1;
  const second = mark("en", "beta", true);
  const third = mark("en", "gamma", true);
  const out = await Promise.all([second, third]);
  assert.equal(out[0].ok, false);
  assert.equal(out[1].ok, true);
  assert.deepEqual(local.annotateKnown.en, ["alpha", "gamma"]);
  // 之后的操作照常.
  assert.equal((await mark("en", "delta", true)).ok, true);
  assert.deepEqual(local.annotateKnown.en, ["alpha", "gamma", "delta"]);
});

test("CLEAR 和 LIST 只接受设置页; MARK 只接受标签页", async () => {
  await reset();
  assert.deepEqual(await send("BILI_ANNOTATE_CLEAR", {}, TAB), FORBIDDEN);
  assert.deepEqual(await send("BILI_ANNOTATE_LIST", {}, TAB), FORBIDDEN);
  assert.deepEqual(await mark("en", "word", true, EXT), FORBIDDEN);
  assert.deepEqual(await mark("en", "word", true, { id: "other", tab: { id: 1 } }), FORBIDDEN);
  assert.deepEqual(await send("BILI_ANNOTATE_LIST", {}, undefined), FORBIDDEN);
  assert.equal("annotateKnown" in local, false);

  await mark("en", "b", true);
  await mark("en", "a", true);
  await mark("zh", "量子", true);
  assert.deepEqual(await send("BILI_ANNOTATE_LIST", {}, EXT), {
    ok: true,
    words: { zh: ["量子"], en: ["b", "a"], es: [] },
    counts: { zh: 1, en: 2, es: 0 }
  });
  assert.deepEqual(await send("BILI_ANNOTATE_CLEAR", {}, EXT), { ok: true });
  assert.deepEqual(local.annotateKnown, { v: 1, zh: [], en: [], es: [] });
  assert.equal((await send("BILI_ANNOTATE_LIST", {}, EXT)).counts.en, 0);

  // 开关关闭时设置页照常能看和清.
  await mark("en", "again", true);
  local.annotateEnabled = false;
  assert.equal((await send("BILI_ANNOTATE_LIST", {}, EXT)).counts.en, 1);
  assert.deepEqual(await send("BILI_ANNOTATE_CLEAR", {}, EXT), { ok: true });
  assert.equal(local.annotateKnown.en.length, 0);
});

test("LIST 排在已经排队的写入之后", async () => {
  await reset();
  storageDelay = true;
  const pending = [mark("en", "one", true), mark("en", "two", true)];
  const list = await send("BILI_ANNOTATE_LIST", {}, EXT);
  assert.deepEqual(list.words.en, ["one", "two"]);
  await Promise.all(pending);
});

test("存储里只有词的 key, 没有段落原文和网址", async () => {
  await reset();
  await bg.streamAnnotateBatch([en(A), en(B)], {}, null);
  await mark("en", "ephemeral", true);
  await mark("zh", "量子", true);
  const known = local.annotateKnown;
  assert.deepEqual(Object.keys(known), ["v", "zh", "en", "es"]);
  assert.equal(known.v, 1);
  for (const lang of ["zh", "en", "es"]) {
    assert.ok(Array.isArray(known[lang]));
    assert.ok(known[lang].every((k) => typeof k === "string" && k.length <= 64 && !/[\s]/.test(k)));
  }
  const stored = JSON.stringify([local, settings]);
  assert.doesNotMatch(stored, /cacophony/);
  assert.doesNotMatch(stored, /https?:\/\/www/);
});

test("storage.onChanged: 只有 annotateKnown 变化时不清翻译缓存和注释缓存", async () => {
  await reset();
  const hello = "hello onchanged";
  const cachedTranslate = async () => {
    const before = calls.length;
    await bg.translate(hello);
    return calls.length - before;
  };
  assert.equal(await cachedTranslate(), 1);
  assert.equal(await cachedTranslate(), 0);
  await bg.streamAnnotateBatch([en(A)], {}, null);
  assert.equal(bg.volatileAnnotateCache.size, 1);

  hooks.changed({ annotateKnown: { oldValue: {}, newValue: {} } }, "local");
  assert.equal(await cachedTranslate(), 0);
  assert.equal(bg.volatileAnnotateCache.size, 1);

  // 其他变化保持原行为: 清空全部缓存.
  hooks.changed({ annotateEnabled: { newValue: false } }, "local");
  assert.equal(bg.volatileAnnotateCache.size, 0);
  assert.equal(await cachedTranslate(), 1);
  assert.equal(await cachedTranslate(), 0);

  await bg.streamAnnotateBatch([en(A)], {}, null);
  hooks.changed({ annotateLevels: { newValue: {} } }, "local");
  assert.equal(bg.volatileAnnotateCache.size, 0);

  await bg.streamAnnotateBatch([en(A)], {}, null);
  hooks.changed({ annotateKnown: {}, glossary: {} }, "local");
  assert.equal(bg.volatileAnnotateCache.size, 0);
  assert.equal(await cachedTranslate(), 1);

  // 同名的键出现在 sync 区不算.
  assert.equal(await cachedTranslate(), 0);
  hooks.changed({ annotateKnown: {} }, "sync");
  assert.equal(await cachedTranslate(), 1);
});

test("BILI_GET_IMMERSIVE_PREFS 的回复带 annotate", async () => {
  await reset();
  assert.equal((await send("BILI_GET_IMMERSIVE_PREFS", {}, TAB)).annotate, true);
  local.annotateEnabled = false;
  assert.equal((await send("BILI_GET_IMMERSIVE_PREFS", {}, TAB)).annotate, false);
  local.annotateEnabled = true;
  settings.target = "Français";
  assert.equal((await send("BILI_GET_IMMERSIVE_PREFS", {}, TAB)).annotate, false);
  settings.target = "中文";
  settings.apiType = "deeplx";
  settings.base = "https://x.example/translate";
  const res = await send("BILI_GET_IMMERSIVE_PREFS", {}, TAB);
  assert.equal(res.annotate, false);
  assert.equal(res.modelSwitch, false);
});
