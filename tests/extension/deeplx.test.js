"use strict";

// DeepLX 接口格式: 目标语言代码, 请求构造与响应解析, 以及划词, 双语对照, 学习卡和识图在该格式下的行为.
const test = require("node:test");
const assert = require("node:assert/strict");
const { storageArea } = require("../support/fake-storage");

const ENDPOINT = "https://deeplx.example.test/translate?token={apiKey}";
const REQUEST_URL = "https://deeplx.example.test/translate?token=tok%2F1";
const IMAGE = "data:image/png;base64,iVBORw0KGgo=";

const settings = {};
const local = { glossary: "" };
const calls = [];
let respond;

globalThis.chrome = { storage: { sync: storageArea(settings), local: storageArea(local) } };
// 与真实 fetch 一致: 信号已中止时直接拒绝, 不算发出请求.
globalThis.fetch = async (url, init) => {
  if (init.signal && init.signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
  const body = JSON.parse(init.body);
  calls.push({ url: String(url), headers: init.headers, body });
  return respond(body);
};

const bg = require("../../bili-index-translate/background.js");

let seq = 0;
const unique = (label) => label + " " + ++seq;
// 像真实的 DeepLX 服务一样逐行翻译并保留换行.
const tr = (text) => text.split("\n").map((line) => "译" + line).join("\n");

function reset(overrides = {}) {
  calls.length = 0;
  for (const k of Object.keys(settings)) delete settings[k];
  Object.assign(settings, { apiType: "deeplx", base: ENDPOINT, proxy: "", target: "中文" }, overrides);
  local.glossary = "";
  local.key = "tok/1";
  respond = (body) => deeplxOk(tr(body.text));
}

function jsonResponse(payload, status = 200) {
  return new Response(JSON.stringify(payload), { status, headers: { "content-type": "application/json" } });
}

function deeplxOk(data) {
  return jsonResponse({ code: 200, id: 1, data, source_lang: "EN", target_lang: "ZH" });
}

const ok = (text, extra = {}) => ({ ok: true, text, model: "DeepLX", via: "direct", glossaryHits: 0, studyCard: false, ...extra });
const batchOk = (segments, extra = {}) => ({ ok: true, segments, model: "DeepLX", via: "direct", glossaryHits: 0, ...extra });

test("deeplxTargetLang 把目标语言名映射为 DeepL 代码, 已是代码的值原样大写", () => {
  const cases = {
    "中文": "ZH",
    "简体中文": "ZH",
    "zh-CN": "ZH",
    "": "ZH",
    "Chinese": "ZH",
    "繁體中文": "ZH-HANT",
    "Traditional Chinese": "ZH-HANT",
    "zh-TW": "ZH-HANT",
    "English": "EN",
    "英文": "EN",
    "日本語": "JA",
    "한국어": "KO",
    "Français": "FR",
    "Deutsch": "DE",
    "Español": "ES",
    "Русский": "RU",
    "Italiano": "IT",
    "葡萄牙语": "PT",
    "Portuguese (Brazil)": "PT",
    "Slovak": "SK",
    "Slovenian": "SL",
    "Tiếng Việt": "VI",
    "it": "IT",
    "pt-br": "PT-BR",
    "en_us": "EN-US",
    "Klingon": "Klingon"
  };
  for (const [target, code] of Object.entries(cases)) {
    assert.equal(bg.deeplxTargetLang(target), code, "target " + JSON.stringify(target));
  }
});

test("buildDeeplxRequest: 地址里的 {apiKey} 换成转义后的 key, 此时不发 Authorization", () => {
  const req = bg.buildDeeplxRequest("https://h.test/translate?token={apiKey}", " a b/c ", "Hello", "中文");
  assert.equal(req.url, "https://h.test/translate?token=a%20b%2Fc");
  assert.deepEqual(req.headers, { "Content-Type": "application/json" });
  assert.deepEqual(req.body, { text: "Hello", source_lang: "auto", target_lang: "ZH" });

  assert.equal(bg.buildDeeplxRequest("https://h.test/{{apiKey}}/translate/", "k", "x", "English").url, "https://h.test/k/translate");
  const emptyKey = bg.buildDeeplxRequest("https://h.test/translate?token={apiKey}", "", "x", "中文");
  assert.equal(emptyKey.url, "https://h.test/translate?token=");
  assert.deepEqual(emptyKey.headers, { "Content-Type": "application/json" });
});

test("buildDeeplxRequest: 没有占位符时地址原样使用, key 走 Bearer, key 为空则不带鉴权头", () => {
  const withKey = bg.buildDeeplxRequest("http://localhost:1188/translate", "secret", "x", "日本語");
  assert.equal(withKey.url, "http://localhost:1188/translate");
  assert.deepEqual(withKey.headers, { "Content-Type": "application/json", "Authorization": "Bearer secret" });
  assert.equal(withKey.body.target_lang, "JA");
  assert.deepEqual(bg.buildDeeplxRequest("http://localhost:1188/translate", "  ", "x", "中文").headers, {
    "Content-Type": "application/json"
  });
});

test("parseDeeplxResponse 读取 data, 把非 200 的 code, 非 JSON 和空译文报告为错误", () => {
  assert.deepEqual(bg.parseDeeplxResponse('{"code":200,"data":" 你好 "}', 200), { ok: true, text: "你好" });
  assert.deepEqual(bg.parseDeeplxResponse('{"data":"你好"}', 200), { ok: true, text: "你好" });
  assert.deepEqual(bg.parseDeeplxResponse('{"code":401,"message":"Invalid access token"}', 200), {
    ok: false,
    status: 401,
    body: "Invalid access token"
  });
  assert.deepEqual(bg.parseDeeplxResponse('{"code":503}', 200), { ok: false, status: 503, body: '{"code":503}' });
  for (const raw of ['{"code":200,"data":""}', '{"code":200}', "null"]) {
    const res = bg.parseDeeplxResponse(raw, 200);
    assert.equal(res.ok, false);
    assert.equal(res.status, 200);
    assert.ok(res.body.startsWith('No translation in "data": '), res.body);
  }
  const html = bg.parseDeeplxResponse("<html>ok</html>", 200);
  assert.deepEqual(html, { ok: false, status: 200, body: "Response is not JSON: <html>ok</html>" });
});

test("streamTranslation: DeepLX 格式只发原文和语言代码, 不发提示词和术语表, 结果写入缓存", async () => {
  reset();
  local.glossary = "fox => 狐狸";
  respond = () => deeplxOk(" 敏捷的棕色狐狸 ");
  const seen = [];
  const text = unique("The quick brown fox");
  const res = await bg.streamTranslation(text, (d) => seen.push(d));
  assert.deepEqual(res, ok("敏捷的棕色狐狸"));
  assert.deepEqual(seen, []);
  assert.deepEqual(calls, [
    {
      url: REQUEST_URL,
      headers: { "Content-Type": "application/json" },
      body: { text, source_lang: "auto", target_lang: "ZH" }
    }
  ]);

  const again = await bg.streamTranslation(text, () => {});
  assert.deepEqual(again, ok("敏捷的棕色狐狸", { cached: true }));
  assert.equal(calls.length, 1);
});

test("translate (Save & test 用的非流式入口) 在 DeepLX 格式下结果相同, 目标语言按设置映射", async () => {
  reset({ base: "https://deeplx.example.test/translate/", target: "English" });
  respond = () => deeplxOk("Hello");
  const res = await bg.translate(unique("你好"));
  assert.deepEqual(res, ok("Hello"));
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "https://deeplx.example.test/translate");
  assert.deepEqual(calls[0].headers, { "Content-Type": "application/json", "Authorization": "Bearer tok/1" });
  assert.equal(calls[0].body.target_lang, "EN");
});

