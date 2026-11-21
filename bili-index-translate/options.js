// options.js
"use strict";

const DEFAULTS = {
  base: "https://index-translate.bilibili.com/v1",
  proxy: "",
  apiType: "openai",
  customName: "",
  customMethod: "POST",
  customHeaders: "",
  customBody: "",
  customResultPath: "",
  customErrorPath: "",
  model: "Index-Translate-9B",
  immersiveModel: "Index-Translate-2B",
  visionModel: "Index-Translate-35B-A3B",
  target: "中文",
  uiLang: "auto"
};

// chrome.storage.local: the API key stays on this device and is not synced to the browser account.
const LOCAL_DEFAULTS = {
  glossary: "",
  key: "bili"
};

const TEST_TEXT = "The quick brown fox jumps over the lazy dog.";

function $(id) {
  return document.getElementById(id);
}

// Interface language of this page: the saved setting, or what the language box currently shows.
let uiLang = BiliI18n.resolveLang("auto");
const englishPageText = new Map();

function tr(key, ...args) {
  return BiliI18n.translate(uiLang, key, ...args);
}

function normalizeUiLang(value) {
  return BiliI18n.LANGS.includes(value) ? value : "auto";
}

// The English text is the markup of options.html. Another language replaces the content of every
// element with a data-i18n key; going back to English restores the markup that was there at load.
function applyPageLanguage(setting) {
  uiLang = BiliI18n.resolveLang(setting);
  document.documentElement.lang = uiLang;
  const page = BiliI18n.PAGE_TEXT[uiLang] || {};
  for (const el of document.querySelectorAll("[data-i18n]")) {
    if (!englishPageText.has(el)) englishPageText.set(el, el.innerHTML);
    const html = page[el.dataset.i18n];
    el.innerHTML = html !== undefined ? html : englishPageText.get(el);
  }
  for (const el of document.querySelectorAll("[data-i18n-placeholder]")) {
    if (!englishPageText.has(el)) englishPageText.set(el, el.placeholder);
    const text = page[el.dataset.i18nPlaceholder];
    el.placeholder = text !== undefined ? text : englishPageText.get(el);
  }
  syncGlossaryBadge();
}

function setStatus(text, isError) {
  const el = $("status");
  el.textContent = text;
  el.className = isError ? "err" : "ok";
}

function stripSlashes(url) {
  return String(url || "").trim().replace(/\/+$/, "");
}

function isHttpUrl(url) {
  try {
    const u = new URL(url);
    return u.protocol === "http:" || u.protocol === "https:";
  } catch (_) {
    return false;
  }
}

function countGlossaryRules(raw) {
  const seen = new Set();
  for (const rawLine of String(raw || "").split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#") || line.startsWith("//")) continue;
    const m = /^(.*?)\s*(?:=>|->|→|\t|=)\s*(.+)$/.exec(line);
    const src = (m ? m[1] : line).trim().toLowerCase();
    if (src) seen.add(src);
  }
  return seen.size;
}

function syncGlossaryBadge() {
  const n = countGlossaryRules($("glossary").value);
  $("glossaryCount").textContent = n === 1 ? tr("opt.ruleOne") : tr("opt.ruleMany", n);
}

function ensureSelectValue(select, val) {
  if (!Array.from(select.options).some((o) => o.value === val)) {
    select.add(new Option(val, val));
  }
  select.value = val;
}

// Starting points for the custom format, taken from each service's API reference. They fill in the
// form only; nothing is saved until the user presses Save.
const CUSTOM_PRESETS = {
  "deepl-free": {
    base: "https://api-free.deepl.com/v2/translate",
    customName: "DeepL",
    customMethod: "POST",
    customHeaders: "Authorization: DeepL-Auth-Key {apiKey}",
    customBody: '{"text": {texts}, "target_lang": "{targetLang}"}',
    customResultPath: "translations[*].text",
    customErrorPath: ""
  },
  "deepl-pro": {
    base: "https://api.deepl.com/v2/translate",
    customName: "DeepL",
    customMethod: "POST",
    customHeaders: "Authorization: DeepL-Auth-Key {apiKey}",
    customBody: '{"text": {texts}, "target_lang": "{targetLang}"}',
    customResultPath: "translations[*].text",
    customErrorPath: ""
  },
  libretranslate: {
    base: "https://libretranslate.com/translate",
    customName: "LibreTranslate",
    customMethod: "POST",
    customHeaders: "",
    customBody: '{"q": {texts}, "source": "auto", "target": "{targetLangLower}", "format": "text", "api_key": "{apiKey}"}',
    customResultPath: "translatedText",
    customErrorPath: "error"
  }
};

