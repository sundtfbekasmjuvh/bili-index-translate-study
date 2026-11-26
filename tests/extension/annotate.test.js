"use strict";

// annotate.js 的纯函数: 词表解析, 语言判定, 词形还原, 挑词, 提示词, 回复解析, 目标语言映射.
// 除最后的"真实数据文件"测试外, 都用小样本词表.
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("fs");
const path = require("path");

const EXT = path.resolve(__dirname, "../../bili-index-translate");
const A = require(path.join(EXT, "annotate.js"));

// ---- 小样本词表 ----
// 英文排名: the1 and2 run3 make4 study5 house6 quick7 brown8 jump9 friend10 happy11 walk12 stop13
const EN = A.parseWordlist("en", "the and run make study house quick brown jump friend happy walk stop".split(" ").join("\n") + "\n");
// 西班牙文排名: de1 la2 casa3 hablar4 libro5 rápido6 amigo7 comer8
const ES = A.parseWordlist("es", "de la casa hablar libro rápido amigo comer".split(" ").join("\n") + "\n");
const ZH = A.parseWordlist("zh", "#1\n我\n喜欢\n学习\n斯\n坦\n#2\n关键字\n大学\n#3\n关键\n字\n#7\n福\n");
const TABLES = { zh: ZH, en: EN, es: ES };

function pick(lang, text, level, extra) {
  return A.pickCandidates({ text, lang, tables: TABLES, level, known: new Set(), exclude: new Set(), ...extra });
}
const ws = (r) => r.map((x) => x.w);
const keys = (r) => r.map((x) => x.key);

// ---- parseWordlist ----

test("parseWordlist: 中文按 #n 标题分级, 重复词取最低等级, 7 级为最高", () => {
  const t = A.parseWordlist("zh", "#1\n我\n\n#2\n你好\n我\n#7\n你好\n深奥\n");
  assert.equal(t.get("我"), 1);
  assert.equal(t.get("你好"), 2);
  assert.equal(t.get("深奥"), 7);
  assert.equal(t.size, 3);
});

test("parseWordlist: 英文/西班牙文以行号为排名, 忽略空行和重复, 兼容 CRLF", () => {
  const t = A.parseWordlist("en", "the\r\nof\r\n\r\nthe\r\nand\r\n");
  assert.equal(t.get("the"), 1);
  assert.equal(t.get("of"), 2);
  assert.equal(t.get("and"), 4); // 行号, 重复的行也占一个名次
  assert.equal(A.parseWordlist("es", "de\nla\n").get("la"), 2);
});

// ---- detectLang ----

test("detectLang: 含假名为 ja, 汉字占多数为 zh", () => {
  assert.equal(A.detectLang("これは日本語です"), "ja");
  assert.equal(A.detectLang("日本語のテキスト and some English", "en"), "ja");
  assert.equal(A.detectLang("这是一个关于词汇的段落。"), "zh");
  assert.equal(A.detectLang("这是一个很好的 test"), "zh");
});

test("detectLang: 拉丁字母先看 lang 属性, 带地区后缀也可以", () => {
  assert.equal(A.detectLang("zzz qqq", "es-MX"), "es");
  assert.equal(A.detectLang("zzz qqq", "EN-us"), "en");
  // lang 属性说 es, 即使功能词像英文, 也以属性为准
  assert.equal(A.detectLang("the cat is on the mat", "es"), "es");
});

test("detectLang: 没有 lang 属性时用功能词投票, 平票或没有功能词返回 null", () => {
  assert.equal(A.detectLang("The cat is on the mat and it was happy"), "en");
  assert.equal(A.detectLang("El gato es de la casa y los perros"), "es");
  assert.equal(A.detectLang("Lorem ipsum dolor sit amet"), null);
  assert.equal(A.detectLang("12345 !!!"), null);
  assert.equal(A.detectLang(""), null);
  assert.equal(A.detectLang("привет мир"), null);
  // 非 en/es 的 lang 属性不起作用, 回到投票
  assert.equal(A.detectLang("The cat is on the mat", "fr"), "en");
});

// ---- lemmaCandidates ----

test("lemmaCandidates 英文: 复数, 过去式, 进行时, 比较级, 副词", () => {
  const has = (w, lemma) => assert.ok(A.lemmaCandidates("en", w).includes(lemma), w + " -> " + lemma);
  has("studies", "study");
  has("boxes", "box");
  has("cats", "cat");
  has("running", "run"); // 双写辅音
  has("making", "make"); // 补 e
  has("walking", "walk");
  has("stopped", "stop");
  has("liked", "like");
  has("studied", "study");
  has("happier", "happy");
  has("fastest", "fast");
  has("quickly", "quick");
  has("happily", "happy");
  has("dog's", "dog");
});

