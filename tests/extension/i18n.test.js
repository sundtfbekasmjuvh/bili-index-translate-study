"use strict";

// 界面语言: 两种语言的文案键和占位符一致, 语言解析规则, 选项页静态文案与 options.html 的标记一致, 后台的菜单标题和错误文案跟随设置.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const { storageArea } = require("../support/fake-storage");

const EXT = path.resolve(__dirname, "../../bili-index-translate");
const i18n = require(path.join(EXT, "i18n.js"));

const sync = {};
const local = {};
globalThis.chrome = { storage: { sync: storageArea(sync), local: storageArea(local) } };
globalThis.fetch = async () => {
  throw new Error("this test sends no requests");
};
const bg = require(path.join(EXT, "background.js"));

const placeholders = (s) => (s.match(/\{[A-Za-z0-9]+\}/g) || []).sort();
const tags = (html) => (html.match(/<(code|kbd|strong)>/g) || []).sort();

test("两种语言的文案键相同, 都不为空, 占位符一致", () => {
  const en = i18n.MESSAGES.en;
  const zh = i18n.MESSAGES["zh-CN"];
  assert.deepEqual(Object.keys(zh).sort(), Object.keys(en).sort());
  for (const key of Object.keys(en)) {
    assert.ok(en[key].trim() && zh[key].trim(), key + " is empty");
    assert.deepEqual(placeholders(zh[key]), placeholders(en[key]), key + " placeholders");
  }
});

test("resolveLang: 明确的设置优先, auto 跟随浏览器语言, 中文各地区都用简体中文, 其余用英文", () => {
  const cases = [
    ["en", "zh-CN", "en"],
    ["zh-CN", "en-US", "zh-CN"],
    ["auto", "zh-CN", "zh-CN"],
    ["auto", "zh-TW", "zh-CN"],
    ["auto", "zh", "zh-CN"],
    ["auto", "en-GB", "en"],
    ["auto", "ja", "en"],
    ["auto", "zhx", "en"],
    ["auto", "", "en"],
    ["fr", "zh-CN", "zh-CN"],
    [undefined, "zh-CN", "zh-CN"],
    ["  zh-CN ", "en", "zh-CN"]
  ];
  for (const [setting, browser, expected] of cases) {
    assert.equal(i18n.resolveLang(setting, browser), expected, JSON.stringify([setting, browser]));
  }
  // Node 里没有 chrome.i18n, auto 按英文处理.
  assert.equal(i18n.browserLanguage(), "");
  assert.equal(i18n.resolveLang("auto"), "en");
});

test("translate: 按序号填入参数, 保留 {text} 这类字面占位符, 未知语言回退英文, 未知键返回键名", () => {
  assert.equal(i18n.translate("zh-CN", "imm.ready", 5, 6), "双语就绪 5/6");
  assert.equal(i18n.translate("en", "imm.ready", 5, 6), "Ready 5/6");
  assert.equal(i18n.translate("zh-CN", "bg.customCount", 3, "a.b", 1), '"a.b" 处应有 3 条译文，实际有 1 条。');
  assert.match(i18n.translate("zh-CN", "bg.custom.noText"), /\{text\}.*\{texts\}/);
  assert.equal(i18n.translate("en", "panel.glossaryHits"), "glossary ×{0}");
  assert.equal(i18n.translate("ja", "opt.saved"), "Saved.");
  assert.equal(i18n.translate("zh-CN", "no.such.key"), "no.such.key");
});

