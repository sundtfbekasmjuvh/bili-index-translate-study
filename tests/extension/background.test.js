"use strict";

// 扩展 background.js 导出的纯函数: 术语表, 提示词构造, 识图双轨解析, 段落锚点, 学习卡 JSON 修复与流式解析.
const test = require("node:test");
const assert = require("node:assert/strict");
const bg = require("../../bili-index-translate/background.js");

const IMAGE = "data:image/png;base64,iVBORw0KGgo=";

test("parseGlossary 支持所有分隔符, 单词锁定, 注释和大小写去重, 并按长度降序", () => {
  const entries = bg.parseGlossary(
    [
      "# comment",
      "// note",
      "",
      "Sovereign Scheduler => 主权调度器",
      "Context Engineering -> 上下文工程",
      "Mixture → 混合",
      "Tab\tTabbed",
      "X = Y",
      "flash-attention",
      "Flash-Attention => 不应覆盖"
    ].join("\n")
  );
  assert.deepEqual(
    entries.map((e) => [e.source, e.target]),
    [
      ["Sovereign Scheduler", "主权调度器"],
      ["Context Engineering", "上下文工程"],
      ["flash-attention", "flash-attention"],
      ["Mixture", "混合"],
      ["Tab", "Tabbed"],
      ["X", "Y"]
    ]
  );
});

test("parseGlossary 处理 CRLF 和空输入", () => {
  assert.deepEqual(bg.parseGlossary(""), []);
  assert.deepEqual(bg.parseGlossary(null), []);
  assert.deepEqual(
    bg.parseGlossary("A => B\r\nC").map((e) => e.source),
    ["A", "C"]
  );
});

test("matchGlossaryHits 只返回原文中出现的词条, 不区分大小写", () => {
  const glossary = "Flash-Attention\nPyTorch => 火炬\nUnused => 未用";
  assert.deepEqual(bg.matchGlossaryHits(glossary, "uses FLASH-attention in pytorch"), [
    { source: "Flash-Attention", target: "Flash-Attention" },
    { source: "PyTorch", target: "火炬" }
  ]);
  assert.deepEqual(bg.matchGlossaryHits(glossary, ""), []);
  assert.deepEqual(bg.matchGlossaryHits("", "PyTorch"), []);
});

test("matchGlossaryHits 接受已解析条目并遵守 maxHits 上限", () => {
  const entries = bg.parseGlossary("aa\nbb\ncc");
  assert.equal(bg.matchGlossaryHits(entries, "aa bb cc", 2).length, 2);
});

test("hasInlineBacktickCode 只识别非空的单行反引号片段", () => {
  assert.equal(bg.hasInlineBacktickCode("run `npm i` now"), true);
  assert.equal(bg.hasInlineBacktickCode("no code"), false);
  assert.equal(bg.hasInlineBacktickCode("``"), false);
  assert.equal(bg.hasInlineBacktickCode("`line\nbreak`"), false);
});

test("stripThink 去掉思维链, 未闭合的思维链视为尚无正文", () => {
  assert.equal(bg.stripThink("<think>hidden</think> 正文 "), "正文");
  assert.equal(bg.stripThink("<think>still thinking"), "");
  assert.equal(bg.stripThink("plain"), "plain");
  assert.equal(bg.stripThink(undefined), "");
});

test("isIndexModelOrHost 按模型名或 B 站主机识别, attachIndexTemplateKwargs 只对其关闭 thinking", () => {
  assert.equal(bg.isIndexModelOrHost("Index-Translate-9B", "https://api.openai.com/v1"), true);
  assert.equal(bg.isIndexModelOrHost("gpt-4o", "https://index-translate.bilibili.com/v1"), true);
  assert.equal(bg.isIndexModelOrHost("gpt-4o", "https://api.openai.com/v1"), false);
  assert.deepEqual(bg.attachIndexTemplateKwargs({}, "Index-Translate-9B", ""), {
    chat_template_kwargs: { enable_thinking: false }
  });
  assert.deepEqual(bg.attachIndexTemplateKwargs({}, "gpt-4o", "https://api.openai.com/v1"), {});
});

test("resolveStudyModel 把 2B 升级为 9B, 显式覆盖优先", () => {
  assert.equal(bg.resolveStudyModel("Index-Translate-2B"), "Index-Translate-9B");
  assert.equal(bg.resolveStudyModel("Index-Translate-2B", "Index-Translate-35B-A3B"), "Index-Translate-35B-A3B");
  assert.equal(bg.resolveStudyModel(""), "Index-Translate-9B");
  assert.equal(bg.resolveStudyModel("gpt-4o"), "gpt-4o");
});

