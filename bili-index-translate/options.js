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
  key: "bili",
  target: "中文"
};

const LOCAL_DEFAULTS = {
  glossary: ""
};

const TEST_TEXT = "The quick brown fox jumps over the lazy dog.";

function $(id) {
  return document.getElementById(id);
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
  $("glossaryCount").textContent = n + (n === 1 ? " rule" : " rules");
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
  setStatus("Preset loaded: " + preset.customName + ". Check the URL, enter your API key, then press Save & test.");
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
      ? {
          blocking: false,
          text: "This URL looks like an OpenAI-compatible root. The DeepLX format needs the full translate URL (it usually ends in /translate)."
        }
      : null;
  }
  if (/\{\{?apiKey\}?\}/i.test(s.base + " " + s.proxy)) {
    return {
      blocking: true,
      text: "The URL contains {apiKey}, which the OpenAI-compatible format does not fill in. Choose DeepLX-compatible or Custom JSON API, or remove the placeholder."
    };
  }
  return /\/translate$/i.test(path)
    ? { blocking: false, text: "This URL looks like a DeepLX endpoint. If requests fail, set API format to DeepLX-compatible." }
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
  $("key").value = s.key;
  $("target").value = s.target;
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
      key: $("key").value.trim(),
      target: $("target").value.trim() || DEFAULTS.target
    },
    local: {
      glossary: $("glossary").value.replace(/\r\n/g, "\n").trim()
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
    setStatus("Base URL must be an http(s) URL.", true);
    return null;
  }
  if (s.proxy && !isHttpUrl(s.proxy)) {
    setStatus("Relay prefix must be empty or an http(s) URL.", true);
    return null;
  }
  const mismatch = endpointFormatMismatch(s);
  if (mismatch && mismatch.blocking) {
    setStatus(mismatch.text, true);
    return null;
  }
  // The background worker owns the template rules; ask it instead of repeating them here.
  const check = await chrome.runtime.sendMessage({ type: "BILI_CHECK_API_CONFIG", settings: s }).catch(() => null);
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
    const saved = "Saved" + (rules > 0 ? " (" + rules + " glossary rules active)." : ".");
    if (mismatch) setStatus(saved + "\n" + mismatch.text, true);
    else setStatus(saved);
  } else {
    setStatus("Saved, but host access was declined - requests to that host may fail (CORS).", true);
  }
  return s;
}

async function test() {
  const s = await save();
  if (!s) return;

  const label = s.apiType === "deeplx" ? "DeepLX" : s.apiType === "custom" ? s.customName || "Custom API" : s.model;
  setStatus("Testing " + label + " ...");
  let res;
  try {
    res = await chrome.runtime.sendMessage({ type: "BILI_TRANSLATE", text: TEST_TEXT });
  } catch (e) {
    res = { ok: false, status: 0, body: String((e && e.message) || e) };
  }

  if (res && res.ok) {
    setStatus("OK via " + res.via + " (" + res.model + "):\n" + res.text);
  } else {
    const status = res && res.status ? "HTTP " + res.status + " " : "";
    setStatus("Failed: " + status + String((res && res.body) || "no response").slice(0, 300), true);
  }
}

document.addEventListener("DOMContentLoaded", async () => {
  const syncCfg = await chrome.storage.sync.get(DEFAULTS);
  const localCfg = chrome.storage.local ? await chrome.storage.local.get(LOCAL_DEFAULTS) : LOCAL_DEFAULTS;
  fillForm(syncCfg, localCfg);

  $("glossary").addEventListener("input", syncGlossaryBadge);
  $("apiType").addEventListener("change", syncApiTypeUi);
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
    setStatus("Defaults restored.");
  });
});