const CUSTOM_FIELDS = ["customName", "customMethod", "customHeaders", "customBody", "customResultPath", "customErrorPath"];

function normalizeApiType(value) {
  return value === "deeplx" || value === "custom" ? value : "openai";
}

// The model choices and the glossary only apply to chat models; the text-only formats ignore them.
function syncApiTypeUi() {
  const type = $("apiType").value;
  for (const id of ["model", "immersiveModel", "visionModel", "glossary"]) $(id).disabled = type !== "openai";
  $("customFields").hidden = type !== "custom";
  $("customBody").disabled = $("customMethod").value === "GET";
  $("base").placeholder =
    type === "deeplx"
      ? "https://host/translate?token={apiKey}"
      : type === "custom"
      ? "https://api.example.com/translate"
      : DEFAULTS.base;
}

function applyCustomPreset(name) {
  const preset = CUSTOM_PRESETS[name];
  if (!preset) return;
  $("base").value = preset.base;
  for (const id of CUSTOM_FIELDS) $(id).value = preset[id];
  syncApiTypeUi();
  setStatus(tr("opt.presetLoaded", preset.customName));
}

// Catches an endpoint entered under the wrong API format. "blocking" marks a combination that cannot
// work; the others are hints, because an unusual URL may still be what the user means.
function endpointFormatMismatch(s) {
  if (s.apiType === "custom") return null; // the template decides what the URL looks like
  let path = "";
  try {
    path = new URL(s.proxy || s.base).pathname.replace(/\/+$/, "");
  } catch (_) {}
  if (s.apiType === "deeplx") {
    return /\/chat\/completions$|\/v\d+$/i.test(path)
      ? { blocking: false, text: tr("opt.mismatchOpenAiRoot") }
      : null;
  }
  if (/\{\{?apiKey\}?\}/i.test(s.base + " " + s.proxy)) {
    return { blocking: true, text: tr("opt.mismatchApiKey") };
  }
  return /\/translate$/i.test(path)
    ? { blocking: false, text: tr("opt.mismatchDeeplx") }
    : null;
}

function fillForm(s, localCfg) {
  $("base").value = s.base;
  $("apiType").value = normalizeApiType(s.apiType);
  $("customName").value = s.customName || "";
  $("customMethod").value = s.customMethod === "GET" ? "GET" : "POST";
  $("customHeaders").value = s.customHeaders || "";
  $("customBody").value = s.customBody || "";
  $("customResultPath").value = s.customResultPath || "";
  $("customErrorPath").value = s.customErrorPath || "";
  $("customPreset").value = "";
  $("proxy").value = s.proxy;
  $("key").value = String((localCfg && localCfg.key) ?? "");
  $("target").value = s.target;
  $("uiLang").value = normalizeUiLang(s.uiLang);
  applyPageLanguage($("uiLang").value);
  ensureSelectValue($("model"), s.model || DEFAULTS.model);
  ensureSelectValue($("immersiveModel"), s.immersiveModel || DEFAULTS.immersiveModel);
  ensureSelectValue($("visionModel"), s.visionModel || DEFAULTS.visionModel);
  $("glossary").value = (localCfg && localCfg.glossary) || "";
  syncGlossaryBadge();
  syncApiTypeUi();
}

function readForm() {
  return {
    sync: {
      base: stripSlashes($("base").value),
      apiType: normalizeApiType($("apiType").value),
      customName: $("customName").value.trim(),
      customMethod: $("customMethod").value === "GET" ? "GET" : "POST",
      customHeaders: $("customHeaders").value.replace(/\r\n/g, "\n").trim(),
      customBody: $("customBody").value.replace(/\r\n/g, "\n").trim(),
      customResultPath: $("customResultPath").value.trim(),
      customErrorPath: $("customErrorPath").value.trim(),
      proxy: stripSlashes($("proxy").value),
      model: $("model").value,
      immersiveModel: $("immersiveModel").value,
      visionModel: $("visionModel").value,
      target: $("target").value.trim() || DEFAULTS.target,
      uiLang: normalizeUiLang($("uiLang").value)
    },
    local: {
      glossary: $("glossary").value.replace(/\r\n/g, "\n").trim(),
      key: $("key").value.trim()
    }
  };
}