test("选项页: 每个 data-i18n 键都有中文文案, 没有多余的键, 中文里的 code/kbd/strong 标记数与英文标记一致", () => {
  const html = fs.readFileSync(path.join(EXT, "options.html"), "utf8");
  const zh = i18n.PAGE_TEXT["zh-CN"];
  const content = {};
  for (const m of html.matchAll(/<(\w+)[^>]*\sdata-i18n="([^"]+)"[^>]*>([\s\S]*?)<\/\1>/g)) content[m[2]] = m[3];
  const attrKeys = [...html.matchAll(/data-i18n-placeholder="([^"]+)"/g)].map((m) => m[1]);

  assert.deepEqual(Object.keys(zh).sort(), [...Object.keys(content), ...attrKeys].sort());
  for (const [key, english] of Object.entries(content)) {
    assert.deepEqual(tags(zh[key]), tags(english), key + " markup");
    assert.equal(/<(?!\/?(code|kbd|strong)>)/.test(zh[key]), false, key + " may only use code, kbd and strong");
  }
  for (const key of attrKeys) assert.equal(zh[key].includes("<"), false, key + " is plain text, not markup");
  assert.ok(html.indexOf('src="i18n.js"') < html.indexOf('src="options.js"'), "i18n.js loads before options.js");
});

const MENU_IDS = ["bili-translate-selection", "bili-study-selection", "bili-translate-image", "bili-immersive-page"];

async function useLang(setting) {
  for (const k of Object.keys(sync)) delete sync[k];
  if (setting !== undefined) sync.uiLang = setting;
  await bg.getSettings();
}

test("后台: 界面语言设为中文后, 菜单标题, 校验提示和错误文案都是中文", async () => {
  await useLang("zh-CN");
  assert.deepEqual(bg.menuTitles(), {
    "bili-translate-selection": "用 Bili 翻译选中的文本",
    "bili-study-selection": "打开 Bili 学习卡片",
    "bili-translate-image": "用 Bili 识图翻译这张图片",
    "bili-immersive-page": "开关双语对照网页 (Alt+A)"
  });
  assert.deepEqual(await bg.translate("   "), { ok: false, status: 0, body: "没有可翻译的内容。" });
  assert.deepEqual(await bg.translate("x".repeat(3001)), { ok: false, status: 0, body: "选中的文本超过 3000 个字符。" });
  await assert.rejects(bg.fetchImageAsDataUrl(""), /^Error: 图片地址为空。$/);
  assert.deepEqual(bg.checkApiConfig({ apiType: "custom", base: "https://api.example.test/t", customBody: '{"q": "{text}"}' }), {
    ok: false,
    error: "自定义接口：结果路径为空。它指出译文在 JSON 应答里的位置，例如 translations[0].text。"
  });
  assert.deepEqual(bg.parseDeeplxResponse("<html>", 200), { ok: false, status: 200, body: "响应不是 JSON：<html>" });
});

test("后台: 设为英文或 auto (Node 里没有浏览器语言) 时是英文, 设置改变后下一次读取即生效", async () => {
  for (const setting of ["en", "auto", undefined, "fr"]) {
    await useLang(setting);
    assert.deepEqual(Object.keys(bg.menuTitles()), MENU_IDS);
    assert.equal(bg.menuTitles()["bili-translate-selection"], "Translate selection with Bili", String(setting));
    assert.deepEqual(await bg.translate(""), { ok: false, status: 0, body: "Nothing to translate." });
  }
  await useLang("zh-CN");
  assert.equal(bg.menuTitles()["bili-translate-selection"], "用 Bili 翻译选中的文本");
});

test("扩展的 _locales: manifest 引用的消息在两种语言里都有, 两个文件的键相同", () => {
  const manifestText = fs.readFileSync(path.join(EXT, "manifest.json"), "utf8");
  const manifest = JSON.parse(manifestText);
  const used = [...manifestText.matchAll(/__MSG_(\w+)__/g)].map((m) => m[1]).sort();
  assert.equal(manifest.default_locale, "en");
  assert.ok(used.length > 0);
  for (const locale of ["en", "zh_CN"]) {
    const messages = JSON.parse(fs.readFileSync(path.join(EXT, "_locales", locale, "messages.json"), "utf8"));
    assert.deepEqual(Object.keys(messages).sort(), used, locale);
    for (const key of used) assert.ok(String(messages[key].message || "").trim(), locale + "." + key);
  }
});
