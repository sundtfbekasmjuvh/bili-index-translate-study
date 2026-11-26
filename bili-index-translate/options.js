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
  key: "bili",
  // "Annotate hard words only". zh is an HSK level (1-7, 7 = HSK 7-9); en and es are frequency-rank
  // cut-offs. The saved words (annotateKnown) are not here: only the background worker writes them.
  annotateEnabled: false,
  annotateLevels: { zh: 4, en: 5000, es: 5000 }
};

const ANNOTATE_LANGS = ["zh", "en", "es"];
const ANNOTATE_HSK_LEVELS = [1, 2, 3, 4, 5, 6, 7];
const ANNOTATE_FREQ_STEPS = [1000, 2000, 3000, 5000, 10000, 20000];
const ANNOTATE_SHOWN = 200; // words listed per language until "Show all" is pressed
const ANNOTATE_CONFIRM_MS = 5000;
const ANNOTATE_LANG_KEY = { zh: "opt.annot.langZh", en: "opt.annot.langEn", es: "opt.annot.langEs" };
const ANNOTATE_LEVEL_IDS = { zh: "annotateLevelZh", en: "annotateLevelEn", es: "annotateLevelEs" };

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
  buildLevelOptions();
  renderAnnotateKnown();
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

// ---- Annotate hard words only ----------------------------------------------------------------

// A missing key or a value of the wrong type falls back to the default of that language.
function normalizeAnnotateLevels(raw) {
  const def = LOCAL_DEFAULTS.annotateLevels;
  const src = raw && typeof raw === "object" ? raw : {};
  return {
    zh: ANNOTATE_HSK_LEVELS.includes(src.zh) ? src.zh : def.zh,
    en: ANNOTATE_FREQ_STEPS.includes(src.en) ? src.en : def.en,
    es: ANNOTATE_FREQ_STEPS.includes(src.es) ? src.es : def.es
  };
}

// The options are built here because their text carries a number; the selected values are kept
// when the interface language changes.
function buildLevelOptions() {
  const fill = (id, entries) => {
    const select = $(id);
    const keep = select.value;
    select.replaceChildren(...entries.map(([value, text]) => new Option(text, String(value))));
    if (keep) select.value = keep;
  };
  fill(
    ANNOTATE_LEVEL_IDS.zh,
    ANNOTATE_HSK_LEVELS.map((n) => [n, n === 7 ? tr("opt.annot.hsk79") : tr("opt.annot.hsk", n)])
  );
  const freq = ANNOTATE_FREQ_STEPS.map((n) => [n, tr("opt.annot.top", n.toLocaleString("en-US"))]);
  fill(ANNOTATE_LEVEL_IDS.en, freq);
  fill(ANNOTATE_LEVEL_IDS.es, freq);
}

function fillAnnotate(localCfg) {
  $("annotateEnabled").checked = !!localCfg && localCfg.annotateEnabled === true;
  const levels = normalizeAnnotateLevels(localCfg && localCfg.annotateLevels);
  for (const lang of ANNOTATE_LANGS) $(ANNOTATE_LEVEL_IDS[lang]).value = String(levels[lang]);
}

function readAnnotate() {
  const levels = {};
  for (const lang of ANNOTATE_LANGS) levels[lang] = Number($(ANNOTATE_LEVEL_IDS[lang]).value);
  return { annotateEnabled: $("annotateEnabled").checked, annotateLevels: normalizeAnnotateLevels(levels) };
}

// The saved words, as listed by the background worker; null until they have been read.
let annotateKnown = null;
let annotateLoadFailed = false;
const annotateExpanded = { zh: false, en: false, es: false };
let clearArmed = false;
let clearTimer = 0;
let clearArmedAt = 0;
// A second click this soon after the first is the same double-click, not a confirmation.
const ANNOTATE_CONFIRM_MIN_MS = 500;

function annotateTotal() {
  return annotateKnown ? ANNOTATE_LANGS.reduce((n, lang) => n + annotateKnown[lang].length, 0) : 0;
}

function disarmClear() {
  clearTimeout(clearTimer);
  clearArmed = false;
  $("annotateClear").textContent = tr("opt.annot.clear");
}