test("lemmaCandidates 英文: 不返回词本身, 不返回少于 3 个字母的还原, 大小写不敏感", () => {
  const c = A.lemmaCandidates("en", "being");
  assert.ok(!c.includes("being"));
  assert.ok(c.every((x) => x.length >= 3));
  assert.ok(A.lemmaCandidates("en", "RUNNING").includes("run"));
  assert.deepEqual(A.lemmaCandidates("en", "the"), []);
});

test("lemmaCandidates 西班牙文: 复数, 阴阳性, -mente, 变位", () => {
  const has = (w, lemma) => assert.ok(A.lemmaCandidates("es", w).includes(lemma), w + " -> " + lemma);
  has("libros", "libro");
  has("casas", "casa");
  has("amigas", "amigo");
  has("papeles", "papel");
  has("luces", "luz");
  has("canciones", "canción");
  has("rápidamente", "rápido");
  has("felizmente", "feliz");
  has("hablando", "hablar");
  has("habló", "hablar");
  has("hablamos", "hablar");
  has("comiendo", "comer");
  has("comió", "comer");
  has("profesora", "profesor");
});

test("lemmaCandidates: 其他语言返回空列表", () => {
  assert.deepEqual(A.lemmaCandidates("zh", "学习"), []);
  assert.deepEqual(A.lemmaCandidates("ja", "x"), []);
});

// ---- pickCandidates: 中文 ----

test("挑词 中文: 相邻片段拼回词表里最长的词 (关键+字 -> 关键字)", () => {
  assert.deepEqual([...new Intl.Segmenter("zh", { granularity: "word" }).segment("关键字")].map((s) => s.segment), ["关键", "字"]);
  const r = pick("zh", "我喜欢关键字", 1);
  assert.deepEqual(r, [{ w: "关键字", key: "关键字" }]);
  // 水平线 2 级: 关键字 本身是 2 级, 已认识
  assert.deepEqual(pick("zh", "我喜欢关键字", 2), []);
});

test("挑词 中文: 片段不在表里时按最长匹配拆分, 每块都在水平线以内就当认识", () => {
  // 斯坦福 不在表里, 拆成 斯/坦/福: 福 是 7 级
  assert.deepEqual(pick("zh", "斯坦福", 1), [{ w: "斯坦福", key: "斯坦福" }]);
  assert.deepEqual(pick("zh", "斯坦福", 7), []);
  // 学习大学 拆成 学习 + 大学 (1 级和 2 级)
  assert.deepEqual(pick("zh", "学习大学", 2), []);
});

test("挑词 中文: 单字, 含字母数字的片段, 已认识的词都不是候选", () => {
  assert.deepEqual(pick("zh", "我 字 abc123 学习", 1), []);
  assert.deepEqual(pick("zh", "大学123", 1), [{ w: "大学", key: "大学" }]);
});

test("挑词 中文: 标点隔开的片段不会被拼起来", () => {
  // 关键，字 之间有逗号, 不能拼成 关键字
  assert.deepEqual(pick("zh", "关键，字", 1), [{ w: "关键", key: "关键" }]);
});

test("挑词 中文: 不认识的语言或缺少词表时返回空", () => {
  assert.deepEqual(pick("ja", "何か", 1), []);
  assert.deepEqual(A.pickCandidates({ text: "x", lang: "zh", tables: {}, level: 1 }), []);
});

// ---- pickCandidates: 英文 / 西班牙文 ----

test("挑词 英文: 排名在水平线以内的认识, 以外的是候选", () => {
  assert.deepEqual(ws(pick("en", "the house and the walk", 6)), ["walk"]);
  assert.deepEqual(ws(pick("en", "the house and the walk", 12)), []);
});

test("挑词: 词本身在表里时用它自己的排名和 key, 不被假原形 (butter -> but, casa -> caso) 盖掉", () => {
  const en = A.parseWordlist("en", ["but", "the", "and", "butter"].join("\n"));
  const es = A.parseWordlist("es", ["de", "caso", "la", "casa"].join("\n"));
  const p = (lang, table, text, level) =>
    A.pickCandidates({ text, lang, tables: { [lang]: table }, level, known: new Set(), exclude: new Set() });
  assert.deepEqual(p("en", en, "butter", 3), [{ w: "butter", key: "butter" }]);
  assert.deepEqual(p("es", es, "casa", 3), [{ w: "casa", key: "casa" }]);
  // 标记 but 会了, 不影响 butter
  assert.deepEqual(
    A.pickCandidates({ text: "butter", lang: "en", tables: { en }, level: 3, known: new Set(["but"]), exclude: new Set() }),
    [{ w: "butter", key: "butter" }]
  );
});

