"use strict";

// 右键图片的下载: 8 MB 上限在下载过程中生效 (先看 Content-Length, 再边读边计数), 超时, 空图片和 MIME 类型.
const test = require("node:test");
const assert = require("node:assert/strict");

const MB = 1024 * 1024;
const TOO_LARGE = /^Error: Image exceeds 8 MB limit; use Alt\+S region snip instead\.$/;
const calls = [];
let respond;

globalThis.fetch = async (url, init) => {
  const call = { url: String(url), credentials: init.credentials, signal: init.signal };
  calls.push(call);
  return respond(call);
};

const bg = require("../../bili-index-translate/background.js");

// 按块供给的响应体, 记录已供给的字节数和是否被读取方取消.
function chunkedResponse(chunkBytes, chunkCount, headers, stats = {}) {
  stats.served = 0;
  stats.cancelled = false;
  let sent = 0;
  const body = new ReadableStream(
    {
      pull(controller) {
        if (sent >= chunkCount) return controller.close();
        sent++;
        stats.served += chunkBytes;
        controller.enqueue(new Uint8Array(chunkBytes).fill(sent));
      },
      cancel() {
        stats.cancelled = true;
      }
    },
    { highWaterMark: 0 }
  );
  return new Response(body, { status: 200, headers });
}

test("data: 图片地址原样返回, 不发请求; 空地址报错", async () => {
  calls.length = 0;
  const dataUrl = "data:image/png;base64,iVBORw0KGgo=";
  assert.equal(await bg.fetchImageAsDataUrl(" " + dataUrl + " "), dataUrl);
  await assert.rejects(bg.fetchImageAsDataUrl("  "), /^Error: Empty image URL\.$/);
  assert.equal(calls.length, 0);
});

test("正常图片转成 data URL: 字节完整, 不带凭据, MIME 取自 Content-Type 并去掉参数", async () => {
  calls.length = 0;
  const stats = {};
  respond = () => chunkedResponse(3, 2, { "content-type": "Image/JPEG; charset=binary" }, stats);
  const out = await bg.fetchImageAsDataUrl("https://img.example.test/a.jpg");
  assert.equal(out, "data:image/jpeg;base64," + Buffer.from([1, 1, 1, 2, 2, 2]).toString("base64"));
  assert.deepEqual(calls.map((c) => [c.url, c.credentials]), [["https://img.example.test/a.jpg", "omit"]]);
  assert.equal(stats.cancelled, false);
});

test("Content-Type 缺失或不是图片类型时按 image/png 处理", async () => {
  respond = () => new Response(new Uint8Array([7, 8]), { status: 200, headers: { "content-type": "application/octet-stream" } });
  assert.equal(await bg.fetchImageAsDataUrl("https://img.example.test/b"), "data:image/png;base64,Bwg=");
  respond = () => {
    const res = new Response(new Uint8Array([7, 8]), { status: 200 });
    res.headers.delete("content-type");
    return res;
  };
  assert.equal(await bg.fetchImageAsDataUrl("https://img.example.test/c"), "data:image/png;base64,Bwg=");
});

test("刚好 8 MB 的图片可以通过", async () => {
  const stats = {};
  respond = () => chunkedResponse(MB, 8, { "content-type": "image/png", "content-length": String(8 * MB) }, stats);
  const out = await bg.fetchImageAsDataUrl("https://img.example.test/exact.png");
  assert.equal(Buffer.from(out.slice("data:image/png;base64,".length), "base64").length, 8 * MB);
  assert.equal(stats.cancelled, false);
});

test("Content-Length 超过 8 MB 时不读取响应体, 直接中止请求", async () => {
  calls.length = 0;
  const stats = {};
  respond = () => chunkedResponse(MB, 20, { "content-type": "image/png", "content-length": String(8 * MB + 1) }, stats);
  await assert.rejects(bg.fetchImageAsDataUrl("https://img.example.test/huge.png"), TOO_LARGE);
  assert.equal(stats.served, 0);
  assert.equal(calls[0].signal.aborted, true);
});

test("没有 Content-Length 时边读边计数, 超过 8 MB 即取消, 不把整个响应读完", async () => {
  calls.length = 0;
  const stats = {};
  respond = () => chunkedResponse(MB, 20, { "content-type": "image/png" }, stats);
  await assert.rejects(bg.fetchImageAsDataUrl("https://img.example.test/stream.png"), TOO_LARGE);
  assert.equal(stats.served, 9 * MB);
  assert.equal(stats.cancelled, true);
  assert.equal(calls[0].signal.aborted, true);
});

test("Content-Length 谎报偏小时仍以实际读到的字节数为准", async () => {
  const stats = {};
  respond = () => chunkedResponse(MB, 20, { "content-type": "image/png", "content-length": "1024" }, stats);
  await assert.rejects(bg.fetchImageAsDataUrl("https://img.example.test/liar.png"), TOO_LARGE);
  assert.equal(stats.served, 9 * MB);
  assert.equal(stats.cancelled, true);
});

test("空响应体和 HTTP 错误分别报错", async () => {
  respond = () => new Response(new Uint8Array(0), { status: 200, headers: { "content-type": "image/png" } });
  await assert.rejects(bg.fetchImageAsDataUrl("https://img.example.test/empty.png"), /^Error: Image is empty\.$/);
  respond = () => new Response("nope", { status: 404 });
  await assert.rejects(bg.fetchImageAsDataUrl("https://img.example.test/missing.png"), /^Error: Failed to fetch image \(HTTP 404\)\.$/);
});

test("连接一直没有响应时按超时报错并中止请求", async () => {
  calls.length = 0;
  respond = (call) =>
    new Promise((_, reject) => {
      call.signal.addEventListener("abort", () => reject(new DOMException("The operation was aborted.", "AbortError")));
    });
  await assert.rejects(bg.fetchImageAsDataUrl("https://img.example.test/slow.png", 50), /^Error: Image download timed out after 0\.05 s\.$/);
  assert.equal(calls[0].signal.aborted, true);
});

test("响应头到达后响应体停住, 同样按超时报错", async () => {
  respond = (call) => {
    const body = new ReadableStream({
      start(controller) {
        controller.enqueue(new Uint8Array([1]));
        call.signal.addEventListener("abort", () => controller.error(new DOMException("The operation was aborted.", "AbortError")));
      }
    });
    return new Response(body, { status: 200, headers: { "content-type": "image/png" } });
  };
  await assert.rejects(bg.fetchImageAsDataUrl("https://img.example.test/stall.png", 50), /^Error: Image download timed out after 0\.05 s\.$/);
});

test("网络错误原样抛出, 不被改写成超时", async () => {
  respond = () => {
    throw new TypeError("Failed to fetch");
  };
  await assert.rejects(bg.fetchImageAsDataUrl("https://img.example.test/offline.png"), /^TypeError: Failed to fetch$/);
});
