"use strict";

// 自定义 JSON 接口模板: 结果路径, 模板校验, 占位符转义, 响应解析, 以及划词, 双语对照, 学习卡和识图在该格式下的行为.
const test = require("node:test");
const assert = require("node:assert/strict");

const IMAGE = "data:image/png;base64,iVBORw0KGgo=";

const settings = {};
const calls = [];
let respond;

globalThis.chrome = {
  storage: {
    sync: { get: async (defaults) => ({ ...defaults, ...settings }) },
    local: { get: async (defaults) => ({ ...defaults, glossary: "fox => 狐狸" }) }
  }
};
// 与真实 fetch 一致: 信号已中止时直接拒绝, 不算发出请求. 请求体按原样记录, GET 请求没有请求体.
globalThis.fetch = async (url, init) => {
  if (init.signal && init.signal.aborted) throw new DOMException("The operation was aborted.", "AbortError");
  const call = { url: String(url), method: init.method, headers: init.headers, body: init.body };
  calls.push(call);
  return respond(call);
};

const bg = require("../../bili-index-translate/background.js");

let seq = 0;
const unique = (label) => label + " " + ++seq;
const tr = (text) => "译" + text;

// DeepL 官方接口的形状: 文本以数组发送, 译文在 translations[].text.
const DEEPL = {
  apiType: "custom",
  customName: "DeepL",
  base: "https://api.example.test/v2/translate",
  proxy: "",
  key: "k 1/2",
  target: "中文",
  customMethod: "POST",
  customHeaders: "Authorization: DeepL-Auth-Key {apiKey}",
  customBody: '{"text": {texts}, "target_lang": "{targetLang}"}',
  customResultPath: "translations[*].text",
  customErrorPath: "message"
};

function reset(overrides = {}) {
  calls.length = 0;
  for (const k of Object.keys(settings)) delete settings[k];
  Object.assign(settings, DEEPL, overrides);
  respond = (call) => jsonResponse({ translations: JSON.parse(call.body).text.map((t) => ({ detected_source_language: "EN", text: tr(t) })) });
}

function jsonResponse(payload, status = 200) {
  return new Response(JSON.stringify(payload), { status, headers: { "content-type": "application/json" } });
}

const template = (overrides = {}) => bg.parseCustomTemplate({ ...DEEPL, ...overrides });
const ok = (text, extra = {}) => ({ ok: true, text, model: "DeepL", via: "direct", glossaryHits: 0, studyCard: false, ...extra });
const batchOk = (segments, extra = {}) => ({ ok: true, segments, model: "DeepL", via: "direct", glossaryHits: 0, ...extra });

test("parseJsonPath 解析键, 下标和 [*], 拒绝空路径和畸形路径", () => {
  assert.deepEqual(bg.parseJsonPath("translations[0].text"), ["translations", 0, "text"]);
  assert.deepEqual(bg.parseJsonPath(" $.data.translations[*].translatedText "), ["data", "translations", null, "translatedText"]);
  assert.deepEqual(bg.parseJsonPath("[0][*][0]"), [0, null, 0]);
  assert.deepEqual(bg.parseJsonPath("data"), ["data"]);
  for (const bad of ["", "  ", "a..b", ".a", "a.", "a[", "a[x]", "a[-1]", "a b", "a.[0]"]) {
    assert.equal(bg.parseJsonPath(bad), null, "path " + JSON.stringify(bad));
  }
});

test("readJsonPath 返回路径能到达的全部值, 路径不存在时为空", () => {
  const data = { translations: [{ text: "x" }, { text: "y" }], data: "z", "*": "star" };
  const read = (p) => bg.readJsonPath(data, bg.parseJsonPath(p));
  assert.deepEqual(read("translations[*].text"), ["x", "y"]);
  assert.deepEqual(read("translations[1].text"), ["y"]);
  assert.deepEqual(read("data"), ["z"]);
  assert.deepEqual(read("*"), ["star"]);
  assert.deepEqual(read("translations[2].text"), []);
  assert.deepEqual(read("translations.text"), []);
  assert.deepEqual(read("data[0]"), []);
  assert.deepEqual(read("missing.deep"), []);
  assert.deepEqual(read("toString"), []);
});