test("resolveVisionModel 把纯文本模型和空值升级为 35B-A3B", () => {
  assert.equal(bg.resolveVisionModel("Index-Nailong-9B"), "Index-Translate-35B-A3B");
  assert.equal(bg.resolveVisionModel(""), "Index-Translate-35B-A3B");
  assert.equal(bg.resolveVisionModel("Index-Translate-9B"), "Index-Translate-9B");
  assert.equal(bg.resolveVisionModel("Index-Translate-9B", "Index-Translate-2B"), "Index-Translate-2B");
});

test("inferSourceLang 按文字脚本推断语言", () => {
  assert.deepEqual(
    ["こんにちは", "안녕하세요", "Привет", "你好", "hello"].map(bg.inferSourceLang),
    ["ja-JP", "ko-KR", "ru-RU", "zh-CN", "en-US"]
  );
});

test("buildTranslateMessages: Index 模型无约束时只发一条精简 user 消息", () => {
  const messages = bg.buildTranslateMessages("  Hello  ", "中文", "Index-Translate-9B", "");
  assert.equal(messages.length, 1);
  assert.equal(messages[0].role, "user");
  assert.ok(messages[0].content.startsWith("请将以下文本翻译为中文"));
  assert.ok(messages[0].content.endsWith("\n\nHello"));
});

test("buildTranslateMessages: Index 模型按命中术语和反引号代码编号注入约束", () => {
  const [msg] = bg.buildTranslateMessages("use `x` here", "简体中文", "Index-Translate-9B", "", [
    { source: "A", target: "B" }
  ]);
  assert.match(msg.content, /【源文】\nuse `x` here/);
  assert.match(msg.content, /1\. 【硬性要求】专名\/术语对照: A→B/);
  assert.match(msg.content, /2\. 【硬性要求】反引号/);
});

test("buildTranslateMessages: 其他 OpenAI 兼容模型使用 system + user", () => {
  const messages = bg.buildTranslateMessages("Hi", "English", "gpt-4o", "https://api.openai.com/v1", [
    { source: "A", target: "B" }
  ]);
  assert.deepEqual(
    messages.map((m) => m.role),
    ["system", "user"]
  );
  assert.match(messages[0].content, /into English/);
  assert.match(messages[0].content, /A -> B/);
  assert.equal(messages[1].content, "Hi");
});

test("buildImmersiveBatchMessages 为每段加 〖Pk〗 锚点", () => {
  const [msg] = bg.buildImmersiveBatchMessages(["first", "second"], "中文", "Index-Translate-2B", "");
  assert.match(msg.content, /〖P0〗first\n〖P1〗second/);
  assert.match(msg.content, /〖P0〗、〖P1〗/);
});

test("buildVisionTranslateMessages 发送 text + image_url, 并注入术语表", () => {
  const [msg] = bg.buildVisionTranslateMessages(IMAGE, "中文", "Index-Translate-35B-A3B", "", "Scheduler => 调度器");
  assert.equal(msg.role, "user");
  assert.deepEqual(
    msg.content.map((part) => part.type),
    ["text", "image_url"]
  );
  assert.equal(msg.content[1].image_url.url, IMAGE);
  assert.match(msg.content[0].text, /〖译文〗/);
  assert.match(msg.content[0].text, /〖原文〗/);
  assert.match(msg.content[0].text, /【术语对照】Scheduler→调度器/);
});

test("parseVisionDualOutput 解析完整双轨输出并去掉思维链", () => {
  assert.deepEqual(bg.parseVisionDualOutput("<think>x</think>〖译文〗你好。\n〖原文〗Hello."), {
    translation: "你好。",
    sourceText: "Hello."
  });
});

test("parseVisionDualOutput 在流式中途去掉残缺的 〖原文〗 标记", () => {
  for (const tail of ["〖", "〖原", "〖原文"]) {
    assert.deepEqual(bg.parseVisionDualOutput("〖译文〗你好" + tail), { translation: "你好", sourceText: "" });
  }
});

test("parseVisionDualOutput 处理原文在前和无标记的输出", () => {
  assert.deepEqual(bg.parseVisionDualOutput("〖原文〗Hello\n〖译文〗你好"), { translation: "你好", sourceText: "Hello" });
  assert.deepEqual(bg.parseVisionDualOutput("纯译文"), { translation: "纯译文", sourceText: "" });
  assert.deepEqual(bg.parseVisionDualOutput("<think>still"), { translation: "", sourceText: "" });
});

test("parseAnchoredSegments 按锚点切段, 忽略越界编号", () => {
  assert.deepEqual(bg.parseAnchoredSegments("〖P1〗二\n〖P0〗一\n〖P7〗越界", 2), { 0: "一", 1: "二" });
});

test("parseAnchoredSegments 在流式中途去掉末尾残缺的锚点", () => {
  for (const tail of ["〖", "〖P", "〖P1"]) {
    assert.deepEqual(bg.parseAnchoredSegments("〖P0〗一\n" + tail, 2), { 0: "一" });
  }
});