test("DeepLX 格式下填写的中继作为完整地址使用, 只请求中继一次", async () => {
  reset({ proxy: "https://relay.example.test/https://deeplx.example.test/translate/" });
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.equal(res.ok, true);
  assert.equal(res.via, "relay");
  assert.deepEqual(calls.map((c) => c.url), ["https://relay.example.test/https://deeplx.example.test/translate"]);
  assert.equal(calls[0].headers.Authorization, "Bearer tok/1");
});

test("DeepLX 请求失败时返回 HTTP 状态或业务 code, 只请求一次, 失败结果不进缓存", async () => {
  reset();
  const text = unique("hello");

  respond = () => new Response("<html>blocked</html>", { status: 403, headers: { "content-type": "text/html" } });
  assert.deepEqual(await bg.streamTranslation(text, () => {}), {
    ok: false,
    status: 403,
    body: "<html>blocked</html>",
    via: "direct"
  });
  assert.equal(calls.length, 1);

  respond = () => jsonResponse({ code: 429, message: "Too Many Requests" });
  assert.deepEqual(await bg.streamTranslation(text, () => {}), {
    ok: false,
    status: 429,
    body: "Too Many Requests",
    via: "direct"
  });

  respond = () => new Response("<html>ok</html>", { status: 200, headers: { "content-type": "text/html" } });
  const html = await bg.translate(text);
  assert.equal(html.ok, false);
  assert.equal(html.status, 200);
  assert.match(html.body, /^Response is not JSON: <html>/);

  respond = () => {
    throw new TypeError("fetch failed");
  };
  assert.deepEqual(await bg.streamTranslation(text, () => {}), { ok: false, status: 0, body: "fetch failed" });

  respond = () => deeplxOk("你好");
  const recovered = await bg.streamTranslation(text, () => {});
  assert.equal(recovered.ok, true);
  assert.equal(recovered.cached, undefined);
  assert.equal(calls.length, 5);
});