test("挑词 英文: 表外的词才取原形的排名, key 是原形", () => {
  // running 不在表里, 原形 run 排第 3
  assert.deepEqual(pick("en", "running", 2), [{ w: "running", key: "run" }]);
  assert.deepEqual(pick("en", "running", 3), []);
  assert.deepEqual(pick("en", "studies", 4), [{ w: "studies", key: "study" }]);
  // 表外的词, 没有可还原的原形: key 就是它本身, 视为最难
  assert.deepEqual(pick("en", "ephemeral", 12), [{ w: "ephemeral", key: "ephemeral" }]);
});

test("挑词 英文: 含数字或下划线, 词中间有大写, 少于 3 个字母, 都不是候选", () => {
  assert.deepEqual(pick("en", "foo2 snake_case iPhone McDonald NASA ab to", 0), []);
});

test("挑词: 带点号的“词” (node.js, example.org) 不是候选; 撇号词可以; 分解写法的重音统一成 NFC", () => {
  const ws = pick("en", "we use node.js and example.org daily", 12).map((c) => c.w);
  assert.ok(ws.includes("daily") && ws.every((w) => !w.includes(".")), JSON.stringify(ws));
  assert.deepEqual(pick("en", "rock’n’roll", 12), [{ w: "rock’n’roll", key: "rock'n'roll" }]);
  const nfd = "café"; // e + 组合重音
  const r = pick("es", "un " + nfd + " muy bueno", 0);
  assert.ok(r.some((c) => c.key === "café" && c.key.normalize("NFC") === c.key), JSON.stringify(r));
});

test("挑词 英文: 行内代码, 代码块和网址不是候选", () => {
  assert.deepEqual(ws(pick("en", "use `ephemeral` here", 12)), ["use", "here"]);
  assert.deepEqual(ws(pick("en", "see https://example.com/ephemeral and www.example.org/lazyword ok", 12)), ["see"]);
  assert.deepEqual(ws(pick("en", "a ```\nephemeral code\n``` b", 12)), []);
});

test("挑词 英文: 不在句首的大写词是专有名词, 句首大写词按小写查", () => {
  assert.deepEqual(ws(pick("en", "I met Alice yesterday", 12)), ["met", "yesterday"]);
  // 句首的 Quick: 小写 quick 在表里 (排名 7)
  assert.deepEqual(pick("en", "Quick dogs.", 3).slice(0, 1), [{ w: "Quick", key: "quick" }]);
  // 句首的 Zorblax: 小写形式不在表里, 视为专有名词
  assert.deepEqual(ws(pick("en", "Zorblax is here.", 12)), ["here"]);
  // 句号之后, 引号之后, 换行之后都算句首
  assert.deepEqual(ws(pick("en", "It ended. Quick work.", 3)), ["ended", "Quick", "work"]);
  assert.deepEqual(ws(pick("en", 'He said: "Quick work".', 3)), ["said", "work"]);
  assert.deepEqual(ws(pick("en", 'He said. "Quick work".', 3)), ["said", "Quick", "work"]);
  assert.deepEqual(ws(pick("en", "walk\nQuick", 3)), ["walk", "Quick"]);
});

test("挑词 英文: 六成以上的词首字母大写时当作标题, 全部转小写再查", () => {
  // Zorblax 在标题里也是候选 (表外, 最难)
  assert.deepEqual(ws(pick("en", "Brown Quick Zorblax", 3)), ["Brown", "Quick", "Zorblax"]);
  assert.deepEqual(keys(pick("en", "Brown Quick Zorblax", 3)), ["brown", "quick", "zorblax"]);
  // 不到六成: 照常处理, 中间的大写词是专有名词
  assert.deepEqual(ws(pick("en", "we saw Brown here today", 3)), ["saw", "here", "today"]);
});

test("挑词 西班牙文: 还原到原形, 排名按原形算", () => {
  const r = pick("es", "hablando libros rápidamente casas", 2);
  assert.deepEqual(r, [
    { w: "hablando", key: "hablar" },
    { w: "libros", key: "libro" },
    { w: "rápidamente", key: "rápido" },
    { w: "casas", key: "casa" },
  ]);
  assert.deepEqual(ws(pick("es", "hablando libros rápidamente casas", 6)), []);
});

