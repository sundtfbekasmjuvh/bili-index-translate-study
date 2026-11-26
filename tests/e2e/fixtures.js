"use strict";

const path = require("path");
const fs = require("fs");
const os = require("os");
const base = require("@playwright/test");
const { startMockUpstream } = require("../support/mock-upstream");

// BIT_EXTENSION_DIR points the tests at another copy of the extension, for example an older checkout
// when comparing measurements (`npm run perf`).
const EXTENSION_DIR = path.resolve(process.env.BIT_EXTENSION_DIR || path.join(__dirname, "../../bili-index-translate"));
const OFFICIAL_HOST = "index-translate.bilibili.com";
const { expect } = base;

// One browser with the unpacked extension per worker; settings and caches are reset per test.
const test = base.test.extend({
  upstream: [
    async ({}, use) => {
      const upstream = await startMockUpstream();
      await use(upstream);
      await upstream.close();
    },
    { scope: "worker" }
  ],

  // Interface language the extension is set to. The browser's own language differs between machines
  // (zh-CN on a Chinese desktop, en-US on CI), so every test pins it; override with test.use({ uiLang }).
  uiLang: ["zh-CN", { option: true }],

  // With test.use({ mapOfficialHost: true }) the browser resolves the official API host to the local
  // mock, so the rule that only applies to that host can be exercised without leaving the machine.
  mapOfficialHost: [false, { option: true, scope: "worker" }],

  externalRequests: [
    async ({}, use) => {
      await use([]);
    },
    { scope: "worker" }
  ],

  extensionContext: [
    async ({ externalRequests, upstream, mapOfficialHost }, use) => {
      const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), "bit-e2e-"));
      const args = ["--disable-extensions-except=" + EXTENSION_DIR, "--load-extension=" + EXTENSION_DIR];
      const local = ["127.0.0.1"];
      if (mapOfficialHost) {
        args.push("--host-resolver-rules=MAP " + OFFICIAL_HOST + " " + new URL(upstream.origin).host);
        local.push(OFFICIAL_HOST);
      }
      const context = await base.chromium.launchPersistentContext(userDataDir, {
        channel: "chromium",
        headless: true,
        viewport: { width: 1280, height: 800 },
        args: args
      });
      context.on("request", (request) => {
        const url = new URL(request.url());
        if (/^https?:$/.test(url.protocol) && !local.includes(url.hostname)) externalRequests.push(request.url());
      });
      await use(context);
      await context.close();
      fs.rmSync(userDataDir, { recursive: true, force: true });
    },
    { scope: "worker" }
  ],

  serviceWorker: [
    async ({ extensionContext }, use) => {
      let [worker] = extensionContext.serviceWorkers();
      if (!worker) worker = await extensionContext.waitForEvent("serviceworker");
      // On a fresh profile the worker's onInstalled writes the default settings (the official address
      // among them). Wait for that write first: if the settings fixture wrote the local mock address
      // in between, the late default would replace it and a test would call the real API.
      await worker.evaluate(async () => {
        if (typeof DEFAULTS !== "object" || !DEFAULTS) return;
        for (let i = 0; i < 250; i++) {
          const stored = await chrome.storage.sync.get(null);
          if (Object.keys(DEFAULTS).every((key) => Object.prototype.hasOwnProperty.call(stored, key))) return;
          await new Promise((resolve) => setTimeout(resolve, 20));
        }
        throw new Error("the extension did not write its default settings within 5 s");
      });
      await use(worker);
    },
    { scope: "worker" }
  ],

  extensionId: [
    async ({ serviceWorker }, use) => {
      await use(new URL(serviceWorker.url()).host);
    },
    { scope: "worker" }
  ],

  context: async ({ extensionContext }, use) => {
    await use(extensionContext);
  },

  // Every request must stay on the local mock; nothing may reach the real API or a relay.
  stayLocal: [
    async ({ externalRequests }, use) => {
      externalRequests.length = 0;
      await use();
      expect(externalRequests, "requests that left 127.0.0.1").toEqual([]);
    },
    { auto: true }
  ],

  // Auto so that tests which only open extension pages also start from known settings and an empty request log.
  settings: [
    async ({ serviceWorker, upstream, uiLang }, use) => {
      const apply = async (overrides = {}) => {
        await serviceWorker.evaluate(
          async ({ sync, local }) => {
            // Write the new values before removing leftovers, so that no setting (the interface
            // language in particular) is briefly missing and seen at its default by an open page.
            for (const [area, values] of [[chrome.storage.sync, sync], [chrome.storage.local, local]]) {
              const stale = Object.keys(await area.get(null)).filter((key) => !(key in values));
              await area.set(values);
              if (stale.length) await area.remove(stale);
            }
            volatileTranslateCache.clear();
            volatileStudyCache.clear();
            volatileVisionCache.clear();
          },
          {
            sync: {
              base: upstream.base,
              proxy: "",
              target: "中文",
              model: "Index-Translate-9B",
              immersiveModel: "Index-Translate-2B",
              visionModel: "Index-Translate-35B-A3B",
              uiLang: uiLang,
              ...(overrides.sync || {})
            },
            // The API key lives in chrome.storage.local, not in the synced area.
            local: { glossary: "", key: "e2e-key", ...(overrides.local || {}) }
          }
        );
      };
      await apply();
      upstream.reset();
      await use(apply);
    },
    { auto: true }
  ],

  page: async ({ extensionContext, serviceWorker, settings, upstream }, use) => {
    const page = await extensionContext.newPage();
    await openWithExtension(page, serviceWorker, upstream.origin + "/article.html");
    await use(page);
    await page.close();
  }
});