test("DeepLX 请求在调用方已中止时返回 cancelled, 不发请求", async () => {
  reset();
  const ctrl = new AbortController();
  ctrl.abort();
  const res = await bg.streamTranslation(unique("hello"), () => {}, ctrl.signal);
  assert.deepEqual(res, { ok: false, cancelled: true, status: 0, body: "Cancelled" });
  assert.equal(calls.length, 0);
});

test("streamImmersiveBatch: 单行段落合并为一次请求并按行拆回, 空段不请求, 结果按段进缓存", async () => {
  reset();
  const a = unique("first");
  const b = unique("second");
  const progress = [];
  const res = await bg.streamImmersiveBatch([a, "", b], "Index-Translate-9B", (p) => progress.push(p));
  assert.deepEqual(res, batchOk({ 0: tr(a), 1: "", 2: tr(b) }));
  assert.deepEqual(progress, [{ segments: { 0: tr(a), 1: "", 2: tr(b) }, model: "DeepLX", via: "direct" }]);
  assert.deepEqual(calls.map((c) => c.body), [{ text: a + "\n" + b, source_lang: "auto", target_lang: "ZH" }]);

  // 合并请求得到的译文按段缓存: 划词同一句直接命中.
  assert.deepEqual(await bg.streamTranslation(a, () => {}), ok(tr(a), { cached: true }));

  // 只剩一段未缓存时发单段请求.
  const c = unique("third");
  const routes = [];
  const second = await bg.streamImmersiveBatch([a, c], "", (p) => routes.push(p.via));
  assert.deepEqual(second.segments, { 0: tr(a), 1: tr(c) });
  assert.deepEqual(routes, ["cache", "direct"]);
  assert.deepEqual(calls.map((x) => x.body.text), [a + "\n" + b, c]);

  const cached = await bg.streamImmersiveBatch([a, b, c], "", () => assert.fail("no progress expected"));
  assert.deepEqual(cached, { ok: true, segments: { 0: tr(a), 1: tr(b), 2: tr(c) }, model: "DeepLX", via: "direct", cached: true });
  assert.equal(calls.length, 2);
});

test("streamImmersiveBatch: 自带换行的段落逐段请求并逐段上报, 个别失败时保留其余译文", async () => {
  reset();
  const [a, b, c] = [unique("one\nmore"), unique("two\nmore"), unique("three")];
  respond = (body) => (body.text === b ? jsonResponse({ code: 429, message: "Too Many Requests" }, 429) : deeplxOk(tr(body.text)));
  const sizes = [];
  const res = await bg.streamImmersiveBatch([a, b, c], "", (p) => sizes.push(Object.keys(p.segments).length));
  assert.deepEqual(res, batchOk({ 0: tr(a), 2: tr(c) }));
  assert.deepEqual(calls.map((x) => x.body.text), [a, b, c]);
  assert.deepEqual(sizes, [1, 2]);
});

