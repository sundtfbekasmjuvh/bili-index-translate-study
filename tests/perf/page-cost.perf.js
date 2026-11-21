"use strict";

// 页面端开销测量: 流式译文和 Alt+A 双语对照期间, 网页主线程花了多少时间, 重排了多少次.
// 上游是一个本地服务, 每 1 ms 发一个 2 字符的增量 (比真实模型密, 用来放大每条增量的成本).
// 数字取自 Chrome 的 Performance.getMetrics: TaskDuration 是主线程忙碌的总时间, LayoutCount 是重排次数.
// 用法: npm run perf. 结果随机器变化, 只适合在同一台机器上比较改动前后.
const http = require("http");
const { test, expect, openWithExtension } = require("../e2e/fixtures");

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const SENTENCE = "The scheduler retries failed jobs after a short exponential backoff and records every attempt. ";
const LONG_TEXT = SENTENCE.repeat(30).trim();

function pageHtml(paragraphs) {
  let body = "";
  for (let i = 0; i < paragraphs; i++) {
    body += '<p id="p' + i + '">Paragraph ' + i + ": " + SENTENCE + "It is kept in the audit log for later review.</p>\n";
  }
  return (
    '<!doctype html><meta charset="utf-8"><title>page cost</title>' +
    "<style>body{font:16px/1.5 sans-serif;margin:24px;max-width:760px}</style>" +
    '<p id="long">' + LONG_TEXT + "</p>\n" + body
  );
}

// hold: resolve it to let the replies start, so that scanning and streaming can be measured apart.
async function startUpstream({ hold = null, silent = false } = {}) {
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, "http://localhost");
    if (req.method === "GET") {
      res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
      res.end(pageHtml(Number(url.searchParams.get("n") || 40)));
      return;
    }
    let raw = "";
    for await (const chunk of req) raw += chunk;
    const content = String(JSON.parse(raw).messages.pop().content);
    const source = /【源文】\n([\s\S]*?)\n\n【约束要求】/.exec(content);
    const marks = source ? [...new Set(source[1].match(/〖P\d+〗/g) || [])] : [];
    const reply = marks.length
      ? marks.map((mark) => mark + "译文内容".repeat(30)).join("\n")
      : "译文内容".repeat(content.length > 1500 ? 750 : 30);

    res.writeHead(200, { "Content-Type": "text/event-stream; charset=utf-8", "Cache-Control": "no-cache" });
    if (silent) return; // never answers: only the page's own work is measured
    if (hold) await hold;
    for (let i = 0; i < reply.length; i += 2) {
      if (res.destroyed) return;
      res.write("data: " + JSON.stringify({ choices: [{ delta: { content: reply.slice(i, i + 2) } }] }) + "\n\n");
      await sleep(1);
    }
    res.write("data: [DONE]\n\n");
    res.end();
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return {
    origin: "http://127.0.0.1:" + server.address().port,
    close() {
      server.closeAllConnections();
      return new Promise((resolve) => server.close(resolve));
    }
  };
}

async function open(context, serviceWorker, settings, upstream, paragraphs) {
  await settings({ sync: { base: upstream.origin + "/v1" } });
  const page = await context.newPage();
  await openWithExtension(page, serviceWorker, upstream.origin + "/page.html?n=" + paragraphs);
  const cdp = await context.newCDPSession(page);
  await cdp.send("Performance.enable");
  const read = async () => {
    const { metrics } = await cdp.send("Performance.getMetrics");
    return Object.fromEntries(metrics.map((m) => [m.name, m.value]));
  };
  return { page, read };
}

function cost(before, after) {
  const ms = (name) => Math.round((after[name] - before[name]) * 1000);
  return {
    "main thread ms": ms("TaskDuration"),
    "script ms": ms("ScriptDuration"),
    "layout ms": ms("LayoutDuration"),
    layouts: after.LayoutCount - before.LayoutCount
  };
}

function report(label, numbers) {
  console.log("PERF " + label.padEnd(44) + JSON.stringify(numbers));
}

test("划词: 3000 字符的译文流式显示", async ({ context, serviceWorker, settings }) => {
  const upstream = await startUpstream();
  const { page, read } = await open(context, serviceWorker, settings, upstream, 40);
  const before = await read();
  const started = Date.now();
  await page.evaluate(() => {
    const el = document.querySelector("#long");
    const range = document.createRange();
    range.selectNodeContents(el);
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    const box = el.getBoundingClientRect();
    document.dispatchEvent(new MouseEvent("mouseup", { button: 0, clientX: box.left + 5, clientY: box.top + 5, bubbles: true }));
  });
  await expect(page.locator("#bili-index-translate-panel .bit-foot")).toContainText("direct", { timeout: 60000 });
  report("selection, 1500 deltas", { "wall ms": Date.now() - started, ...cost(before, await read()) });
  await page.close();
  await upstream.close();
});

for (const paragraphs of [40, 6000]) {
  test("双语对照: " + paragraphs + " 段的页面, 扫描和流式分开计", async ({ context, serviceWorker, settings }) => {
    let release;
    const upstream = await startUpstream({ hold: new Promise((resolve) => (release = resolve)) });
    const { page, read } = await open(context, serviceWorker, settings, upstream, paragraphs);
    const status = page.locator("#bili-index-imm-bar .bit-imm-status");

    const before = await read();
    await page.keyboard.press("Alt+KeyA");
    await page.waitForTimeout(1500);
    const scanned = await read();
    report("Alt+A scan, " + paragraphs + " paragraphs", cost(before, scanned));

    release();
    await expect(status).toContainText("双语就绪", { timeout: 60000 });
    const streamed = await read();
    report("Alt+A streaming, " + paragraphs + " paragraphs", cost(scanned, streamed));

    await page.waitForTimeout(1500);
    report("Alt+A idle 1.5 s, " + paragraphs + " paragraphs", cost(streamed, await read()));
    await page.close();
    await upstream.close();
  });
}

test("双语对照: 6000 段的页面上滚动 90 帧", async ({ context, serviceWorker, settings }) => {
  const upstream = await startUpstream({ silent: true });
  const { page, read } = await open(context, serviceWorker, settings, upstream, 6000);
  const scroll = () =>
    page.evaluate(
      () =>
        new Promise((done) => {
          let frames = 0;
          const step = () => {
            window.scrollBy(0, 40);
            if (++frames < 90) requestAnimationFrame(step);
            else done();
          };
          requestAnimationFrame(step);
        })
    );

  let before = await read();
  await scroll();
  report("scroll 90 frames, Alt+A off", cost(before, await read()));

  await page.evaluate(() => window.scrollTo(0, 0));
  await page.keyboard.press("Alt+KeyA");
  await page.waitForTimeout(1200);
  before = await read();
  await scroll();
  report("scroll 90 frames, Alt+A on", cost(before, await read()));
  await page.close();
  await upstream.close();
});
