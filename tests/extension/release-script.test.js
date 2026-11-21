"use strict";

// package-release.ps1 开着 Set-StrictMode: 读 manifest 里不存在的键会抛异常, 打包会失败.
// 这台 Linux 上跑不了 PowerShell, 所以静态检查: 脚本里每条 $manifest.a.b.c 读取都必须能在 manifest.json 里解析到,
// 除非顶层键 a 被 PSObject.Properties["a"] 判断保护.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const ROOT = path.resolve(__dirname, "../..");
const script = fs.readFileSync(path.join(ROOT, ".github/scripts/package-release.ps1"), "utf8");
const manifest = JSON.parse(fs.readFileSync(path.join(ROOT, "bili-index-translate/manifest.json"), "utf8"));

function resolves(value, keys) {
  if (keys.length === 0) return true;
  if (Array.isArray(value)) return value.length > 0 && value.every((item) => resolves(item, keys));
  if (value === null || typeof value !== "object") return false;
  if (!Object.prototype.hasOwnProperty.call(value, keys[0])) return false;
  return resolves(value[keys[0]], keys.slice(1));
}

test("package-release.ps1 only reads manifest keys that exist (or guards them)", () => {
  const guarded = new Set([...script.matchAll(/\$manifest\.PSObject\.Properties\["([^"]+)"\]/g)].map((m) => m[1]));
  const reads = [...script.matchAll(/\$manifest((?:\.[A-Za-z_][\w]*)+)/g)]
    .map((m) => m[1].split(".").slice(1))
    .map((keys) => (keys.includes("PSObject") ? keys.slice(0, keys.indexOf("PSObject")) : keys))
    .filter((keys) => keys.length > 0);
  assert.ok(reads.length >= 5, "expected to find $manifest reads in the script");
  const bad = [];
  for (const keys of reads) {
    if (guarded.has(keys[0])) continue;
    if (!resolves(manifest, keys)) bad.push("$manifest." + keys.join("."));
  }
  assert.deepEqual(bad, [], "unguarded manifest reads that do not resolve in manifest.json");
});

test("package-release.ps1 no longer mentions the deleted dnr_rules.json", () => {
  assert.ok(!script.includes("dnr_rules.json"));
});