test("挑词 西班牙文: 带重音和 ñ 的词可以切出来, 句首的倒问号不影响判断", () => {
  assert.deepEqual(ws(pick("es", "¿Qué niño sabe?", 12)).includes("niño"), true);
  // 句首 ¿ 后面的大写词按句首处理: Casa 的小写 casa 在表里
  assert.deepEqual(pick("es", "¿Casa?", 2), [{ w: "Casa", key: "casa" }]);
  // 非句首大写: 专有名词
  assert.deepEqual(pick("es", "vive en Madrid", 0).map((x) => x.w).includes("Madrid"), false);
});

// ---- pickCandidates: 通用 ----

test("挑词: 已会的词 (known) 和排除表 (exclude) 里的 key 被过滤, 用原形匹配", () => {
  assert.deepEqual(ws(pick("en", "running walking", 2, { known: new Set(["run"]) })), ["walking"]);
  assert.deepEqual(ws(pick("en", "running walking", 2, { exclude: new Set(["walk"]) })), ["running"]);
  assert.deepEqual(pick("zh", "关键字 大学", 1, { known: new Set(["关键字"]) }), [{ w: "大学", key: "大学" }]);
});

test("挑词: 同一段里重复的词 (包括同一原形的不同写法) 只取第一次", () => {
  assert.deepEqual(pick("en", "walk walk Walk walking", 3), [{ w: "walk", key: "walk" }]);
  assert.deepEqual(pick("zh", "大学 大学 大学", 1), [{ w: "大学", key: "大学" }]);
});

test("挑词: 超出每段上限时保留最难的词, 输出仍按原文顺序", () => {
  const r = pick("en", "house quick brown jump", 3, { maxPerPara: 2 });
  assert.deepEqual(ws(r), ["brown", "jump"]); // 排名 8 和 9
  assert.equal(pick("en", "house quick brown jump friend happy walk stop ephemeral lazyword", 3).length, 8);
  // 表外的词 (排名无穷) 最难
  assert.deepEqual(ws(pick("en", "walk ephemeral stop", 3, { maxPerPara: 1 })), ["ephemeral"]);
  // 中文按 HSK 等级: 福 (7 级) 比 大学 (2 级) 难
  assert.deepEqual(ws(pick("zh", "大学 斯坦福", 1, { maxPerPara: 1 })), ["斯坦福"]);
});

test("挑词: 默认每段上限为 8", () => {
  const text = "a1 ".repeat(0) + "zzaa zzbb zzcc zzdd zzee zzff zzgg zzhh zzii zzjj";
  assert.equal(pick("en", text, 0).length, 8);
});

// ---- buildAnnotatePrompt ----

test("提示词 A3 单段 (中文释义): 逐字对照交接文档 3.1", () => {
  const { messages, k } = A.buildAnnotatePrompt([{ text: "Beijing is big.", words: ["Beijing", "big"] }], "zh");
  assert.equal(k, 2);
  assert.equal(messages.length, 1);
  assert.equal(messages[0].role, "user");
  const expected = [
    "请阅读下面的【段落】，只为【词表】中的每个词给出它在这段话中的语境释义，并且严格遵循所有约束要求。",
    "",
    "【段落】",
    "Beijing is big.",
    "",
    "【词表】",
    "1. Beijing",
    "2. big",
    "",
    "【约束要求】",
    "1. 【硬性要求】恰好输出 2 行，每行格式为“编号. 词 => 释义”，编号从 1 到 2，例如：1. 词 => 释义",
    "2. 【硬性要求】“词”必须与词表里的字符完全一致：词表里是什么语言就原样抄什么语言，不能换成译文，不要加注音或其他文字",
    "3. 【硬性要求】释义必须用简体中文书写，写成对应的词或短语（像词典的简明对译），不要写成定义、解释或描述；专有名词给通用译名，不超过12个汉字",
    "4. 只处理词表中的词，不要翻译整段，不要有前言、解释或代码块",
    "",
    "只输出这 2 行，不要有任何额外说明。",
  ].join("\n");
  assert.equal(messages[0].content, expected);
});