// Opens a page and waits until the content script is running in it and has read the interface language.
async function openWithExtension(page, serviceWorker, url) {
  await page.goto(url);
  await expect
    .poll(() => contentScriptLoaded(serviceWorker, page.url()), { message: "content script injected", timeout: 10000 })
    .toBe(true);
  await page.bringToFront();
}

async function contentScriptLoaded(serviceWorker, url) {
  return serviceWorker.evaluate(async (target) => {
    const tab = (await chrome.tabs.query({})).find((t) => t.url === target);
    if (!tab) return false;
    try {
      const [res] = await chrome.scripting.executeScript({
        target: { tabId: tab.id },
        // The language marker appears once the content script has read the saved interface language.
        func: () => window.__biliIndexTranslateLoaded === true && typeof window.__biliIndexTranslateLang === "string"
      });
      return Boolean(res && res.result);
    } catch (_) {
      return false;
    }
  }, url);
}

// Drags across an element's text with the left button, which is how a user selects it. Any previous
// selection is cleared first, as a click elsewhere would; pressing inside a selection starts a drag.
// The drag follows the first line of the element's own text, not the middle of its box: Alt+A
// appends the translation below the text, and the middle of the taller box can then lie on the
// translation, which is not selectable. Where that middle falls depends on the machine's fonts.
async function dragSelect(page, selector) {
  await page.evaluate(() => getSelection().removeAllRanges());
  const line = await page.locator(selector).evaluate((el) => {
    const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, {
      acceptNode: (node) =>
        node.parentElement.closest("[data-bit-imm]")
          ? NodeFilter.FILTER_REJECT
          : node.nodeValue.trim()
          ? NodeFilter.FILTER_ACCEPT
          : NodeFilter.FILTER_SKIP
    });
    const text = walker.nextNode();
    if (!text) return null;
    const range = document.createRange();
    range.selectNodeContents(text);
    const first = range.getClientRects()[0];
    const box = el.getBoundingClientRect();
    return first ? { left: first.left, right: first.right, top: first.top, height: first.height, boxRight: box.right } : null;
  });
  if (!line) throw new Error("element has no visible text: " + selector);
  const y = line.top + line.height / 2;
  await page.mouse.move(line.left + 1, y);
  await page.mouse.down();
  // End just past the last character so that it is included, without leaving the element.
  await page.mouse.move(Math.min(line.boxRight - 1, line.right + 4), y, { steps: 8 });
  await page.mouse.up();
}

// Native context menus cannot be clicked from Playwright, so the click event is dispatched in the
// service worker with the same info object Chrome would pass.
async function clickContextMenu(serviceWorker, page, info) {
  await serviceWorker.evaluate(
    async ({ url, info }) => {
      const tab = (await chrome.tabs.query({})).find((t) => t.url === url);
      chrome.contextMenus.onClicked.dispatch({ frameId: 0, ...info }, tab);
    },
    { url: page.url(), info }
  );
}

async function runCommand(serviceWorker, command) {
  await serviceWorker.evaluate((name) => chrome.commands.onCommand.dispatch(name), command);
}

function requestsOf(upstream, kind) {
  return upstream.requests.filter((r) => r.kind === kind);
}

// The words stored as known (chrome.storage.local annotateKnown), as the background keeps them.
async function readAnnotateKnown(serviceWorker) {
  return serviceWorker.evaluate(async () => (await chrome.storage.local.get("annotateKnown")).annotateKnown);
}

// The annotate cache lives in the service worker and is not emptied by the settings fixture when no
// stored setting changes, so tests that count requests empty it themselves.
async function clearAnnotateCache(serviceWorker) {
  await serviceWorker.evaluate(() => volatileAnnotateCache.clear());
}

// Writes annotateKnown (and, when given, the switch and levels) the way a background write would, for
// tests of the options page. `words` is { zh: [], en: [], es: [] }; the list order is the order of marking.
async function seedAnnotateKnown(serviceWorker, words, extra = {}) {
  await serviceWorker.evaluate(
    async ({ words, extra }) => {
      await chrome.storage.local.set({ ...extra, annotateKnown: { v: 1, zh: [], en: [], es: [], ...words } });
    },
    { words, extra }
  );
}

// The annotate settings of chrome.storage.local (keys that were never written are absent).
async function readAnnotateSettings(serviceWorker) {
  return serviceWorker.evaluate(() => chrome.storage.local.get(["annotateEnabled", "annotateLevels"]));
}

module.exports = {
  test,
  expect,
  dragSelect,
  openWithExtension,
  clickContextMenu,
  runCommand,
  requestsOf,
  readAnnotateKnown,
  clearAnnotateCache,
  seedAnnotateKnown,
  readAnnotateSettings,
  EXTENSION_DIR,
  OFFICIAL_HOST
};