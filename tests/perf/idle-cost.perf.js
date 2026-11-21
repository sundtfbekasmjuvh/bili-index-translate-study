"use strict";

// 空闲开销测量: 用户什么都没做的网页上, 扩展(i18n.js + content.js + content.css 注入 <all_urls>, all_frames)带来的固定开销.
// 场景: 普通页(article.html) 和 多 iframe 页(30 个同源 iframe, 从本地服务器加载).
// 条件: base(不加载扩展) / current(当前扩展) / a(all_frames=false) / b(去掉 content.css) / c(content.js 换空文件).
// 每次运行都是全新的浏览器, 页面 load 后再等 1500 ms, 先 collectGarbage 再读 CDP Performance.getMetrics.
// 各条件轮流交错运行, 抵消机器漂移. 结果行以 "PERF " 开头.
// 用法: npm run perf -- idle-cost    (或 npx playwright test -c tests/perf idle-cost)
// 环境变量: BIT_IDLE_RUNS(默认 7), BIT_PERF_VARIANTS(变体副本目录, 默认在系统临时目录下).
const fs = require("fs");
const os = require("os");
const path = require("path");
const http = require("http");
const base = require("@playwright/test");
const { test, expect } = base;

const ROOT = path.resolve(__dirname, "../..");
const EXT = path.join(ROOT, "bili-index-translate");
const FIXTURES = path.join(ROOT, "tests/support/fixtures");
const VARIANTS = process.env.BIT_PERF_VARIANTS || path.join(os.tmpdir(), "bit-perf-variants");
const RUNS = Number(process.env.BIT_IDLE_RUNS || 7);
const FRAMES = 30;
const SETTLE_MS = 1500;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

// 变体副本: 每次都从当前的扩展目录重新复制, 只改副本里的 manifest / content.js.
function makeVariants() {
  fs.rmSync(VARIANTS, { recursive: true, force: true });
  const make = (name, edit) => {
    const dir = path.join(VARIANTS, name);
    fs.cpSync(EXT, dir, { recursive: true });
    const file = path.join(dir, "manifest.json");
    const manifest = JSON.parse(fs.readFileSync(file, "utf8"));
    edit(manifest, dir);
    fs.writeFileSync(file, JSON.stringify(manifest, null, 2));
    return dir;
  };
  return {
    a: make("a-all-frames-false", (m) => (m.content_scripts[0].all_frames = false)),
    b: make("b-no-css", (m) => delete m.content_scripts[0].css),
    c: make("c-empty-content-js", (m, dir) => fs.writeFileSync(path.join(dir, "content.js"), ""))
  };
}

async function startServer() {
  const article = fs.readFileSync(path.join(FIXTURES, "article.html"));
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, "http://localhost");
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    if (url.pathname === "/article.html") return res.end(article);
    if (url.pathname === "/frame.html") {
      return res.end('<!doctype html><meta charset="utf-8"><title>frame</title><body style="font:14px sans-serif"><p>Frame ' + url.searchParams.get("i") + ": a short piece of text in a simple page.</p>");
    }
    if (url.pathname === "/frames.html") {
      let body = "";
      for (let i = 0; i < FRAMES; i++) body += '<iframe src="/frame.html?i=' + i + '" width="300" height="60"></iframe>\n';
      return res.end('<!doctype html><meta charset="utf-8"><title>frames</title><h1>30 frames</h1>\n' + body);
    }
    res.statusCode = 404;
    res.end();
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return { origin: "http://127.0.0.1:" + server.address().port, close: () => { server.closeAllConnections(); return new Promise((r) => server.close(r)); } };
}

async function launch(extDir) {
  const args = extDir ? ["--disable-extensions-except=" + extDir, "--load-extension=" + extDir] : [];
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), "bit-idle-"));
  const context = await base.chromium.launchPersistentContext(userDataDir, { channel: "chromium", headless: true, viewport: { width: 1280, height: 800 }, args });
  if (extDir && !context.serviceWorkers().length) await context.waitForEvent("serviceworker");
  await sleep(1000);
  return { context, close: async () => { await context.close(); fs.rmSync(userDataDir, { recursive: true, force: true }); } };
}

async function metricsOf(cdp) {
  const { metrics } = await cdp.send("Performance.getMetrics");
  return Object.fromEntries(metrics.map((m) => [m.name, m.value]));
}

// 返回扩展在每个 frame 里是否已运行(只在读完指标之后查, 不影响测量).
async function injectedFrames(context, page) {
  const worker = context.serviceWorkers()[0];
  if (!worker) return null;
  return worker.evaluate(async (url) => {
    const tab = (await chrome.tabs.query({})).find((t) => t.url === url);
    const results = await chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, func: () => window.__biliIndexTranslateLoaded === true });
    return { frames: results.length, loaded: results.filter((r) => r.result).length };
  }, page.url());
}

