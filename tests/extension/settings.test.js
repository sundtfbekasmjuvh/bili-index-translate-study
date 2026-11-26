"use strict";

// API key 的存放位置: 只在 chrome.storage.local, 旧版本留在 chrome.storage.sync 的 key 会被移过来并删除同步副本.
const test = require("node:test");
const assert = require("node:assert/strict");
const { storageArea } = require("../support/fake-storage");

const sync = {};
const local = {};
const calls = [];

globalThis.chrome = { storage: { sync: storageArea(sync), local: storageArea(local) } };
globalThis.fetch = async (url, init) => {
  calls.push({ url: String(url), headers: init.headers });
  return new Response(JSON.stringify({ choices: [{ message: { content: "你好" } }] }), {
    status: 200,
    headers: { "content-type": "application/json" }
  });
};

const bg = require("../../bili-index-translate/background.js");

function reset(syncData = {}, localData = {}) {
  calls.length = 0;
  for (const k of Object.keys(sync)) delete sync[k];
  for (const k of Object.keys(local)) delete local[k];
  Object.assign(sync, syncData);
  Object.assign(local, localData);
}

test("全新安装: key 取默认值, 两个存储区都不被写入", async () => {
  reset();
  const s = await bg.getSettings();
  assert.equal(s.key, "bili");
  assert.equal(s.base, "https://index-translate.bilibili.com/v1");
  assert.equal(s.glossary, "");
  assert.deepEqual(sync, {});
  assert.deepEqual(local, {});
});

test("旧版本存在 sync 里的 key 移到 local, 同步副本被删除, 其余同步设置不动", async () => {
  reset({ key: "sk-legacy", model: "Index-Translate-35B-A3B", target: "English" }, { glossary: "fox => 狐狸" });
  assert.equal(await bg.migrateKeyToLocal(), true);
  assert.deepEqual(sync, { model: "Index-Translate-35B-A3B", target: "English" });
  assert.deepEqual(local, { glossary: "fox => 狐狸", key: "sk-legacy" });

  assert.equal(await bg.migrateKeyToLocal(), false, "第二次没有可移的内容");
  const s = await bg.getSettings();
  assert.equal(s.key, "sk-legacy");
  assert.equal(s.model, "Index-Translate-35B-A3B");
  assert.equal(s.glossary, "fox => 狐狸");
});

test("getSettings 自行完成迁移, 请求带的是迁移后的 key", async () => {
  reset({ key: "sk-legacy-2" });
  const res = await bg.translate("hello " + Date.now());
  assert.equal(res.ok, true);
  assert.equal(calls.length, 1);
  assert.equal(calls[0].headers.Authorization, "Bearer sk-legacy-2");
  assert.deepEqual(sync, {});
  assert.deepEqual(local, { key: "sk-legacy-2" });
});

test("本机已有 key 时以本机为准, 同步过来的旧 key 只被删除", async () => {
  reset({ key: "sk-from-another-device" }, { key: "sk-this-device" });
  const s = await bg.getSettings();
  assert.equal(s.key, "sk-this-device");
  assert.deepEqual(sync, {});
  assert.deepEqual(local, { key: "sk-this-device" });
});

test("旧版本存的空 key 迁移后仍为空, 不回退为默认 key", async () => {
  reset({ key: "" });
  const s = await bg.getSettings();
  assert.equal(s.key, "");
  assert.deepEqual(sync, {});
  assert.deepEqual(local, { key: "" });
});

test("写入 local 失败时不删除同步副本, 错误上报, 下次重试成功", async () => {
  reset({ key: "sk-keep" });
  const realSet = chrome.storage.local.set;
  chrome.storage.local.set = async () => {
    throw new Error("QUOTA_BYTES quota exceeded");
  };
  try {
    await assert.rejects(bg.getSettings(), /quota exceeded/);
    assert.deepEqual(sync, { key: "sk-keep" });
    assert.deepEqual(local, {});
  } finally {
    chrome.storage.local.set = realSet;
  }
  assert.equal((await bg.getSettings()).key, "sk-keep");
  assert.deepEqual(sync, {});
  assert.deepEqual(local, { key: "sk-keep" });
});

// 安装或更新时补默认设置: 只写还没存的项, 已存的项一律不写回.
test("安装时补默认设置: 全新安装写入全部默认值; 已存的项保持原值, 不被写回", async () => {
  reset();
  const filled = await bg.fillMissingSyncDefaults();
  assert.equal(sync.base, "https://index-translate.bilibili.com/v1");
  assert.equal(sync.target, "中文");
  assert.deepEqual(Object.keys(filled).sort(), Object.keys(sync).sort());

  reset({ base: "http://127.0.0.1:9/v1", target: "English" });
  const writes = [];
  const realSet = chrome.storage.sync.set;
  chrome.storage.sync.set = async (items) => {
    writes.push(items);
    return realSet(items);
  };
  try {
    await bg.fillMissingSyncDefaults();
  } finally {
    chrome.storage.sync.set = realSet;
  }
  assert.equal(sync.base, "http://127.0.0.1:9/v1");
  assert.equal(sync.target, "English");
  assert.ok(writes.every((w) => !("base" in w) && !("target" in w)), JSON.stringify(writes));

  // 什么都不缺时一次也不写.
  writes.length = 0;
  chrome.storage.sync.set = async (items) => {
    writes.push(items);
    return realSet(items);
  };
  try {
    await bg.fillMissingSyncDefaults();
  } finally {
    chrome.storage.sync.set = realSet;
  }
  assert.deepEqual(writes, []);
});

test("安装时补默认设置: 读和写之间别处保存的值不会被旧值盖掉", async () => {
  reset({ base: "https://index-translate.bilibili.com/v1" });
  const realGet = chrome.storage.sync.get;
  // 模拟竞态: 刚读完, 选项页 (或测试) 就保存了新的地址.
  chrome.storage.sync.get = async (keys) => {
    const out = await realGet(keys);
    sync.base = "http://127.0.0.1:9/v1";
    return out;
  };
  try {
    await bg.fillMissingSyncDefaults();
  } finally {
    chrome.storage.sync.get = realGet;
  }
  assert.equal(sync.base, "http://127.0.0.1:9/v1");
});