test("streamImmersiveBatch: 合并请求失败时不再逐段重发; 没有任何译文才返回错误", async () => {
  reset();
  respond = () => new Response("upstream down", { status: 502 });
  const failed = await bg.streamImmersiveBatch([unique("x"), unique("y")], "", () => assert.fail("no progress expected"));
  assert.deepEqual(failed, { ok: false, status: 502, body: "upstream down", via: "direct" });
  assert.equal(calls.length, 1);

  reset();
  const kept = unique("kept");
  await bg.streamTranslation(kept, () => {});
  calls.length = 0;
  respond = () => new Response("upstream down", { status: 502 });
  const partial = await bg.streamImmersiveBatch([kept, unique("p"), unique("q")], "", () => {});
  assert.deepEqual(partial, batchOk({ 0: tr(kept) }));
  assert.equal(calls.length, 1);
});

test("streamImmersiveBatch: 回复行数对不上时改为逐段请求, 并记住该接口不再合并, 其他接口不受影响", async () => {
  reset({ base: "https://joined.example.test/translate" });
  // 这个接口把多行合成一行返回.
  respond = (body) => deeplxOk("译" + body.text.replace(/\n/g, " "));
  const [a, b] = [unique("alpha"), unique("beta")];
  const res = await bg.streamImmersiveBatch([a, b], "", () => {});
  assert.deepEqual(res, batchOk({ 0: "译" + a, 1: "译" + b }));
  assert.deepEqual(calls.map((x) => x.body.text), [a + "\n" + b, a, b]);

  calls.length = 0;
  const [c, d] = [unique("gamma"), unique("delta")];
  await bg.streamImmersiveBatch([c, d], "", () => {});
  assert.deepEqual(calls.map((x) => x.body.text), [c, d]);

  reset();
  const [e, f] = [unique("epsilon"), unique("zeta")];
  await bg.streamImmersiveBatch([e, f], "", () => {});
  assert.deepEqual(calls.map((x) => x.body.text), [e + "\n" + f]);
});

test("streamImmersiveBatch: 调用方中止后不再请求后续段落", async () => {
  reset();
  const ctrl = new AbortController();
  const items = [unique("a\nx"), unique("b\ny"), unique("c\nz")];
  const res = await bg.streamImmersiveBatch(items, "", () => ctrl.abort(), ctrl.signal);
  assert.deepEqual(res, { ok: false, cancelled: true, status: 0, body: "Cancelled" });
  assert.equal(calls.length, 1);
});

test("学习卡和识图在 DeepLX 格式下直接报不支持, 不发请求", async () => {
  reset();
  const results = [
    await bg.streamStudyCard(unique("ephemeral"), "", () => {}),
    await bg.generateStudyCard(unique("ephemeral")),
    await bg.streamVisionTranslation(IMAGE, "", () => {})
  ];
  for (const res of results) {
    assert.equal(res.ok, false);
    assert.equal(res.status, 0);
    assert.match(res.body, /needs an OpenAI-compatible chat model/);
  }
  assert.match(results[0].body, /^Study Card /);
  assert.match(results[1].body, /^Study Card /);
  assert.match(results[2].body, /^Image translation /);
  assert.equal(calls.length, 0);
});

test("apiType 为 openai 或缺省时仍请求 /chat/completions 并用 Bearer 鉴权", async () => {
  for (const apiType of ["openai", undefined]) {
    reset({ base: "https://api.example.test/v1/" });
    if (apiType) settings.apiType = apiType;
    else delete settings.apiType;
    respond = () => jsonResponse({ choices: [{ message: { content: "你好" } }] });
    const res = await bg.translate(unique("hello"));
    assert.equal(res.ok, true);
    assert.equal(res.text, "你好");
    assert.equal(res.model, "Index-Translate-9B");
    assert.equal(res.studyCard, undefined);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, "https://api.example.test/v1/chat/completions");
    assert.deepEqual(calls[0].headers, { "Content-Type": "application/json", "Authorization": "Bearer tok/1" });
    assert.equal(calls[0].body.stream, false);
    assert.ok(Array.isArray(calls[0].body.messages));
  }
});