test("parseCustomTemplate: 请求体默认按 JSON 发送, {texts} 启用批量, GET 不带请求体", () => {
  const tpl = template();
  assert.equal(tpl.ok, true);
  assert.equal(tpl.method, "POST");
  assert.equal(tpl.batch, true);
  assert.equal(tpl.json, true);
  assert.deepEqual(tpl.headers, [
    ["Authorization", "DeepL-Auth-Key {apiKey}"],
    ["Content-Type", "application/json"]
  ]);

  const form = template({ customHeaders: "# comment\r\n\r\ncontent-type: application/x-www-form-urlencoded", customBody: "q={text}" });
  assert.equal(form.form, true);
  assert.equal(form.json, false);
  assert.equal(form.batch, false);
  assert.deepEqual(form.headers, [["content-type", "application/x-www-form-urlencoded"]]);

  const get = template({ customMethod: "get", base: "https://api.example.test/t?q={text}", customHeaders: "" });
  assert.equal(get.method, "GET");
  assert.equal(get.bodyTemplate, "");
  assert.deepEqual(get.headers, []);
});

test("parseCustomTemplate 报告无法工作的模板: 缺结果路径, 路径或请求头格式错误, 没有发送原文", () => {
  const cases = [
    [{ customResultPath: " " }, /the result path is empty/],
    [{ customResultPath: "a..b" }, /the result path "a\.\.b" is not valid/],
    [{ customErrorPath: "[x]" }, /the error path "\[x\]" is not valid/],
    [{ customHeaders: "Authorization Bearer x" }, /the header line "Authorization Bearer x" is not in the form/],
    [{ customBody: '{"target_lang": "{targetLang}"}' }, /never sends the text/],
    [{ customMethod: "GET" }, /never sends the text/]
  ];
  for (const [overrides, pattern] of cases) {
    const tpl = template(overrides);
    assert.equal(tpl.ok, false);
    assert.match(tpl.error, /^Custom API: /);
    assert.match(tpl.error, pattern);
  }
});

test("buildCustomRequest 按位置转义占位符: URL 编码, JSON 转义, 请求头去换行", () => {
  const nasty = 'He said "hi"\\ \n第二行\t{text} </b>';
  const tpl = template({
    customHeaders: "X-Key: {{apiKey}}\nX-Target: {target}|{targetLang}|{targetLangLower}",
    customBody: '{"q": "{text}", "all": {texts}, "to": "{targetLangLower}", "nested": {"key": "{apiKey}"}}'
  });
  const req = bg.buildCustomRequest(tpl, "https://h.test/t/?k={apiKey}&to={targetLang}/", " a b/c\n", [nasty, "second"], "English");
  assert.equal(req.url, "https://h.test/t/?k=a%20b%2Fc&to=EN");
  assert.deepEqual(req.headers, { "X-Key": "a b/c", "X-Target": "English|EN|en", "Content-Type": "application/json" });
  assert.deepEqual(JSON.parse(req.body), { q: nasty, all: [nasty, "second"], to: "en", nested: { key: "a b/c" } });

  const headerOnly = bg.buildCustomRequest(template({ customHeaders: "X-Text: {text}" }), "https://h.test/t", "k", ["line1\r\nline2"], "中文");
  assert.equal(headerOnly.headers["X-Text"], "line1 line2");
});

test("buildCustomRequest: 表单请求体按 URL 编码; GET 请求没有请求体; 填完后不是合法 JSON 时报错", () => {
  const form = template({ customHeaders: "Content-Type: application/x-www-form-urlencoded", customBody: "q={text}&target={target}" });
  const req = bg.buildCustomRequest(form, "https://h.test/t", "k", ["a&b=c d"], "zh-CN");
  assert.equal(req.body, "q=a%26b%3Dc%20d&target=zh-CN");

  const get = template({ customMethod: "GET", base: "https://h.test/t?q={text}", customHeaders: "" });
  const getReq = bg.buildCustomRequest(get, "https://h.test/t?q={text}&to={targetLangLower}", "", ["你好 world?"], "English");
  assert.equal(getReq.url, "https://h.test/t?q=%E4%BD%A0%E5%A5%BD%20world%3F&to=en");
  assert.equal(getReq.body, undefined);
  assert.deepEqual(getReq.headers, {});

  const unquoted = bg.buildCustomRequest(template({ customBody: '{"q": {text}}' }), "https://h.test/t", "k", ["hello"], "中文");
  assert.match(unquoted.error, /^Custom API: the request body is not valid JSON/);
});