test("parseAnchoredSegments 无锚点时: 行数匹配才按行对齐, 单段直接返回", () => {
  assert.deepEqual(bg.parseAnchoredSegments("一\n\n二", 2), { 0: "一", 1: "二" });
  assert.deepEqual(bg.parseAnchoredSegments("一\n二\n三", 2), {});
  assert.deepEqual(bg.parseAnchoredSegments("单段", 1), { 0: "单段" });
  assert.deepEqual(bg.parseAnchoredSegments("", 3), {});
});

const VALID_DECK = JSON.stringify({
  mode: "word",
  sourceLang: "en-US",
  cards: [
    {
      term: "ephemeral",
      reading: "/ɪˈfem.ər.əl/",
      pos: "adj.",
      meaning: "短暂的",
      collocations: ["ephemeral beauty（短暂之美）"],
      example: { source: "Fame is ephemeral.", target: "名声转瞬即逝。" }
    }
  ]
});

test("parseStudyCardJson 解析合法 JSON 和 markdown 代码块", () => {
  for (const raw of [VALID_DECK, "```json\n" + VALID_DECK + "\n```"]) {
    const deck = bg.parseStudyCardJson(raw, "ephemeral");
    assert.equal(deck.mode, "word");
    assert.equal(deck.sourceLang, "en-US");
    assert.equal(deck.cards.length, 1);
    assert.equal(deck.cards[0].term, "ephemeral");
    assert.equal(deck.cards[0].reading, "/ɪˈfem.ər.əl/");
    assert.deepEqual(deck.cards[0].example, { source: "Fame is ephemeral.", target: "名声转瞬即逝。" });
  }
});

test("repairStudyJson 修复裸 IPA, 字符串内未转义引号和尾逗号", () => {
  const broken =
    '{"mode":"word","cards":[{"term":"ephemeral","reading":/ɪˈfem.ər.əl/,"meaning":"短暂的","nuance":"比 "temporary" 更文学",}]}';
  const repaired = JSON.parse(bg.repairStudyJson(broken));
  assert.equal(repaired.cards[0].reading, "/ɪˈfem.ər.əl/");
  assert.equal(repaired.cards[0].nuance, '比 "temporary" 更文学');
  const deck = bg.parseStudyCardJson(broken, "ephemeral");
  assert.equal(deck.cards[0].meaning, "短暂的");
});

test("parseStudyCardJson 按词头去重并最多保留 4 张卡", () => {
  const raw = JSON.stringify({
    cards: ["A", "a", "B", "C", "D", "E"].map((term, i) => ({ term, meaning: String(i) }))
  });
  const deck = bg.parseStudyCardJson(raw, "x");
  assert.deepEqual(
    deck.cards.map((c) => c.term),
    ["A", "B", "C", "D"]
  );
  assert.equal(deck.mode, "sentence");
});

test("parseStudyCardJson 对非 JSON 回退为单张卡, 空输入返回 null", () => {
  const deck = bg.parseStudyCardJson("not json at all", "hello there my old friend");
  assert.equal(deck.mode, "sentence");
  assert.equal(deck.cards.length, 1);
  assert.equal(deck.cards[0].term, "hello there my old friend");
  assert.equal(deck.cards[0].meaning, "not json at all");
  assert.equal(bg.parseStudyCardJson("", "x"), null);
});

test("extractPartialStudyDeck 在流式中途只返回已完整的卡片", () => {
  const partial =
    '{"mode":"sentence","sourceLang":"ja-JP","grammarNote":"主谓结构","cards":[{"term":"木漏れ日","meaning":"树叶间漏下的阳光"},{"term":"一期一会","mea';
  const deck = bg.extractPartialStudyDeck(partial, "木漏れ日");
  assert.equal(deck.streaming, true);
  assert.equal(deck.mode, "sentence");
  assert.equal(deck.sourceLang, "ja-JP");
  assert.equal(deck.grammarNote, "主谓结构");
  assert.deepEqual(
    deck.cards.map((c) => c.term),
    ["木漏れ日"]
  );
  assert.equal(bg.extractPartialStudyDeck('{"mode":"word"', "x"), null);
  assert.equal(bg.extractPartialStudyDeck("", "x"), null);
});

test("buildStudyMessages 生成 system + user, 并附带命中的术语提示", () => {
  const messages = bg.buildStudyMessages("Sovereign Scheduler", "中文", [{ source: "Sovereign Scheduler", target: "主权调度器" }]);
  assert.deepEqual(
    messages.map((m) => m.role),
    ["system", "user"]
  );
  assert.match(messages[0].content, /简体中文/);
  assert.match(messages[1].content, /Sovereign Scheduler→主权调度器/);
  assert.match(bg.buildStudyMessages("hi", "English")[0].content, /in English/);
});