async function measure(extDir, url, { profile = false } = {}) {
  const browser = await launch(extDir);
  try {
    const page = await browser.context.newPage();
    const cdp = await browser.context.newCDPSession(page);
    await cdp.send("Performance.enable");
    if (profile) {
      await cdp.send("Profiler.enable");
      await cdp.send("Profiler.setSamplingInterval", { interval: 100 });
      await cdp.send("Profiler.start");
    }
    await page.goto(url, { waitUntil: "load" });
    await page.waitForTimeout(SETTLE_MS);
    let prof = null;
    if (profile) prof = (await cdp.send("Profiler.stop")).profile;
    await cdp.send("HeapProfiler.collectGarbage");
    const m = await metricsOf(cdp);
    const injected = extDir ? await injectedFrames(browser.context, page) : null;
    return {
      TaskDuration: m.TaskDuration * 1000,
      ScriptDuration: m.ScriptDuration * 1000,
      RecalcStyleDuration: m.RecalcStyleDuration * 1000,
      LayoutDuration: m.LayoutDuration * 1000,
      heapKB: m.JSHeapUsedSize / 1024,
      listeners: m.JSEventListeners,
      nodes: m.Nodes,
      injected,
      prof
    };
  } finally {
    await browser.close();
  }
}

const KEYS = ["TaskDuration", "ScriptDuration", "RecalcStyleDuration", "LayoutDuration", "heapKB", "listeners", "nodes"];
const median = (a) => { const s = [...a].sort((x, y) => x - y); const n = s.length; return n % 2 ? s[(n - 1) / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; };
const fmt = (v) => (Math.round(v * 10) / 10).toString();
const stat = (rows, k) => { const v = rows.map((r) => r[k]); return { med: median(v), min: Math.min(...v), max: Math.max(...v) }; };

function profileSummary(profile) {
  const byId = new Map(profile.nodes.map((n) => [n.id, n]));
  const self = new Map();
  profile.samples.forEach((id, i) => self.set(id, (self.get(id) || 0) + (profile.timeDeltas[i] || 0)));
  const files = {}, fns = {};
  let total = 0;
  for (const [id, us] of self) {
    const cf = byId.get(id).callFrame;
    const file = /(content|i18n)\.js$/.test(cf.url) ? cf.url.split("/").pop() : cf.url ? "other" : "(native/idle/program/gc)";
    const name = cf.functionName || "(anonymous)";
    if (name === "(idle)") continue;
    files[file] = (files[file] || 0) + us;
    const key = name + " @ " + (cf.url.split("/").pop() || "-") + ":" + cf.lineNumber;
    fns[key] = (fns[key] || 0) + us;
    total += us;
  }
  return { files, fns, total };
}

test("空闲开销: 普通页和 30 个 iframe 的页面", async () => {
  test.setTimeout(RUNS * 10 * 15000 + 300000);
  const variants = makeVariants();
  const server = await startServer();
  const conditions = [["base", null], ["current", EXT], ["a all_frames=false", variants.a], ["b no content.css", variants.b], ["c empty content.js", variants.c]];
  const scenarios = [["plain", server.origin + "/article.html"], ["frames30", server.origin + "/frames.html"]];
  const rows = {};
  try {
    for (let run = 0; run < RUNS; run++) {
      for (const [sname, url] of scenarios) {
        for (const [cname, dir] of conditions) {
          const r = await measure(dir, url);
          (rows[sname + "|" + cname] ||= []).push(r);
          if (run === 0 && r.injected) console.log("PERF injected " + sname + " " + cname + " " + JSON.stringify(r.injected));
        }
      }
    }
    for (const [sname] of scenarios) {
      const baseRows = rows[sname + "|base"];
      for (const [cname] of conditions) {
        const rs = rows[sname + "|" + cname];
        const out = {};
        for (const k of KEYS) {
          const s = stat(rs, k);
          out[k] = fmt(s.med) + " (" + fmt(s.min) + "-" + fmt(s.max) + ")";
          if (cname !== "base") out[k] += " d" + fmt(s.med - stat(baseRows, k).med);
        }
        console.log("PERF " + sname.padEnd(9) + cname.padEnd(20) + "n=" + rs.length + " " + JSON.stringify(out));
      }
    }

    // 定位: 当前扩展 + 多 iframe 页, Profiler 采样一次加载过程.
    const r = await measure(EXT, server.origin + "/frames.html", { profile: true });
    const sum = profileSummary(r.prof);
    const ms = (us) => fmt(us / 1000);
    console.log("PERF profile total sampled self ms (no idle) " + ms(sum.total));
    for (const [f, us] of Object.entries(sum.files).sort((a, b) => b[1] - a[1])) console.log("PERF profile file " + f.padEnd(26) + ms(us) + " ms");
    for (const [f, us] of Object.entries(sum.fns).sort((a, b) => b[1] - a[1]).filter(([f]) => !/^\((program|garbage collector|root)\)/.test(f)).slice(0, 5)) console.log("PERF profile top fn " + f.padEnd(50) + ms(us) + " ms");
    expect(sum.total).toBeGreaterThan(0);
  } finally {
    await server.close();
  }
});
