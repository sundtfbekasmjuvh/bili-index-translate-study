// options.js
"use strict";

const DEFAULTS = {
  base: "https://index-translate.bilibili.com/v1",
  proxy: "",
  model: "Index-Translate-9B",
  immersiveModel: "Index-Translate-2B",
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

function fillForm(s, localCfg) {
  $("base").value = s.base;
  $("proxy").value = s.proxy;
  $("key").value = s.key;
  $("target").value = s.target;
  ensureSelectValue($("model"), s.model || DEFAULTS.model);
  ensureSelectValue($("immersiveModel"), s.immersiveModel || DEFAULTS.immersiveModel);
  $("glossary").value = (localCfg && localCfg.glossary) || "";
  syncGlossaryBadge();
}

function readForm() {
  return {
    sync: {
      base: stripSlashes($("base").value),
      proxy: stripSlashes($("proxy").value),
      model: $("model").value,
      immersiveModel: $("immersiveModel").value,
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

  const granted = await requestHostAccess([s.base, s.proxy]);
  await chrome.storage.sync.set(s);
  if (chrome.storage.local) {
    await chrome.storage.local.set(l);
  }
  syncGlossaryBadge();

  if (granted) {
    const rules = countGlossaryRules(l.glossary);
    setStatus("Saved" + (rules > 0 ? " (" + rules + " glossary rules active)." : "."));
  } else {
    setStatus("Saved, but host access was declined - requests to that host may fail (CORS).", true);
  }
  return s;
}

async function test() {
  const s = await save();
  if (!s) return;

  setStatus("Testing " + s.model + " ...");
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