test("提示词 A3 单段 (英文释义): 用英语, 不超过6个英文单词", () => {
  const c = A.buildAnnotatePrompt([{ text: "段落", words: ["关键字"] }], "en").messages[0].content;
  assert.ok(c.includes("释义必须用英语书写"));
  assert.ok(c.includes("不超过6个英文单词"));
  assert.ok(!c.includes("简体中文"));
  assert.ok(!c.includes("12个汉字"));
});

test("提示词 M1 多段: 编号跨段连续, 词表和段落按段编号", () => {
  const { messages, k } = A.buildAnnotatePrompt(
    [
      { text: "para one", words: ["aaa", "bbb"] },
      { text: "para two", words: ["ccc"] },
      { text: "para three", words: ["ddd", "eee"] },
    ],
    "zh"
  );
  assert.equal(k, 5);
  assert.equal(messages.length, 1);
  const c = messages[0].content;
  assert.ok(c.startsWith("请阅读下面的3段文字，只为每段自己的【词表】中的每个词给出它在该段话中的语境释义，并且严格遵循所有约束要求。"));
  const order = ["【段落1】", "para one", "【词表1】", "1. aaa", "2. bbb", "【段落2】", "para two", "【词表2】", "3. ccc", "【段落3】", "para three", "【词表3】", "4. ddd", "5. eee"];
  let at = -1;
  for (const s of order) {
    const i = c.indexOf(s, at + 1);
    assert.ok(i > at, "顺序: " + s);
    at = i;
  }
  assert.ok(c.includes("1. 【硬性要求】全部词表共 5 个词，恰好输出 5 行"));
  assert.ok(c.includes("编号从 1 到 5，与词表中的编号一致（跨段连续），按词表顺序输出，不要分段，不要写段落标记"));
  assert.ok(c.endsWith("只输出这 5 行，不要有任何额外说明。"));
  assert.ok(!c.includes("【段落】"));
});

// ---- parseAnnotateReply ----

const W3 = ["北京", "Beijing", "学生"];

test("解析规则1: 逐行宽容解析, 容忍行尾空格, 代码围栏和空行", () => {
  const reply = "```\n1. 北京 => Beijing  \n\n2) Beijing → 北京   \n3、学生：student\n```";
  const r = A.parseAnnotateReply(reply, W3, { glossLang: "en" });
  assert.equal(r.ok, true);
  assert.equal(r.glosses[0], "Beijing");
  assert.equal(r.glosses[2], "student");
  // 第 2 条释义是中文, 释义语言不对, 单条丢掉
  assert.equal(r.glosses[1], null);
});

test("解析规则2: 按编号对应, 不看抄回来的词", () => {
  // 模型把 北京 抄成 Beijing
  const r = A.parseAnnotateReply("1. Beijing => Beijing\n2. foo => 北京\n3. 学生 => student", W3, { glossLang: "en" });
  assert.equal(r.ok, true);
  assert.equal(r.glosses[0], "Beijing");
  // 编号乱序时仍按编号放回
  const r2 = A.parseAnnotateReply("3. x => 学生\n1. y => 北京\n2. z => Beijing", W3, { glossLang: "zh" });
  assert.deepEqual(r2.glosses, ["北京", null, "学生"]);
});

test("解析规则3: 行数不对, 编号重复, 无法解析文字超过 10 个字符, finish_reason 为 length, 整段失败", () => {
  const bad = (content, opts, re) => {
    const r = A.parseAnnotateReply(content, W3, { glossLang: "zh", ...opts });
    assert.equal(r.ok, false);
    assert.deepEqual(r.glosses, [null, null, null]);
    assert.match(r.reason, re);
  };
  bad("1. a => 一\n2. b => 二", {}, /expected 3 lines, got 2/);
  bad("1. a => 一\n2. b => 二\n3. c => 三\n4. d => 四", {}, /expected 3 lines, got 4/);
  bad("1. a => 一\n1. b => 二\n3. c => 三", {}, /duplicate/);
  bad("1. a => 一\n2. b => 二\n3. c => 三\n这是一段很长很长的额外说明文字", {}, /unparsable/);
  bad("1. a => 一\n2. b => 二\n3. c => 三", { finishReason: "length" }, /length/);
  bad("1. a => 一\n2. b => 二\n7. c => 三", {}, /out of range/);
  bad("", {}, /expected 3 lines, got 0/);
});

test("解析规则3: 少量无法解析的文字 (不超过 10 个字符) 可以容忍", () => {
  const r = A.parseAnnotateReply("好的:\n1. a => 一\n2. b => 二\n3. c => 三", W3, { glossLang: "zh" });
  assert.equal(r.ok, true);
  assert.equal(r.glosses.every(Boolean), true);
  assert.equal(r.reason, "");
});