function renderAnnotateKnown() {
  disarmClear();
  const count = $("annotateCount");
  const details = $("annotateDetails");
  const list = $("annotateList");
  list.replaceChildren();
  $("annotateClear").disabled = annotateTotal() === 0;
  if (!annotateKnown) {
    count.textContent = annotateLoadFailed ? tr("opt.annot.loadFailed") : "";
    details.hidden = true;
    return;
  }
  if (annotateTotal() === 0) {
    count.textContent = tr("opt.annot.empty");
    details.hidden = true;
    return;
  }
  const n = annotateKnown;
  count.textContent = tr("opt.annot.count", annotateTotal(), n.zh.length, n.en.length, n.es.length);
  details.hidden = false;
  for (const lang of ANNOTATE_LANGS) {
    const words = n[lang];
    if (words.length === 0) continue;
    const group = document.createElement("div");
    group.className = "known-group";
    group.dataset.lang = lang;
    const head = document.createElement("strong");
    head.tabIndex = -1;
    head.textContent = tr("opt.annot.group", tr(ANNOTATE_LANG_KEY[lang]), words.length);
    const ul = document.createElement("ul");
    ul.setAttribute("aria-label", head.textContent);
    // The newest words come first; a long list shows only the latest ones until "Show all".
    const shown = annotateExpanded[lang] ? words.length : Math.min(words.length, ANNOTATE_SHOWN);
    for (let i = words.length - 1; i >= words.length - shown; i--) {
      const li = document.createElement("li");
      const word = document.createElement("span");
      word.textContent = words[i];
      const del = document.createElement("button");
      del.type = "button";
      del.className = "word-del";
      del.dataset.lang = lang;
      del.dataset.key = words[i];
      del.textContent = "×";
      del.setAttribute("aria-label", tr("opt.annot.del", words[i]));
      li.append(word, del);
      ul.append(li);
    }
    group.append(head, ul);
    if (shown < words.length) {
      const more = document.createElement("button");
      more.type = "button";
      more.className = "known-more";
      more.dataset.lang = lang;
      more.textContent = tr("opt.annot.showAll", words.length);
      group.append(more);
    }
    list.append(group);
  }
}

async function loadAnnotateKnown() {
  let res = null;
  try {
    res = await chrome.runtime.sendMessage({ type: "BILI_ANNOTATE_LIST" });
  } catch (_) {}
  const words = res && res.ok && res.words && typeof res.words === "object" ? res.words : null;
  annotateLoadFailed = !words;
  annotateKnown = words
    ? Object.fromEntries(
        ANNOTATE_LANGS.map((lang) => [lang, Array.isArray(words[lang]) ? words[lang].filter((w) => typeof w === "string") : []])
      )
    : null;
  renderAnnotateKnown();
}

async function removeKnownWord(button) {
  const { lang, key } = button.dataset;
  if (button.disabled) return;
  button.disabled = true;
  let res = null;
  try {
    res = await chrome.runtime.sendMessage({ type: "BILI_ANNOTATE_REMOVE", lang, key });
  } catch (_) {}
  if (!res || res.ok !== true) {
    button.disabled = false;
    setStatus(tr("opt.annot.removeFailed", key), true);
    return;
  }
  const at = button.closest("ul") ? [...button.closest("ul").querySelectorAll(".word-del")].indexOf(button) : 0;
  const words = annotateKnown && annotateKnown[lang];
  if (words && words.includes(key)) words.splice(words.indexOf(key), 1);
  renderAnnotateKnown();
  // The focused button is gone: move focus to its neighbour so keyboard users can keep deleting.
  const rest = document.querySelectorAll('#annotateList .known-group[data-lang="' + lang + '"] .word-del');
  const next = rest[Math.min(at, rest.length - 1)];
  if (next) next.focus();
  else if ($("annotateDetails").hidden) $("annotateEnabled").focus();
  else $("annotateDetails").querySelector("summary").focus();
  setStatus(tr("opt.annot.removed", key));
}

async function clearKnownWords() {
  if (!clearArmed) {
    clearArmed = true;
    clearArmedAt = Date.now();
    $("annotateClear").textContent = tr("opt.annot.clearConfirm");
    setStatus(tr("opt.annot.clearArmed"));
    clearTimer = setTimeout(disarmClear, ANNOTATE_CONFIRM_MS);
    return;
  }
  if (Date.now() - clearArmedAt < ANNOTATE_CONFIRM_MIN_MS) return;
  disarmClear();
  let res = null;
  try {
    res = await chrome.runtime.sendMessage({ type: "BILI_ANNOTATE_CLEAR" });
  } catch (_) {}
  if (!res || res.ok !== true) {
    setStatus(tr("opt.annot.clearFailed"), true);
    return;
  }
  for (const lang of ANNOTATE_LANGS) annotateExpanded[lang] = false;
  await loadAnnotateKnown();
  setStatus(tr("opt.annot.cleared"));
}

function onAnnotateListClick(e) {
  const target = e.target instanceof Element ? e.target : null;
  const del = target && target.closest("button.word-del");
  if (del) {
    removeKnownWord(del);
    return;
  }
  const more = target && target.closest("button.known-more");
  if (more) {
    annotateExpanded[more.dataset.lang] = true;
    renderAnnotateKnown();
    const head = document.querySelector('#annotateList .known-group[data-lang="' + more.dataset.lang + '"] strong');
    if (head) head.focus();
  }
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
  fillAnnotate(localCfg);
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
      key: $("key").value.trim(),
      ...readAnnotate()
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
  $("annotateList").addEventListener("click", onAnnotateListClick);
  $("annotateClear").addEventListener("click", clearKnownWords);
  loadAnnotateKnown();
  $("reset").addEventListener("click", async () => {
    fillForm(DEFAULTS, LOCAL_DEFAULTS);
    await chrome.storage.sync.set(DEFAULTS);
    // LOCAL_DEFAULTS turns annotation off and restores the levels; the saved words are not touched.
    if (chrome.storage.local) await chrome.storage.local.set(LOCAL_DEFAULTS);
    setStatus(tr("opt.reset") + (annotateTotal() > 0 ? "\n" + tr("opt.annot.resetKept") : ""));
  });
});