test("parseCustomResponse 读取结果路径: 单条, 分片拼接, 批量数组和数量不符", () => {
  const single = template({ customBody: '{"q": "{text}"}', customResultPath: "data.out" });
  assert.deepEqual(bg.parseCustomResponse('{"data":{"out":"  你好 "}}', 200, true, single, 1), { ok: true, texts: ["你好"] });

  // 有的服务把一段译文拆成若干片返回; 非批量模板把路径到达的全部字符串拼成一条.
  const pieces = template({ customBody: '{"q": "{text}"}', customResultPath: "[0][*][0]" });
  assert.deepEqual(bg.parseCustomResponse('[[["你好，", "Hello, "], ["世界。", "world."]], null, "en"]', 200, true, pieces, 1), {
    ok: true,
    texts: ["你好，世界。"]
  });

  const batch = template();
  assert.deepEqual(bg.parseCustomResponse('{"translations":[{"text":" 一 "},{"text":"二"}]}', 200, true, batch, 2), { ok: true, texts: ["一", "二"] });
  // LibreTranslate 的形状: 结果路径直接指向字符串数组.
  const arrayAtPath = template({ customResultPath: "translatedText" });
  assert.deepEqual(bg.parseCustomResponse('{"translatedText":["一","二"]}', 200, true, arrayAtPath, 2), { ok: true, texts: ["一", "二"] });

  assert.deepEqual(bg.parseCustomResponse('{"translations":[{"text":"一"}]}', 200, true, batch, 2), {
    ok: false,
    status: 200,
    mismatch: true,
    body: 'Expected 2 translation(s) at "translations[*].text" but found 1.'
  });
});

test("parseCustomResponse 报告错误: 错误路径优先, 否则显示响应片段; 非 JSON 和类型不符都不当作译文", () => {
  const tpl = template({ customErrorPath: "error.message" });
  assert.deepEqual(bg.parseCustomResponse('{"error":{"message":" Wrong key "}}', 403, false, tpl, 1), { ok: false, status: 403, body: "Wrong key" });
  assert.deepEqual(bg.parseCustomResponse("<html>blocked</html>", 403, false, tpl, 1), { ok: false, status: 403, body: "<html>blocked</html>" });
  assert.deepEqual(bg.parseCustomResponse('{"error":{"message":"Quota exceeded"}}', 200, true, tpl, 1), { ok: false, status: 200, body: "Quota exceeded" });
  assert.deepEqual(bg.parseCustomResponse("<html>ok</html>", 200, true, tpl, 1), { ok: false, status: 200, body: "Response is not JSON: <html>ok</html>" });

  for (const raw of ['{"translations":[]}', '{"translations":[{"text":42}]}', '{"other":1}', "null"]) {
    const res = bg.parseCustomResponse(raw, 200, true, tpl, 1);
    assert.equal(res.ok, false);
    assert.equal(res.body, 'No translation at "translations[*].text" in: ' + raw);
  }
  const blank = bg.parseCustomResponse('{"data":{"out":"   "}}', 200, true, template({ customBody: '{"q": "{text}"}', customResultPath: "data.out" }), 1);
  assert.equal(blank.ok, false);
  assert.match(blank.body, /^No translation at "data\.out" in: /);
});

test("checkApiConfig 只检查自定义格式, 能工作的模板通过, 不能工作的给出原因", () => {
  assert.deepEqual(bg.checkApiConfig({ apiType: "openai", customResultPath: "" }), { ok: true });
  assert.deepEqual(bg.checkApiConfig({ apiType: "deeplx" }), { ok: true });
  assert.deepEqual(bg.checkApiConfig(DEEPL), { ok: true });
  assert.match(bg.checkApiConfig({ ...DEEPL, customResultPath: "" }).error, /result path is empty/);
  assert.match(bg.checkApiConfig({ ...DEEPL, customBody: '{"text": [{text}]}' }).error, /not valid JSON/);
});

test("streamTranslation: 按模板发送方法, 请求头和请求体, 不发提示词和术语表, 结果写入缓存", async () => {
  reset();
  const seen = [];
  const text = unique('The "quick" brown fox');
  const res = await bg.streamTranslation(text, (d) => seen.push(d));
  assert.deepEqual(res, ok(tr(text)));
  assert.deepEqual(seen, []);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, "https://api.example.test/v2/translate");
  assert.equal(calls[0].method, "POST");
  assert.deepEqual(calls[0].headers, { "Authorization": "DeepL-Auth-Key k 1/2", "Content-Type": "application/json" });
  assert.deepEqual(JSON.parse(calls[0].body), { text: [text], target_lang: "ZH" });

  assert.deepEqual(await bg.streamTranslation(text, () => {}), ok(tr(text), { cached: true }));
  assert.deepEqual(await bg.translate(text), ok(tr(text), { cached: true }));
  assert.equal(calls.length, 1);
});

