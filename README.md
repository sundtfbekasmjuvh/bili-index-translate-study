# Bili Index Translate · 极简流式划词 + Zero-OCR 多模态识图框选 + Alt+A 双语对照 + instTrans 术语表 + 伴随语言学习卡片

基于 Bilibili OpenAI 兼容多模态与文本翻译接口（`Index-Translate-35B-A3B` / `9B` / `2B`）构建的**双平台极简翻译与按需语言学习私教**：
- **网页端（Chrome / Edge MV3）**：无需本地 Python 代理、无需第三方 OCR 或中转，通过 MV3 `declarativeNetRequest` 原生直连 B 站接口；同时支持 **划词流式翻译**、**`Alt+S` 零 OCR 框选图片/漫画/视频字幕帧直译 + 生词卡闭环**、**图片右键一键直译**、**`Alt+A` 视口流式双语对照全页**、**官方 `instTrans` 动态术语表/免译锁定** 与 **按 `S` 并排展开零留痕词典级学习卡片**。
- **Windows 桌面端（.NET 8 WPF + UIA + Per-Monitor DPI 识图框选）**：跨任意桌面应用的非抢焦点全局划词气泡 + **`Alt+Shift+S` 全屏跨显示器框选识图直译（支持游戏 HUD、扫描版 PDF、视频硬字幕与剪贴板截图）** + `instTrans` 自定义术语表 + 伴随语言学习卡片。