function requestHostAccess(urls) {
  const origins = urls.filter(Boolean).map((u) => new URL(u).origin + "/*");
  return chrome.permissions.request({ origins: origins }).catch(() => false);
}

async function save() {
  const { sync: s, local: l } = readForm();
  if (!isHttpUrl(s.base)) {
    setStatus(tr("opt.baseInvalid"), true);
    return null;
  }
  if (s.proxy && !isHttpUrl(s.proxy)) {
    setStatus(tr("opt.proxyInvalid"), true);
    return null;
  }
  const mismatch = endpointFormatMismatch(s);
  if (mismatch && mismatch.blocking) {
    setStatus(mismatch.text, true);
    return null;
  }
  // The background worker owns the template rules; ask it instead of repeating them here.
  const check = await chrome.runtime
    .sendMessage({ type: "BILI_CHECK_API_CONFIG", settings: { ...s, key: l.key } })
    .catch(() => null);
  if (check && check.ok === false) {
    setStatus(check.error, true);
    return null;
  }

  const granted = await requestHostAccess([s.base, s.proxy]);
  await chrome.storage.sync.set(s);
  if (chrome.storage.local) {
    await chrome.storage.local.set(l);
  }
  syncGlossaryBadge();

  if (granted) {
    const rules = s.apiType === "openai" ? countGlossaryRules(l.glossary) : 0;
    const saved = rules > 0 ? tr("opt.savedRules", rules) : tr("opt.saved");
    if (mismatch) setStatus(saved + "\n" + mismatch.text, true);
    else setStatus(saved);
  } else {
    setStatus(tr("opt.savedNoHost"), true);
  }
  return s;
}

async function test() {
  const s = await save();
  if (!s) return;

  const label = s.apiType === "deeplx" ? "DeepLX" : s.apiType === "custom" ? s.customName || "Custom API" : s.model;
  setStatus(tr("opt.testing", label));
  let res;
  try {
    res = await chrome.runtime.sendMessage({ type: "BILI_TRANSLATE", text: TEST_TEXT });
  } catch (e) {
    res = { ok: false, status: 0, body: String((e && e.message) || e) };
  }

  if (res && res.ok) {
    setStatus(tr("opt.ok", res.via, res.model, res.text));
  } else {
    const status = res && res.status ? "HTTP " + res.status + " " : "";
    setStatus(tr("opt.failed", status + String((res && res.body) || tr("opt.noResponse")).slice(0, 300)), true);
  }
}

document.addEventListener("DOMContentLoaded", async () => {
  // A key left in chrome.storage.sync by an older version is moved by the background worker; wait for
  // that before reading. If the worker cannot be reached, it moves the key on its next request instead.
  await chrome.runtime.sendMessage({ type: "BILI_MIGRATE_SETTINGS" }).catch(() => null);
  const syncCfg = await chrome.storage.sync.get(DEFAULTS);
  const localCfg = chrome.storage.local ? await chrome.storage.local.get(LOCAL_DEFAULTS) : LOCAL_DEFAULTS;
  fillForm(syncCfg, localCfg);

  $("glossary").addEventListener("input", syncGlossaryBadge);
  $("apiType").addEventListener("change", syncApiTypeUi);
  // Shows the page in the chosen language at once; like every other field it is stored by Save.
  $("uiLang").addEventListener("change", () => applyPageLanguage($("uiLang").value));
  $("customMethod").addEventListener("change", syncApiTypeUi);
  $("customPreset").addEventListener("change", () => applyCustomPreset($("customPreset").value));
  $("form").addEventListener("submit", (e) => {
    e.preventDefault();
    save();
  });
  $("test").addEventListener("click", test);
  $("reset").addEventListener("click", async () => {
    fillForm(DEFAULTS, LOCAL_DEFAULTS);
    await chrome.storage.sync.set(DEFAULTS);
    if (chrome.storage.local) await chrome.storage.local.set(LOCAL_DEFAULTS);
    setStatus(tr("opt.reset"));
  });
});