test("GET 模板把原文和 key 放进地址, 不带请求体; 未填显示名时页脚用 Custom API", async () => {
  reset({
    customName: " ",
    customMethod: "GET",
    base: "https://api.example.test/t?q={text}&to={targetLangLower}&key={apiKey}",
    customHeaders: "",
    customResultPath: "data.out",
    target: "English"
  });
  respond = () => jsonResponse({ data: { out: "Hello" } });
  const res = await bg.translate(unique("你好"));
  assert.deepEqual(res, ok("Hello", { model: "Custom API" }));
  assert.equal(calls.length, 1);
  assert.equal(calls[0].method, "GET");
  assert.equal(calls[0].body, undefined);
  assert.deepEqual(calls[0].headers, {});
  assert.match(calls[0].url, /^https:\/\/api\.example\.test\/t\?q=%E4%BD%A0%E5%A5%BD%20\d+&to=en&key=k%201%2F2$/);
});

test("填写的中继作为完整地址模板使用, 只请求中继一次", async () => {
  reset({ proxy: "https://relay.example.test/https://api.example.test/v2/translate/" });
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.equal(res.ok, true);
  assert.equal(res.via, "relay");
  assert.deepEqual(calls.map((c) => c.url), ["https://relay.example.test/https://api.example.test/v2/translate"]);
});

test("请求失败时返回 HTTP 状态和错误路径的文字, 只请求一次, 失败结果不进缓存", async () => {
  reset();
  const text = unique("hello");

  respond = () => jsonResponse({ message: "Wrong endpoint. Use https://api-free.example.test" }, 403);
  assert.deepEqual(await bg.streamTranslation(text, () => {}), {
    ok: false,
    status: 403,
    body: "Wrong endpoint. Use https://api-free.example.test",
    via: "direct"
  });
  assert.equal(calls.length, 1);

  respond = () => new Response("<html>ok</html>", { status: 200, headers: { "content-type": "text/html" } });
  const html = await bg.translate(text);
  assert.deepEqual(html, { ok: false, status: 200, body: "Response is not JSON: <html>ok</html>", via: "direct" });

  // 批量模板在单条请求里数量不符时按普通错误返回, 不带内部标记.
  respond = () => jsonResponse({ translations: [{ text: "一" }, { text: "二" }] });
  assert.deepEqual(await bg.streamTranslation(text, () => {}), {
    ok: false,
    status: 200,
    body: 'Expected 1 translation(s) at "translations[*].text" but found 2.',
    via: "direct"
  });

  respond = () => {
    throw new TypeError("fetch failed");
  };
  assert.deepEqual(await bg.streamTranslation(text, () => {}), { ok: false, status: 0, body: "fetch failed" });

  reset();
  const recovered = await bg.streamTranslation(text, () => {});
  assert.equal(recovered.ok, true);
  assert.equal(recovered.cached, undefined);
});

test("无法工作的模板和已中止的调用都不发请求", async () => {
  reset({ customResultPath: "" });
  const res = await bg.streamTranslation(unique("hello"), () => {});
  assert.equal(res.ok, false);
  assert.equal(res.status, 0);
  assert.match(res.body, /^Custom API: the result path is empty/);
  const batch = await bg.streamImmersiveBatch([unique("a"), unique("b")], "", () => assert.fail("no progress expected"));
  assert.match(batch.body, /^Custom API: the result path is empty/);

  reset({ customBody: '{"text": [{text}]}' });
  assert.match((await bg.translate(unique("hello"))).body, /^Custom API: the request body is not valid JSON/);

  reset();
  const ctrl = new AbortController();
  ctrl.abort();
  assert.deepEqual(await bg.streamTranslation(unique("hello"), () => {}, ctrl.signal), { ok: false, cancelled: true, status: 0, body: "Cancelled" });
  assert.equal(calls.length, 0);
});