> **开箱即用下载**：可直接前往右侧 [**Releases**](https://github.com/sundtfbekasmjuvh/bili-index-translate-study/releases) 下载打包好的 Chrome/Edge 扩展 ZIP 与 Windows 桌面版可执行程序。

---

## 目录结构与双端概览

| 目录 | 说明 | 状态 |
|---|---|---|
| [`bili-index-translate/`](bili-index-translate/) | **Chrome / Edge / Brave / Arc 浏览器扩展（Manifest V3）**：MV3 原生直连 + `Alt+S` 零 OCR 识图框选/右键图片直译 + `Alt+A` 视口流式双语对照 + `instTrans` 动态术语表 + 划词与伴随语言学习卡片 | 可直接加载未打包扩展或下载 Release ZIP |
| [`BiliIndexTranslate.Desktop/`](BiliIndexTranslate.Desktop/) | **Windows 桌面全局划词与屏幕识图端（C# / .NET 8, WPF + UI Automation）**：跨应用非抢焦点浮窗翻译 + `Alt+Shift+S` 跨屏冻结框选识图直译/剪贴板截图直译 + `instTrans` 术语表 + 伴随学习卡片 | Windows 上 `0 warnings` 编译通过，`225/225` xUnit 核心单测通过（见 [`BiliIndexTranslate.Desktop/README.md`](BiliIndexTranslate.Desktop/README.md)） |
| [`bili-study-sandbox/`](bili-study-sandbox/) | **零依赖本地浏览器预览沙盒（Node.js 标准库）**：无需重载扩展即可在本地 `http://localhost:3001` 实时体验划词、学习卡片、`Alt+S` 画布框选识图、术语表与 `Alt+A` 双语对照 | 运行 `node bili-study-sandbox/server.js` 即可启动，默认只监听 `127.0.0.1` |

---

## 核心特性（v1.4.1 多模态全能旗舰版）

### 1. Zero-OCR 多模态识图框选直译 + 学习卡片全闭环（Chrome `Alt+S` / Windows `Alt+Shift+S`）
- **突破不可选中像素边界**：无需外接任何第三方 OCR 引擎，直接调用 `Index-Translate-35B-A3B` 旗舰 MoE 视觉多模态能力，一气呵成读懂**生肉漫画气泡、论文扫描图、架构示意图、游戏外语 UI 以及 YouTube/B 站暂停视频硬字幕帧**。35B-A3B 首字比 2B/9B 慢，但译文和回填原文最完整（2B 不回填原文，2B 和 9B 在日文与多行样例中会重复句子），实测数据见下方「识图速度实测」。
- **`〖译文〗` → `〖原文〗` 双轨流式提取，无缝打通词典级学习卡片**：译文优先逐字流式上屏，紧接着自动回填图片中的原始外语文本——原本无法选中的图片像素即刻点亮 **发音朗读** 与 **`S` 键伴随语言学习卡片**（音标/假名、词性、辨析、助记与双语例句一键拆解），并同步遵守用户自定义**术语表**！
- **100% 纯内存裁剪，零临时文件落盘**：网页端基于 `<canvas>` 内存裁剪、Windows 端基于 `MemoryStream` 物理像素冻结截图裁剪（同时支持托盘菜单一键翻译 `Win+Shift+S` 剪贴板截图），绝不在磁盘写入任何截图缓存。

### 2. 免本地代理 `Alt+A` 视口流式双语对照（Chrome 扩展）
- **免 Python 网桥原生直连**：在浏览器网络层自动剥离扩展 `Origin` 头并注入 `enable_thinking: false`，装好扩展即刻直连，无本地回环代理冲突。
- **`IntersectionObserver` 视口优先 + `〖P0〗` 锚点合并 SSE 流式上屏**：按下 `Alt+A` 仅优先翻译当前屏幕可见及预读区段落，滚动加载自动续翻；右下角微透胶囊一键切换 **`双语` ↔ `仅译`**、**`2B` ↔ `9B`**，再次按 `Alt+A` 100% 无损还原原网页。

### 3. 官方 `instTrans` 动态命中术语表 & 伴随语言学习卡片（双端通用）
- **强化学习原生槽位对齐**：支持每行一条 `Source => Target` 指定译名，或单写一行 `Flash-Attention` 表示**免译原样锁定**；文本划词按需零冗余注入，多模态识图同步注入 `【术语对照】`。
- **双速语音跟读 + 自测遮罩 + 严格零留痕**：词头与例句支持 `1x` / `0.7x` 慢速跟读，支持 `M` 键一键切换 `9B` ↔ `35B-A3B` 旗舰精析，纯内存 LRU 秒开（`⚡瞬时`），零历史落盘。

---

## 快捷键一览

| 快捷键 | 平台 | 功能 |
|---|---|---|
| `Alt + S` | Chrome / Edge | **零 OCR 网页/视频帧框选识图直译**（亦可直接右键点击任意图片翻译） |
| `Alt + Shift + S` | Windows 桌面端 | **零 OCR 跨显示器屏幕冻结框选识图直译**（托盘亦支持剪贴板截图直译） |
| `Alt + A` | Chrome / Edge | 开启 / 退出全页视口流式「双语对照阅读」（100% 无损还原） |
| `Alt + Q` | Windows 桌面端 | 按需兜底划词翻译快捷键（UIA 不可读控件自动单次复制翻译） |
| `S` | Chrome 端浮窗内 | 展开 / 收起伴随「语言学习卡片」（划词或识图完成后均可一键唤起） |
| `Space` / `Shift + Space` | 学习卡片内 | 常速（`1x`）/ 慢速（`0.7x`）朗读当前词头 |
| `E` / `Shift + E` | 学习卡片内 | 常速（`1x`）/ 慢速（`0.7x`）朗读双语例句原文 |
| `D` / `F` / `M` / `C` | 学习卡片内 | 切换 `详尽/精简` / `自测翻转` / `9B↔35B` 模型 / 复制格式化词卡笔记 |
| `1` – `4` 或 `←` / `→` | 学习卡片内 | 在长句或识图提取的多张重点词卡之间快速切换 |
| `Ctrl + 鼠标滚轮` / `Esc` | 双端通用 | 动态缩放浮窗字号 / 关闭浮窗并停止语音朗读 |

---

## 快速安装指南

1. **Chrome / Edge 浏览器扩展**：下载 Release 中的 `bili-index-translate-chrome-v1.4.1.zip` 解压 → 打开 `chrome://extensions` 开启右上角**开发者模式** → **加载已解压的扩展程序** 选择 `bili-index-translate/` 目录。
2. **Windows 桌面端（.NET 8 WPF）**：下载 Release 中的 `BiliIndexTranslate-Desktop-win-x64-selfcontained-v1.4.1.zip`（自带运行时单文件免安装版）或 `BiliIndexTranslate-Desktop-win-x64-v1.4.1.zip`（轻量版），解压运行 `BiliIndexTranslate.exe`。
3. **本地预览沙盒**：运行 `node bili-study-sandbox/server.js` 并访问 `http://localhost:3001`。
   沙盒默认只监听本机 `127.0.0.1`；确需局域网访问时设置环境变量 `HOST=0.0.0.0`，端口用 `PORT` 修改。

---

## 接口格式（OpenAI 兼容 / DeepLX / 自定义模板）

扩展选项页和桌面端 Settings → Service 里的「API format」决定请求格式，默认是 OpenAI 兼容，已有设置不受影响。

| 接口格式 | Base URL 填写 | 鉴权 | 可用功能 |
|---|---|---|---|
| OpenAI-compatible | 接口根地址，如 `https://index-translate.bilibili.com/v1`，自动追加 `/chat/completions` | `Authorization: Bearer <API Key>` | 全部 |
| DeepLX-compatible | 完整的翻译地址，原样使用，如 `https://host/translate?token={apiKey}` 或 `http://localhost:1188/translate` | 地址里的 `{apiKey}` 会替换成 API Key；没有占位符时用 `Authorization: Bearer <API Key>`，Key 留空则不带鉴权头 | 划词翻译，以及扩展的 `Alt+A` 双语对照 |
| Custom JSON API | 完整地址，可以带占位符，如 `https://host/translate?key={apiKey}` | 由模板决定：写在地址、请求头或请求体里 | 划词翻译，以及扩展的 `Alt+A` 双语对照 |

- DeepLX 格式的请求体是 `{"text": 原文, "source_lang": "auto", "target_lang": 语言代码}`，译文取响应里的 `data`；HTTP 错误和响应里非 200 的 `code` 都会显示出来。
- 这种接口没有提示词，所以术语表不生效；学习卡片和识图直译会直接提示需要 OpenAI 兼容接口，不发请求；模型选项对它无效，扩展选项页选中 DeepLX 后这些控件会置灰，译文面板也不再提示词卡。
- 扩展的 Target language 会映射成 DeepL 语言代码（`中文` → `ZH`，`English` → `EN`，`日本語` → `JA`，以及 DeepL 支持的三十多种语言的中英文名称）；地区变体直接填代码，如 `PT-BR`。桌面端按所选目标语言映射：简体中文 → `ZH`，繁體中文 → `ZH-HANT`，English → `EN`，日本語 → `JA`，한국어 → `KO`。
- `Alt+A` 把同一批的单行段落合并成一次请求（每段一行，译文按行拆回，一批最多 4 段），请求数明显少于段落数；接口的回复行数对不上时改为逐段请求，并在设置变更前不再对该接口合并。已译段落走内存缓存。扩展填了 Relay prefix、或桌面端勾选了中继时，中继地址必须是经过中继后的完整翻译地址。
- 接口格式和地址明显不匹配时会拦下来：OpenAI 格式不填充占位符，地址里带 `{apiKey}` 会拒绝保存（扩展和桌面端都是，否则 key 会被悄悄丢掉）；地址形似另一种格式时，扩展在保存后给出提示。
- 桌面端把这项设置存在 `settings.json` 的 `apiFormat` 字段（`OpenAi` / `Deeplx` / `Custom`）；API key 仍用 DPAPI 加密保存，替换进地址的 key 不会写入日志，也不进入缓存键。

### 自定义接口模板（Custom JSON API）

接口不是上面两种格式时，不用改源码：在「API format」里选 Custom JSON API，用模板描述请求，再用一个路径指出译文在 JSON 响应里的位置。扩展和桌面端的字段相同。

| 字段 | 说明 |
|---|---|
| Base URL / Endpoint | 完整地址，可以带占位符 |
| Method | `POST` 或 `GET`（`GET` 不发请求体，原文放在地址里） |
| Request headers | 每行一条 `Name: value`，如 `Authorization: DeepL-Auth-Key {apiKey}` |
| Request body | 请求体模板，默认按 `application/json` 发送；请求头里写了别的 `Content-Type` 就按它发 |
| Result path | 译文在响应里的位置：`data`、`translations[0].text`、`translations[*].text` |
| Error path | 可选，接口放错误信息的位置，如 `error.message` |

占位符：`{text}` 原文；`{texts}` 一次请求里的全部原文（JSON 数组，只用在请求体里）；`{apiKey}`；`{target}` 目标语言（扩展里是你填写的原样，桌面端是 `zh-Hans` 这类代码）；`{targetLang}` / `{targetLangLower}` 是它对应的代码，如 `ZH` / `zh`。

- 占位符按所在位置转义：地址和表单请求体里做 URL 编码，JSON 请求体里做 JSON 转义（`"{text}"` 写在引号里，`{texts}` 不加引号），请求头里去掉换行。
- 请求体里用了 `{texts}` 时，扩展的 `Alt+A` 把一批段落放进一次请求，结果路径要返回同样数量的译文（用 `[*]`，或指向一个字符串数组）；数量对不上时改为逐段请求。只用 `{text}` 时每段请求一次。
- 内置预设：DeepL 官方 API（Free / Pro）和 LibreTranslate，请求与响应格式取自各自的官方文档。预设只填表单，保存前可以改；这两个预设只在本地模拟服务上测过，没有连过真实服务。
- 模板不能工作时（没有结果路径、请求头格式不对、没有发送原文、填完占位符后请求体不是合法 JSON）在保存时就报错，不会等到第一次翻译。
- 和 DeepLX 一样，这种接口只做纯文本翻译：术语表不生效，学习卡片和识图直译会提示需要 OpenAI 兼容接口。

---

## 网络与隐私

- 扩展默认直连 `index-translate.bilibili.com`。官方接口对带扩展 `Origin` 头的请求返回 HTTP 412，扩展用 `dnr_rules.json` 在网络层去掉该头。
- 中继（Relay prefix）只在选项页手动填写后使用。直连返回 412 时只显示错误和提示，不会自动改走任何第三方中继。中继会收到选中的文本、图片和 API key，只填写你信任的地址。
- 桌面端同理：中继需要在设置里手动开启，应用不会自行切换线路。

---

## 识图速度实测

2026-11-05 在 Windows 11 上用 Node 直连公共接口测得，每个模型每张图 5 次，取中位数。走的是扩展的识图提示词，流式输出，`max_tokens` 为 1024。四个时间点都从发出请求算起：

- **首字**：收到第一个非空增量。
- **开始显示**：解析出第一段可显示的译文，面板从这时开始逐字上屏。
- **译文完成**：`〖原文〗` 标记到达，此时译文已经完整，后面只剩原文转写。模型不输出 `〖原文〗` 时等于全部完成。
- **全部完成**：流结束。此后才回填原文，才能用 `S` 打开学习卡片。

| 样例图片 | 模型 | 首字 | 开始显示 | 译文完成 | 全部完成 | 回填原文 |
|---|---|---|---|---|---|---|
| 720×160，两行英文 | 2B | 127 ms | 127 ms | 186 ms | 186 ms | 无 |
| | 9B | 206 ms | 222 ms | 523 ms | 775 ms | 有 |
| | 35B-A3B | 284 ms | 335 ms | 617 ms | 835 ms | 有 |
| 720×160，两行日文 | 2B | 102 ms | 102 ms | 232 ms | 232 ms | 无 |
| | 9B | 197 ms | 213 ms | 655 ms | 1168 ms | 有 |
| | 35B-A3B | 309 ms | 367 ms | 673 ms | 964 ms | 有 |
| 1280×720，7 行英文 | 2B | 147 ms | 147 ms | 705 ms | 705 ms | 无 |
| | 9B | 229 ms | 297 ms | 3135 ms | 5467 ms | 有 |
| | 35B-A3B | 475 ms | 526 ms | 1875 ms | 3137 ms | 有 |

- 35B-A3B 的译文在全部完成前 200–1300 ms 就已完整，约占总耗时的 60–75%，剩下的时间用于转写原文。提示词让模型先输出 `〖译文〗` 再输出 `〖原文〗`，面板边收边显示，所以用户读到完整译文的时间按「译文完成」计算。
- 质量：2B 从不回填原文，识图后无法唤起学习卡片，并且在日文和 7 行样例里重复了句子；9B 在这两个样例里也重复了句子（译文和原文都有）；35B-A3B 三个样例的译文和原文都完整，所以扩展和桌面端默认用它识图。
- 耗时随服务端负载变化明显。同一天早些时候测的 3 次中位数里，1280×720 样例的全部完成时间是 2B 1102 ms、9B 3419 ms、35B-A3B 7788 ms，9B 和 35B-A3B 的先后与上表相反。只有「2B 最快但不回填原文」和「35B-A3B 输出最完整」在两次测量里一致。
- `Alt+A` 双语对照用 5 段英文测得（同日早些时候，3 次中位数）：2B 完成 1087 ms，9B 为 1872 ms。

---

## 开发与测试

| 检查 | 命令 |
|---|---|
| 扩展与沙盒单测（Node 22+，零依赖） | `node --test "tests/**/*.test.js"` 或 `npm test` |
| 扩展端到端测试（Playwright + Chromium） | `npm ci`，`npx playwright install chromium`，然后 `npm run test:e2e` |
| JS 语法检查 | `node --check bili-index-translate/background.js`（其余脚本同理） |
| 桌面端构建 | `dotnet build BiliIndexTranslate.Desktop/BiliIndexTranslate.sln -c Release` |
| 桌面端单测 | `dotnet test BiliIndexTranslate.Desktop/tests/BiliIndexTranslate.Core.Tests -c Release` |

- `tests/extension/` 覆盖术语表解析、提示词构造、识图双轨解析、段落锚点、学习卡 JSON 修复与流式解析，四条流式请求路径的分块、取消和 JSON 回退，并用模拟的 `fetch` 验证 412 时只发一次请求、不自动改走中继；`deeplx.test.js` 和 `custom.test.js` 覆盖 DeepLX 格式与自定义模板的请求构造、占位符转义、响应解析和各条功能路径。
- `tests/e2e/` 在无头 Chromium 里加载未打包的扩展，后端换成 `tests/support/mock-upstream.js`（本地 OpenAI 兼容模拟服务，按提示词返回确定的译文、词卡和识图结果）。用例覆盖划词流式与取消、术语表注入、412 与显式中继、学习卡片快捷键、`Alt+A` 双语对照（视口优先、跳过中文和代码、滚动续翻、仅译与模型切换、无损还原）、`Alt+S` 框选裁剪、右键图片、选项页、DeepLX 接口格式（模拟服务的 `/translate` 端点）和自定义模板（模拟服务里一个形状自拟的 `/custom-api` 端点）。每个用例结束时检查没有请求离开 `127.0.0.1`。右键菜单和扩展快捷键无法在 Playwright 里直接点击，用例在 service worker 里派发与 Chrome 相同的事件。
- GitHub Actions（`.github/workflows/ci.yml`）在每次推送和 PR 上运行以上全部检查；端到端测试失败时上传 Playwright 报告和 trace，桌面端在 `windows-latest` 上构建、测试，并试打一次发布包。Dependabot（`.github/dependabot.yml`）每周检查 Actions、npm 和 NuGet 依赖。
- 发布：先把 `bili-index-translate/manifest.json` 的 `version` 和 `BiliIndexTranslate.Desktop/Directory.Build.props` 的 `<Version>` 改成同一个版本号并合入 `main`，再推送 `vX.Y.Z` tag。`.github/workflows/release.yml` 会跑全部单测，用 `.github/scripts/package-release.ps1` 打出 3 个 ZIP 和 `SHA256SUMS.txt`，建成草稿 Release，核对说明后手动发布。tag 与两处版本号不一致或工作区有未提交改动时脚本直接失败。本地预演：`pwsh .github/scripts/package-release.ps1 -OutDir <空目录>`。
