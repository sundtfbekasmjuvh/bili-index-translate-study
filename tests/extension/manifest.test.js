"use strict";

// manifest.json 引用的文件都存在; 图标是声明尺寸的 RGBA PNG, 四角透明, 中心是品牌色的笔画.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");
const zlib = require("zlib");

const EXT = path.resolve(__dirname, "../../bili-index-translate");
const manifest = JSON.parse(fs.readFileSync(path.join(EXT, "manifest.json"), "utf8"));

// 解码生成器写出的 PNG: 8 位 RGBA, 非隔行, 每行过滤类型为 0.
function decodePng(file) {
  const buf = fs.readFileSync(file);
  assert.equal(buf.subarray(0, 8).toString("hex"), "89504e470d0a1a0a", file + " PNG signature");
  let width = 0;
  let height = 0;
  const idat = [];
  for (let off = 8; off < buf.length; ) {
    const len = buf.readUInt32BE(off);
    const type = buf.toString("ascii", off + 4, off + 8);
    const data = buf.subarray(off + 8, off + 8 + len);
    if (type === "IHDR") {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      assert.deepEqual([data[8], data[9], data[12]], [8, 6, 0], file + " must be 8-bit RGBA, not interlaced");
    }
    if (type === "IDAT") idat.push(data);
    off += 12 + len;
  }
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const stride = 1 + width * 4;
  assert.equal(raw.length, stride * height, file + " pixel data size");
  return {
    width,
    height,
    pixel(x, y) {
      assert.equal(raw[y * stride], 0, file + " row filter");
      const i = y * stride + 1 + x * 4;
      return [raw[i], raw[i + 1], raw[i + 2], raw[i + 3]];
    }
  };
}

test("manifest 引用的脚本, 样式, 选项页和规则文件都存在", () => {
  const files = [
    manifest.background.service_worker,
    manifest.options_ui.page,
    ...manifest.content_scripts.flatMap((c) => [...c.js, ...c.css]),
    ...manifest.declarative_net_request.rule_resources.map((r) => r.path)
  ];
  for (const file of files) assert.ok(fs.existsSync(path.join(EXT, file)), file);
});

test("扩展图标: 16/32/48/128 四个尺寸齐全, 工具栏图标复用其中的 16 和 32", () => {
  assert.deepEqual(Object.keys(manifest.icons), ["16", "32", "48", "128"]);
  assert.deepEqual(manifest.action.default_icon, { 16: manifest.icons[16], 32: manifest.icons[32] });
});

for (const [size, file] of Object.entries(manifest.icons)) {
  test("图标 " + file + " 是 " + size + " 像素的 RGBA PNG, 四角透明, 竖笔是品牌色", () => {
    const n = Number(size);
    const png = decodePng(path.join(EXT, file));
    assert.deepEqual([png.width, png.height], [n, n]);
    for (const [x, y] of [[0, 0], [n - 1, 0], [0, n - 1], [n - 1, n - 1]]) {
      assert.equal(png.pixel(x, y)[3], 0, "corner " + x + "," + y + " alpha");
    }
    // 竖笔经过图标正中; 小尺寸下笔画只有一两个像素宽, 会和底色混合, 所以只要求偏向陶土色.
    const [r, g, b, a] = png.pixel(Math.floor(n / 2), Math.floor(n / 2));
    assert.equal(a, 255);
    assert.ok(r > g + 40 && g > b, "centre pixel " + [r, g, b] + " should lean towards #B2562F");
    // 笔画之外是浅色底.
    const [sr, sg, sb, sa] = png.pixel(Math.floor(n * 0.3), Math.floor(n * 0.6));
    assert.equal(sa, 255);
    assert.ok(sr > 230 && sg > 225 && sb > 215, "tile pixel " + [sr, sg, sb] + " should be the light surface");
  });
}

test("选项页引用的图标文件存在", () => {
  const html = fs.readFileSync(path.join(EXT, "options.html"), "utf8");
  const refs = [...html.matchAll(/(?:src|href)="(icons\/[^"]+)"/g)].map((m) => m[1]);
  assert.ok(refs.length >= 2, "options.html should show the icon");
  for (const ref of refs) assert.ok(fs.existsSync(path.join(EXT, ref)), ref);
});