test("streamImmersiveBatch: {texts} 模板把一批段落作为数组一次发送, 含换行的段落也能合并, 结果按段进缓存", async () => {
  reset();
  const a = unique("first\nwith a line break");
  const b = unique("second");
  const progress = [];
  const res = await bg.streamImmersiveBatch([a, "", b], "Index-Translate-9B", (p) => progress.push(p));
  assert.deepEqual(res, batchOk({ 0: tr(a), 1: "", 2: tr(b) }));
  assert.deepEqual(progress, [{ segments: { 0: tr(a), 1: "", 2: tr(b) }, model: "DeepL", via: "direct" }]);
  assert.deepEqual(calls.map((c) => JSON.parse(c.body)), [{ text: [a, b], target_lang: "ZH" }]);

  assert.deepEqual(await bg.streamTranslation(a, () => {}), ok(tr(a), { cached: true }));

  const c = unique("third");
  const routes = [];
  const second = await bg.streamImmersiveBatch([a, c], "", (p) => routes.push(p.via));
  assert.deepEqual(second.segments, { 0: tr(a), 1: tr(c) });
  assert.deepEqual(routes, ["cache", "direct"]);
  assert.deepEqual(calls.map((x) => JSON.parse(x.body).text), [[a, b], [c]]);

  const cached = await bg.streamImmersiveBatch([a, b, c], "", () => assert.fail("no progress expected"));
  assert.deepEqual(cached, { ok: true, segments: { 0: tr(a), 1: tr(b), 2: tr(c) }, model: "DeepL", via: "direct", cached: true });
  assert.equal(calls.length, 2);
});

test("streamImmersiveBatch: {text} 模板每段请求一次, 个别失败时保留其余译文", async () => {
  reset({ customBody: '{"q": "{text}", "to": "{targetLangLower}"}', customResultPath: "out", customErrorPath: "error" });
  const [a, b, c] = [unique("one"), unique("two"), unique("three")];
  respond = (call) => {
    const q = JSON.parse(call.body).q;
    return q === b ? jsonResponse({ error: "Slowdown" }, 429) : jsonResponse({ out: tr(q) });
  };
  const sizes = [];
  const res = await bg.streamImmersiveBatch([a, b, c], "", (p) => sizes.push(Object.keys(p.segments).length));
  assert.deepEqual(res, batchOk({ 0: tr(a), 2: tr(c) }));
  assert.deepEqual(calls.map((x) => JSON.parse(x.body)), [
    { q: a, to: "zh" },
    { q: b, to: "zh" },
    { q: c, to: "zh" }
  ]);
  assert.deepEqual(sizes, [1, 2]);

  calls.length = 0;
  respond = () => jsonResponse({ error: "Slowdown" }, 429);
  const failed = await bg.streamImmersiveBatch([unique("x"), unique("y")], "", () => assert.fail("no progress expected"));
  assert.deepEqual(failed, { ok: false, status: 429, body: "Slowdown", via: "direct" });
  assert.equal(calls.length, 2);
});

test("streamImmersiveBatch: 批量请求失败时不再逐段重发; 返回数量对不上时改为逐段请求并记住该接口", async () => {
  reset();
  respond = () => jsonResponse({ message: "Quota exceeded" }, 456);
  const failed = await bg.streamImmersiveBatch([unique("x"), unique("y")], "", () => assert.fail("no progress expected"));
  assert.deepEqual(failed, { ok: false, status: 456, body: "Quota exceeded", via: "direct" });
  assert.equal(calls.length, 1);

  // 这个接口不管收到几条都只返回一条译文.
  reset({ base: "https://single.example.test/v2/translate" });
  respond = (call) => jsonResponse({ translations: [{ text: tr(JSON.parse(call.body).text.join(" ")) }] });
  const [a, b] = [unique("alpha"), unique("beta")];
  const res = await bg.streamImmersiveBatch([a, b], "", () => {});
  assert.deepEqual(res, batchOk({ 0: tr(a), 1: tr(b) }));
  assert.deepEqual(calls.map((x) => JSON.parse(x.body).text), [[a, b], [a], [b]]);

  calls.length = 0;
  const [c, d] = [unique("gamma"), unique("delta")];
  await bg.streamImmersiveBatch([c, d], "", () => {});
  assert.deepEqual(calls.map((x) => JSON.parse(x.body).text), [[c], [d]]);

  // 其他接口不受影响.
  reset();
  const [e, f] = [unique("epsilon"), unique("zeta")];
  await bg.streamImmersiveBatch([e, f], "", () => {});
  assert.deepEqual(calls.map((x) => JSON.parse(x.body).text), [[e, f]]);
});

test("学习卡和识图在自定义格式下直接报不支持, 不发请求", async () => {
  reset();
  const results = [
    await bg.streamStudyCard(unique("ephemeral"), "", () => {}),
    await bg.generateStudyCard(unique("ephemeral")),
    await bg.streamVisionTranslation(IMAGE, "", () => {})
  ];
  for (const res of results) {
    assert.equal(res.ok, false);
    assert.equal(res.status, 0);
    assert.match(res.body, /needs an OpenAI-compatible chat model\. The custom API format only translates text/);
  }
  assert.match(results[0].body, /^Study Card /);
  assert.match(results[2].body, /^Image translation /);
  assert.equal(calls.length, 0);
});
