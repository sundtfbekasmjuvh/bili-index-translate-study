"use strict";

// 直连规则: 去掉 Origin 头并设置客户端名, 只作用于本扩展自己发往官方地址的请求.
// 规则在运行时登记 (要写入扩展自己的 ID), 浏览器会跨重启保留, 所以只在缺失或内容不同的时候写入.
const test = require("node:test");
const assert = require("node:assert/strict");
const { storageArea } = require("../support/fake-storage");

const EXTENSION_ID = "abcdefghijklmnopabcdefghijklmnop";
const stored = [];
const updates = [];

globalThis.chrome = {
  runtime: { id: EXTENSION_ID },
  storage: { sync: storageArea({}), local: storageArea({}) },
  declarativeNetRequest: {
    async getDynamicRules() {
      return stored.map((rule) => JSON.parse(JSON.stringify(rule)));
    },
    async updateDynamicRules({ removeRuleIds = [], addRules = [] }) {
      updates.push({ removeRuleIds, addRules });
      for (const id of removeRuleIds) {
        const i = stored.findIndex((rule) => rule.id === id);
        if (i >= 0) stored.splice(i, 1);
      }
      stored.push(...addRules);
    }
  }
};

const bg = require("../../bili-index-translate/background.js");

test("规则只匹配官方地址的 /v1/ 路径, 发起方限定为本扩展", () => {
  const rule = bg.directHeaderRule(EXTENSION_ID);
  assert.equal(rule.action.type, "modifyHeaders");
  assert.deepEqual(rule.action.requestHeaders, [
    { header: "origin", operation: "remove" },
    { header: "user-agent", operation: "set", value: "Index-Translate-Client/1.0" }
  ]);
  assert.deepEqual(rule.condition, {
    urlFilter: "||index-translate.bilibili.com/v1/",
    initiatorDomains: [EXTENSION_ID]
  });
});

test("没有规则时写入; 已有相同规则时不再写; 浏览器改写了键的顺序和大小写也算相同", async () => {
  stored.length = 0;
  updates.length = 0;
  assert.equal(await bg.installDirectRule(), true);
  assert.equal(updates.length, 1);
  assert.deepEqual(updates[0].removeRuleIds, [1]);
  assert.deepEqual(stored, [bg.directHeaderRule(EXTENSION_ID)]);

  assert.equal(await bg.installDirectRule(), false);
  assert.equal(updates.length, 1);

  const rule = stored[0];
  stored[0] = {
    condition: { initiatorDomains: rule.condition.initiatorDomains, urlFilter: rule.condition.urlFilter, isUrlFilterCaseSensitive: false },
    action: {
      requestHeaders: [
        { operation: "remove", header: "Origin" },
        { value: "Index-Translate-Client/1.0", operation: "set", header: "User-Agent" }
      ],
      type: "modifyHeaders"
    },
    priority: 1,
    id: 1
  };
  assert.equal(await bg.installDirectRule(), false);
  assert.equal(updates.length, 1);
});

test("旧版本留下的规则 (不限发起方, 或内容不同) 被替换成当前的一条", async () => {
  stored.length = 0;
  updates.length = 0;
  const old = bg.directHeaderRule(EXTENSION_ID);
  delete old.condition.initiatorDomains;
  stored.push(old, { id: 7, priority: 1, action: { type: "block" }, condition: { urlFilter: "||ads.example/" } });

  assert.equal(await bg.installDirectRule(), true);
  assert.deepEqual(
    stored.find((rule) => rule.id === 1),
    bg.directHeaderRule(EXTENSION_ID)
  );
  assert.equal(stored.length, 2, "其他规则不受影响");
});