test("解析规则4: 释义与原词相同是合法的", () => {
  const r = A.parseAnnotateReply("1. Internet => Internet\n2. x => 学生\n3. 学生 => 学生", ["Internet", "x", "学生"], { glossLang: "en" });
  assert.equal(r.glosses[0], "Internet");
  const z = A.parseAnnotateReply("1. 学生 => 学生", ["学生"], { glossLang: "zh" });
  assert.deepEqual(z.glosses, ["学生"]);
});

test("解析规则5: 单条释义不合格只丢这一条, 不影响整段", () => {
  const long = "字".repeat(41);
  const ok40 = "字".repeat(40);
  const r = A.parseAnnotateReply("1. a => " + long + "\n2. b => " + ok40 + "\n3. c => English", ["a", "b", "c"], { glossLang: "zh" });
  assert.equal(r.ok, true);
  assert.deepEqual(r.glosses, [null, ok40, null]);
  // 英文: 超过 12 个词, 或者含汉字为主, 丢掉
  const words13 = Array.from({ length: 13 }, () => "w").join(" ");
  const words12 = Array.from({ length: 12 }, () => "w").join(" ");
  const e = A.parseAnnotateReply("1. a => " + words13 + "\n2. b => " + words12 + "\n3. c => 学生", ["a", "b", "c"], { glossLang: "en" });
  assert.equal(e.ok, true);
  assert.deepEqual(e.glosses, [null, words12, null]);
});

test("解析: 结果和 words 等长, 没有词时总是成功", () => {
  const r = A.parseAnnotateReply("", [], { glossLang: "zh" });
  assert.deepEqual(r, { ok: true, glosses: [], reason: "" });
  assert.equal(A.parseAnnotateReply("1. a => 一", ["a"], { glossLang: "zh" }).glosses.length, 1);
});

// ---- glossLangForTarget ----

test("glossLangForTarget: 中文类 -> zh, English/en -> en, 其他 -> null", () => {
  for (const t of ["中文", "简体中文", "zh", "zh-CN", "zh-Hans", "ZH_cn", "Chinese", "Simplified Chinese", "chinese (simplified)"]) {
    assert.equal(A.glossLangForTarget(t), "zh", t);
  }
  for (const t of ["English", "english", "en", "en-US", "EN"]) assert.equal(A.glossLangForTarget(t), "en", t);
  for (const t of ["日本語", "Japanese", "Español", "French", "", null, undefined, "Entlang"]) {
    assert.equal(A.glossLangForTarget(t), null, String(t));
  }
});

test("glossLangForTarget: 繁体中文目标不提供注释 (返回 null)", () => {
  for (const t of ["繁體中文", "繁体中文", "Traditional Chinese", "zh-TW", "zh-HK", "zh-Hant", "Chinese (Traditional)"]) {
    assert.equal(A.glossLangForTarget(t), null, t);
  }
});

// ---- 真实数据文件 ----

test("真实词表: 格式正确, 抽查已知词", () => {
  const read = (f) => fs.readFileSync(path.join(EXT, "data", f), "utf8");
  const zh = A.parseWordlist("zh", read("hsk30.txt"));
  const en = A.parseWordlist("en", read("freq-en.txt"));
  const es = A.parseWordlist("es", read("freq-es.txt"));
  assert.equal(zh.get("的"), 1);
  assert.equal(zh.get("我"), 1);
  assert.ok(zh.get("斯文") === undefined || zh.get("斯文") >= 1);
  assert.ok([...zh.values()].every((v) => v >= 1 && v <= 7));
  assert.ok(zh.size > 10000);
  assert.ok(en.get("the") <= 3);
  assert.ok(en.get("house") < en.get("ephemeral") || !en.has("ephemeral"));
  assert.ok(en.size >= 40000);
  assert.ok(es.get("de") <= 3);
  assert.ok(es.get("casa") < 2000);
  assert.ok(es.size >= 40000);
  // 用真实表跑一遍端到端的挑词
  const tables = { zh, en, es };
  const r = A.pickCandidates({ text: "The cat was running quickly.", lang: "en", tables, level: 5000, known: new Set(), exclude: new Set() });
  assert.deepEqual(r, []);
  const r2 = A.pickCandidates({ text: "我喜欢学习汉语。", lang: "zh", tables, level: 1, known: new Set(), exclude: new Set() });
  assert.ok(r2.every((x) => x.w.length >= 2));
});
