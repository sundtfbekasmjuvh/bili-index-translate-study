// i18n.js - interface text in English and Simplified Chinese, shared by the background worker,
// the content script and the options page. Model output and server replies are not translated.
(function (root) {
  "use strict";

  const LANGS = ["en", "zh-CN"];

  // Text used from code. Both languages have the same keys; {0}, {1} ... are filled in by translate().
  const MESSAGES = {
    en: {
      "panel.aria": "Bili Index Translate",
      "panel.speak": "Read the source text aloud",
      "panel.copy": "Copy the translation (C)",
      "panel.studyToggle": "Show or hide the Study Card (S)",
      "panel.studyOpen": "Open the Study Card",
      "panel.studyLabel": "Study Card",
      "panel.close": "Close",
      "panel.translating": "Translating…",
      "panel.visionLoading": "Translating the image…",
      "panel.streaming": "streaming…",
      "panel.visionStreaming": "reading the image…",
      "panel.sourceTag": "Source",
      "panel.visionTag": "image",
      "panel.glossaryHits": "glossary ×{0}",
      "panel.cached": "⚡ cached",
      "panel.studyHint": "S Study Card",

      "error.noResponse": "No response from background worker.",
      "error.requestFailed": "Request failed",
      "error.hint412Relay": "(The relay was also rejected; check the Relay prefix, Base URL and API key in extension options.)",
      "error.hint412Direct":
        "(Upstream rejected the direct request. If you trust a relay, set the Relay prefix in extension options; nothing is switched automatically.)",
      "error.decodeCapture": "Failed to decode captured viewport image.",
      "error.visionPort": "Vision streaming port unavailable.",
      "error.capture": "Unable to capture tab region.",
      "error.imageLoad": "Failed to load image.",

      "study.aria": "Bili Language Study Card",
      "study.title": "Study Card",
      "study.quiz": "Quiz",
      "study.quizExit": "End quiz",
      "study.quizTitle": "Self-test: hide the meaning until you reveal it (F)",
      "study.copyTitle": "Copy this card as notes (C)",
      "study.close": "Close the Study Card",
      "study.compact": "Compact",
      "study.compactTitle": "Compact view: meaning and example only (click or press D for nuance, memory aid and collocations)",
      "study.detailed": "Detailed",
      "study.detailedTitle": "Detailed view: nuance, memory aid and collocations shown (click or press D for the compact view)",
      "study.flagshipTitle": "Using 35B-A3B, the flagship model (click or press M to go back to 9B)",
      "study.balancedTitle": "Using 9B, the balanced model (click or press M to switch to 35B-A3B)",
      "study.copied": "Card copied",
      "study.noSpeech": "This browser has no speech synthesis",
      "study.slowReading": "Reading at 0.7x…",
      "study.speechBlocked": "Speech playback was blocked",
      "study.loadingFlagship": "35B is analysing…",
      "study.loadingBalanced": "9B is analysing…",
      "study.noTrace": "Nothing stored",
      "study.retry": "Retry",
      "study.prev": "Previous word (←)",
      "study.next": "Next word (→)",
      "study.loadingMore": "Loading the other cards…",
      "study.footKeys": "Space speak · F quiz · D compact",
      "study.grammar": "Grammar",
      "study.inText": "In text: {0}",
      "study.speak": "Speak",
      "study.speakTitle": "Speak at normal speed (Space)",
      "study.slowTitle": "Speak slowly at 0.7x (Shift+Space)",
      "study.reveal": "Click or press F to reveal the meaning",
      "study.nuance": "Nuance",
      "study.memory": "Memory aid",
      "study.colloc": "Collocations",
      "study.collocSpeak": "Click to hear this phrase",
      "study.exampleSpeak": "Speak the example (E)",
      "study.exampleSlow": "Speak the example at 0.7x (Shift+E)",
      "study.md.meaning": "Meaning: {0}",
      "study.md.nuance": "Nuance: {0}",
      "study.md.memory": "Memory aid: {0}",
      "study.md.colloc": "Collocations: {0}",
      "study.md.example": "Example: {0}",
      "study.md.grammar": "Grammar: {0}",

      "snip.hint": "Drag over an image, a comic panel or video subtitles to translate · Esc cancels",

      "imm.title": "Bilingual page",
      "imm.modeTitle": "Switch between the bilingual and the translation-only layout",
      "imm.modelTitle": "Switch the page translation model (2B fastest / 9B more precise)",
      "imm.closeTitle": "Leave bilingual mode and restore the page (Alt+A)",
      "imm.targetOnly": "Target only",
      "imm.bilingual": "Bilingual",
      "imm.none": "No foreign-language paragraphs found",
      "imm.busy": "Translating {0}/{1}",
      "imm.ready": "Ready {0}/{1}",
      "imm.failed": "[failed]",

      "menu.translate": "Translate selection with Bili",
      "menu.study": "Open Bili Study Card",
      "menu.image": "Translate image with Bili",
      "menu.immersive": "Toggle bilingual page translation (Alt+A)",

      "bg.timeout": "Request timed out after {0} s",
      "bg.notJson": "Response is not JSON: {0}",
      "bg.deeplxNoData": 'No translation in "data": {0}',
      "bg.customNoResult": 'No translation at "{0}" in: {1}',
      "bg.customCount": 'Expected {0} translation(s) at "{1}" but found {2}.',
      "bg.custom.headerLine": 'Custom API: the header line "{0}" is not in the form "Name: value".',
      "bg.custom.resultPathInvalid": 'Custom API: the result path "{0}" is not valid. Use a path like translations[0].text or data.',
      "bg.custom.resultPathEmpty":
        "Custom API: the result path is empty. It says where the translation is in the JSON reply, for example translations[0].text.",
      "bg.custom.errorPathInvalid": 'Custom API: the error path "{0}" is not valid.',
      "bg.custom.noText": "Custom API: the template never sends the text. Put {text} in the URL or the body, or {texts} in the body.",
      "bg.custom.bodyNotJson":
        "Custom API: the request body is not valid JSON once the placeholders are filled in. Write {text} inside the quotes of a string and {texts} without quotes.",
      "bg.unsupported":
        "{0} needs an OpenAI-compatible chat model. The {1} API format only translates text; change the API format in the extension options to use it.",
      "bg.feature.image": "Image translation",
      "bg.feature.study": "Study Card",
      "bg.format.deeplx": "DeepLX",
      "bg.format.custom": "custom",
      "bg.image.emptyUrl": "Empty image URL.",
      "bg.image.http": "Failed to fetch image (HTTP {0}).",
      "bg.image.empty": "Image is empty.",
      "bg.image.tooLarge": "Image exceeds 8 MB limit; use Alt+S region snip instead.",
      "bg.image.timeout": "Image download timed out after {0} s.",
      "bg.noContent": "No choices[0].message.content in: {0}",
      "bg.nothingToTranslate": "Nothing to translate.",
      "bg.tooLong": "Selection is longer than {0} characters.",
      "bg.emptyTranslation": "Empty translation.",
      "bg.invalidImage": "Invalid image payload.",
      "bg.noTextInImage": "No readable text found in image.",
      "bg.emptyBatch": "Empty batch.",
      "bg.nothingToAnalyze": "Nothing to analyze.",
      "bg.emptyStudy": "Empty study card response.",

      "opt.ruleOne": "1 rule",
      "opt.ruleMany": "{0} rules",
      "opt.presetLoaded": "Preset loaded: {0}. Check the URL, enter your API key, then press Save & test.",
      "opt.mismatchOpenAiRoot":
        "This URL looks like an OpenAI-compatible root. The DeepLX format needs the full translate URL (it usually ends in /translate).",
      "opt.mismatchApiKey":
        "The URL contains {apiKey}, which the OpenAI-compatible format does not fill in. Choose DeepLX-compatible or Custom JSON API, or remove the placeholder.",
      "opt.mismatchDeeplx": "This URL looks like a DeepLX endpoint. If requests fail, set API format to DeepLX-compatible.",
      "opt.baseInvalid": "Base URL must be an http(s) URL.",
      "opt.proxyInvalid": "Relay prefix must be empty or an http(s) URL.",
      "opt.saved": "Saved.",
      "opt.savedRules": "Saved ({0} glossary rules active).",
      "opt.savedNoHost": "Saved, but host access was declined - requests to that host may fail (CORS).",
      "opt.testing": "Testing {0} ...",
      "opt.ok": "OK via {0} ({1}):\n{2}",
      "opt.failed": "Failed: {0}",
      "opt.noResponse": "no response",
      "opt.reset": "Defaults restored."
    },

    "zh-CN": {
      "panel.aria": "Bili Index Translate",
      "panel.speak": "朗读选中原文",
      "panel.copy": "复制译文（快捷键 C）",
      "panel.studyToggle": "展开/收起伴随语言学习卡片（快捷键 S）",
      "panel.studyOpen": "打开语言学习卡片",
      "panel.studyLabel": "学习卡片",
      "panel.close": "关闭",
      "panel.translating": "翻译中…",
      "panel.visionLoading": "识图直译中…",
      "panel.streaming": "流式输出中…",
      "panel.visionStreaming": "识图流式中…",
      "panel.sourceTag": "原文",
      "panel.visionTag": "识图",
      "panel.glossaryHits": "术语×{0}",
      "panel.cached": "⚡瞬时",
      "panel.studyHint": "S 词卡",

      "error.noResponse": "后台没有响应。",
      "error.requestFailed": "请求失败",
      "error.hint412Relay": "（中继也被拒绝；请在扩展选项里检查中继前缀、接口地址和 API Key。）",
      "error.hint412Direct": "（上游拒绝了直连请求。如果有信任的中继，可在扩展选项里填写中继前缀；扩展不会自动切换线路。）",
      "error.decodeCapture": "截图解码失败。",
      "error.visionPort": "无法连接识图通道。",
      "error.capture": "无法截取页面区域。",
      "error.imageLoad": "图片加载失败。",

      "study.aria": "Bili 语言学习卡片",
      "study.title": "语言学习卡片",
      "study.quiz": "自测",
      "study.quizExit": "退出自测",
      "study.quizTitle": "主动回忆自测翻转（快捷键 F）",
      "study.copyTitle": "复制当前词卡笔记（快捷键 C）",
      "study.close": "关闭学习卡片",
      "study.compact": "精简",
      "study.compactTitle": "当前精简视图：仅显示释义与例句（点击或按 D 展开详尽辨析与助记）",
      "study.detailed": "详尽",
      "study.detailedTitle": "当前详尽视图：显示辨析、助记与搭配（点击或按 D 切换精简视图）",
      "study.flagshipTitle": "当前 35B-A3B 旗舰模式（点击或按 M 切回 9B 均衡模式）",
      "study.balancedTitle": "当前 9B 均衡模式（点击或按 M 切换 35B-A3B 旗舰精析）",
      "study.copied": "已复制词卡",
      "study.noSpeech": "当前浏览器不支持语音合成",
      "study.slowReading": "0.7x 慢读跟读中…",
      "study.speechBlocked": "语音播放受限",
      "study.loadingFlagship": "35B 旗舰拆解中…",
      "study.loadingBalanced": "9B 流式拆解中…",
      "study.noTrace": "零留痕",
      "study.retry": "重试",
      "study.prev": "上一个词 (←)",
      "study.next": "下一个词 (→)",
      "study.loadingMore": "正在加载其余词卡…",
      "study.footKeys": "Space朗读 · F自测 · D精简",
      "study.grammar": "句法",
      "study.inText": "文中: {0}",
      "study.speak": "发音",
      "study.speakTitle": "常速朗读发音（快捷键 Space）",
      "study.slowTitle": "0.7x 慢速清晰跟读（快捷键 Shift+Space）",
      "study.reveal": "点击或按 F 揭晓释义与解析",
      "study.nuance": "辨析",
      "study.memory": "助记",
      "study.colloc": "搭配",
      "study.collocSpeak": "点击朗读该搭配短语",
      "study.exampleSpeak": "常速朗读例句（快捷键 E）",
      "study.exampleSlow": "0.7x 慢读例句（快捷键 Shift+E）",
      "study.md.meaning": "释义：{0}",
      "study.md.nuance": "辨析：{0}",
      "study.md.memory": "助记：{0}",
      "study.md.colloc": "搭配：{0}",
      "study.md.example": "例句：{0}",
      "study.md.grammar": "句法：{0}",

      "snip.hint": "拖拽框选图片 / 漫画 / 视频字幕区域直译 · Esc 取消",

      "imm.title": "双语对照",
      "imm.modeTitle": "切换「双语对照」与「仅显译文」排版",
      "imm.modelTitle": "切换网页双语流式模型（2B 极速 / 9B 精译）",
      "imm.closeTitle": "退出双语并无损还原原网页（快捷键 Alt+A）",
      "imm.targetOnly": "仅译",
      "imm.bilingual": "双语",
      "imm.none": "未检测到外文段落",
      "imm.busy": "流式译配 {0}/{1}",
      "imm.ready": "双语就绪 {0}/{1}",
      "imm.failed": "[译配失败]",

      "menu.translate": "用 Bili 翻译选中的文本",
      "menu.study": "打开 Bili 学习卡片",
      "menu.image": "用 Bili 识图翻译这张图片",
      "menu.immersive": "开关双语对照网页 (Alt+A)",

      "bg.timeout": "请求超时（{0} 秒）",
      "bg.notJson": "响应不是 JSON：{0}",
      "bg.deeplxNoData": '"data" 里没有译文：{0}',
      "bg.customNoResult": '在 "{0}" 处没有找到译文：{1}',
      "bg.customCount": '"{1}" 处应有 {0} 条译文，实际有 {2} 条。',
      "bg.custom.headerLine": '自定义接口：请求头 "{0}" 不是 "Name: value" 的形式。',
      "bg.custom.resultPathInvalid": '自定义接口：结果路径 "{0}" 无效。请使用 translations[0].text 或 data 这样的路径。',
      "bg.custom.resultPathEmpty": "自定义接口：结果路径为空。它指出译文在 JSON 应答里的位置，例如 translations[0].text。",
      "bg.custom.errorPathInvalid": '自定义接口：错误路径 "{0}" 无效。',
      "bg.custom.noText": "自定义接口：模板没有发送待翻译的文本。请在地址或请求体里写 {text}，或在请求体里写 {texts}。",
      "bg.custom.bodyNotJson":
        "自定义接口：填入占位符后，请求体不是合法的 JSON。{text} 要写在字符串的引号内，{texts} 不加引号。",
      "bg.unsupported": "{0}需要 OpenAI 兼容的对话模型。{1}接口格式只能翻译文本；请在扩展选项里更换接口格式后再使用。",
      "bg.feature.image": "识图翻译",
      "bg.feature.study": "学习卡片",
      "bg.format.deeplx": "DeepLX ",
      "bg.format.custom": "自定义",
      "bg.image.emptyUrl": "图片地址为空。",
      "bg.image.http": "图片下载失败 (HTTP {0})。",
      "bg.image.empty": "图片内容为空。",
      "bg.image.tooLarge": "图片超过 8 MB 上限，请改用 Alt+S 框选。",
      "bg.image.timeout": "图片下载超时（{0} 秒）。",
      "bg.noContent": "响应里没有 choices[0].message.content：{0}",
      "bg.nothingToTranslate": "没有可翻译的内容。",
      "bg.tooLong": "选中的文本超过 {0} 个字符。",
      "bg.emptyTranslation": "译文为空。",
      "bg.invalidImage": "图片数据无效。",
      "bg.noTextInImage": "图片里没有识别到文字。",
      "bg.emptyBatch": "没有要翻译的段落。",
      "bg.nothingToAnalyze": "没有可分析的内容。",
      "bg.emptyStudy": "学习卡片的响应为空。",

      "opt.ruleOne": "1 条规则",
      "opt.ruleMany": "{0} 条规则",
      "opt.presetLoaded": "已载入预设：{0}。请核对地址，填写 API Key，然后点「保存并测试」。",
      "opt.mismatchOpenAiRoot": "这个地址像是 OpenAI 兼容接口的根地址。DeepLX 格式需要完整的翻译地址（通常以 /translate 结尾）。",
      "opt.mismatchApiKey": "地址里含有 {apiKey}，OpenAI 兼容格式不会填充它。请改选 DeepLX 兼容或自定义 JSON 接口，或者去掉这个占位符。",
      "opt.mismatchDeeplx": "这个地址像是 DeepLX 接口。如果请求失败，请把接口格式改为 DeepLX 兼容。",
      "opt.baseInvalid": "接口地址必须是 http(s) 地址。",
      "opt.proxyInvalid": "中继前缀必须留空或是 http(s) 地址。",
      "opt.saved": "已保存。",
      "opt.savedRules": "已保存（{0} 条术语规则生效）。",
      "opt.savedNoHost": "已保存，但没有授予该主机的访问权限，发往它的请求可能失败 (CORS)。",
      "opt.testing": "正在测试 {0} ...",
      "opt.ok": "测试通过，线路 {0}（{1}）：\n{2}",
      "opt.failed": "失败：{0}",
      "opt.noResponse": "没有响应",
      "opt.reset": "已恢复默认设置。"
    }
  };

  // Static text of options.html. The English text is the markup in that file; another language
  // replaces the content of each element that carries the matching data-i18n key. The values are
  // fixed strings from this file, so they are safe to assign as HTML.
  const PAGE_TEXT = {
    "zh-CN": {
      "page.title": "Bili Index Translate — 设置",
      "page.lead":
        "MV3 原生直连 · <code>instTrans</code> 术语表 · <code>Alt+A</code> 双语对照 · <code>Alt+S</code> 零 OCR 框选识图 · 不保存任何历史。",
      "base.label": "接口地址 (Base URL)",
      "base.help": "OpenAI 兼容接口的根地址（会自动追加 <code>/chat/completions</code>）；接口格式为 DeepLX 时填写完整的翻译地址。",
      "apiType.label": "接口格式",
      "apiType.openai": "OpenAI 兼容 (/chat/completions)",
      "apiType.deeplx": "DeepLX 兼容 (/translate)",
      "apiType.custom": "自定义 JSON 接口（模板）",
      "apiType.help":
        "DeepLX：接口地址按填写的原样使用，例如 <code>https://host/translate?token={apiKey}</code>。<code>{apiKey}</code> 会被替换为 API Key；地址里没有它时 key 以 <code>Bearer</code> 方式发送，key 为空则不带鉴权头。填了中继前缀时，它必须是完整的中继后地址。这种格式只翻译划词和 <kbd>Alt+A</kbd> 页面（一批段落合并为一次请求，每行一段）：学习卡片、识图翻译、术语表和模型选择需要 OpenAI 兼容接口。目标语言可填语言名称或 DeepL 代码（<code>ZH</code>、<code>EN</code>、<code>PT-BR</code>）。",
      "custom.label": "自定义接口模板",
      "custom.badge": "任意 JSON 接口",
      "custom.presetNone": "载入预设…",
      "custom.presetLibre": "LibreTranslate（把主机名换成你的实例）",
      "custom.presetHelp": "预设只填写接口地址和下面的字段，按保存之前不会写入任何设置。任何以 JSON 应答的服务都可以在这里描述。",
      "custom.name": "显示名称",
      "custom.method": "请求方法",
      "custom.headers": "请求头",
      "custom.headersHelp":
        "每行一个 <code>Name: value</code>。这里没有写 <code>Content-Type</code> 时，请求体按 <code>application/json</code> 发送。",
      "custom.body": "请求体",
      "custom.resultPath": "结果路径",
      "custom.errorPath": "错误路径（可选）",
      "custom.placeholdersHelp":
        '接口地址、请求头和请求体里可用的占位符：<code>{text}</code> 待翻译的文本 · <code>{texts}</code> 一次请求里的全部文本，JSON 数组（只能用在请求体里；这样 <kbd>Alt+A</kbd> 的一批段落只需一次请求）· <code>{apiKey}</code> · <code>{target}</code> 填写的目标语言原文 · <code>{targetLang}</code> / <code>{targetLangLower}</code> 目标语言代码，如 <code>ZH</code> / <code>zh</code>。在 JSON 请求体里，<code>"{text}"</code> 要写在引号内，<code>{texts}</code> 不加引号。结果路径指出译文在 JSON 应答里的位置，例如 <code>data</code>、<code>translations[0].text</code>，或与 <code>{texts}</code> 配合使用的 <code>translations[*].text</code>。和 DeepLX 一样，这种格式只翻译划词和 <kbd>Alt+A</kbd> 页面。',
      "proxy.label": "中继前缀（可选）",
      "proxy.placeholder": "留空即直连 Bilibili 接口（已启用 MV3 DNR）",
      "proxy.help":
        "留空即直连 <code>index-translate.bilibili.com</code>（不需要 Python 代理）。请求不会自行改走中继：直连返回 HTTP 412 时只显示错误，由你决定。中继会收到你选中的文本、图片和 API Key，只填写你信任的地址。",
      "model.label": "划词模型",
      "model.2b": "Index-Translate-2B（最快 · 学习卡片自动改用 9B）",
      "model.9b": "Index-Translate-9B（推荐，均衡）",
      "model.35b": "Index-Translate-35B-A3B（旗舰 MoE）",
      "model.nailong": "Index-Nailong-9B（229K 长上下文）",
      "immersive.label": "双语对照模型 (Alt+A)",
      "immersive.2b": "Index-Translate-2B（最快）",
      "immersive.9b": "Index-Translate-9B（更精确）",
      "immersive.35b": "Index-Translate-35B-A3B（旗舰 MoE）",
      "vision.label": "识图模型（Alt+S 框选与右键图片）",
      "vision.badge": "多模态",
      "vision.35b": "Index-Translate-35B-A3B（最准确的 MoE · 推荐）",
      "vision.9b": "Index-Translate-9B（识图 9B）",
      "vision.2b": "Index-Translate-2B（识图 2B）",
      "vision.help": "一次请求同时提取外文并翻译，图片、漫画和暂停的视频画面也能朗读和打开学习卡片（<kbd>S</kbd>）。",
      "target.label": "目标语言",
      "key.help":
        "API Key 只保存在本机，不随浏览器账号同步。目标语言示例：<code>中文</code>、<code>繁體中文</code>、<code>English</code>、<code>日本語</code>、<code>한국어</code>。",
      "uiLang.label": "界面语言",
      "uiLang.auto": "自动（跟随浏览器语言）",
      "uiLang.help": "作用于本页、翻译面板、学习卡片和右键菜单；保存后生效。",
      "glossary.label": "自定义术语表与免译锁定 (instTrans)",
      "glossary.placeholder":
        "# 每行一条规则（每次请求只注入命中的术语）：\nSovereign Scheduler => 主权调度器\nContext Engineering -> 上下文工程\nFlash-Attention\nPyTorch",
      "glossary.help":
        "格式：<code>Source =&gt; Target</code>（也可用 <code>-&gt;</code> / <code>=</code>）指定译名；只写一个词（如 <code>Flash-Attention</code>）表示免译锁定。行内 <code>`code`</code> 会自动保护。",
      "button.save": "保存",
      "button.test": "保存并测试",
      "button.reset": "恢复默认",
      "shortcuts.vision":
        "<strong>零 OCR 框选识图与图片：</strong> <kbd>Alt+S</kbd> 框选任意图片、漫画对话框、图表或暂停的视频画面（也可右键图片）· 译文流式显示，并可用 <kbd>S</kbd> 打开学习卡片和朗读。",
      "shortcuts.immersive":
        "<strong>双语对照网页：</strong> <kbd>Alt+A</kbd> 开关按视口流式翻译的双语网页（也可右键页面）· 悬浮条可切换 <code>双语/仅译</code>、<code>2B/9B</code>，并一键还原页面。",
      "shortcuts.study":
        "<strong>划词与学习卡片：</strong> <kbd>S</kbd> 学习卡片 · <kbd>Space</kbd> / <kbd>Shift+Space</kbd> 朗读 1x / 0.7x · <kbd>E</kbd> 朗读例句 · <kbd>F</kbd> 自测翻转 · <kbd>D</kbd> 精简/详尽 · <kbd>M</kbd> 9B/35B 模型 · <kbd>C</kbd> 复制 · <kbd>1-4</kbd> / <kbd>←</kbd><kbd>→</kbd> 切换单词 · <kbd>Ctrl+滚轮</kbd> 文字大小 · <kbd>Esc</kbd> 关闭"
    }
  };

  // The browser's own interface language, or "" where there is no extension API (unit tests, the sandbox).
  function browserLanguage() {
    try {
      if (typeof chrome !== "undefined" && chrome.i18n && typeof chrome.i18n.getUILanguage === "function") {
        return String(chrome.i18n.getUILanguage() || "");
      }
    } catch (_) {}
    return "";
  }

  // setting: "auto" (follow the browser), "en" or "zh-CN". Any Chinese browser locale gets zh-CN;
  // everything else, including an unknown setting, gets English.
  function resolveLang(setting, browserLang) {
    const s = String(setting || "").trim();
    if (LANGS.includes(s)) return s;
    const b = String(browserLang === undefined ? browserLanguage() : browserLang || "");
    return /^zh\b/i.test(b) ? "zh-CN" : "en";
  }

  function translate(lang, key, ...args) {
    const table = MESSAGES[lang] || MESSAGES.en;
    const template = Object.prototype.hasOwnProperty.call(table, key) ? table[key] : MESSAGES.en[key];
    if (template === undefined) return key;
    return template.replace(/\{(\d+)\}/g, (m, i) => (Number(i) < args.length ? String(args[Number(i)]) : m));
  }

  const api = { LANGS, MESSAGES, PAGE_TEXT, browserLanguage, resolveLang, translate };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.BiliI18n = api;
})(typeof globalThis !== "undefined" ? globalThis : this);
