<img src="bili-index-translate/icons/icon128.png" width="72" height="72" alt="Bili Index Translate 图标">

# Bili Index Translate · 极简流式划词 + Zero-OCR 多模态识图框选 + Alt+A 双语对照 + instTrans 术语表 + 伴随语言学习卡片

基于 Bilibili OpenAI 兼容多模态与文本翻译接口（`Index-Translate-35B-A3B` / `9B` / `2B`）构建的**双平台极简翻译与按需语言学习私教**：
- **网页端（Chrome / Edge MV3）**：无需本地 Python 代理、无需第三方 OCR 或中转，通过 MV3 `declarativeNetRequest` 原生直连 B 站接口；同时支持 **划词流式翻译**、**`Alt+S` 零 OCR 框选图片/漫画/视频字幕帧直译 + 生词卡闭环**、**图片右键一键直译**、**`Alt+A` 视口流式双语对照全页**、**官方 `instTrans` 动态术语表/免译锁定** 与 **按 `S` 并排展开零留痕词典级学习卡片**。
- **Windows 桌面端（.NET 10 WPF + UIA + Per-Monitor DPI 识图框选）**：跨任意桌面应用的非抢焦点全局划词气泡 + **`Alt+Shift+S` 全屏跨显示器框选识图直译（支持游戏 HUD、扫描版 PDF、视频硬字幕与剪贴板截图）** + `instTrans` 自定义术语表 + 伴随语言学习卡片；DeepLX 和自定义接口的框选识图改用 Windows 自带的离线 OCR 先识别文字再翻译（截图不上传），并新增托盘“识别屏幕文字并复制”。

> **开箱即用下载**：可直接前往右侧 [**Releases**](https://github.com/sundtfbekasmjuvh/bili-index-translate-study/releases) 下载打包好的 Chrome/Edge 扩展 ZIP 与 Windows 桌面版（安装程序 `Setup.exe` 或免安装 ZIP）。

---

## 目录结构与双端概览

| 目录 | 说明 | 状态 |
|---|---|---|
| [`bili-index-translate/`](bili-index-translate/) | **Chrome / Edge / Brave / Arc 浏览器扩展（Manifest V3）**：MV3 原生直连 + `Alt+S` 零 OCR 识图框选/右键图片直译 + `Alt+A` 视口流式双语对照 + `instTrans` 动态术语表 + 划词与伴随语言学习卡片 | 可直接加载未打包扩展或下载 Release ZIP |
| [`BiliIndexTranslate.Desktop/`](BiliIndexTranslate.Desktop/) | **Windows 桌面全局划词与屏幕识图端（C# / .NET 10, WPF + UI Automation）**：跨应用非抢焦点浮窗翻译 + `Alt+Shift+S` 跨屏冻结框选识图直译/剪贴板截图直译 + `instTrans` 术语表 + 伴随学习卡片 | `0 warnings` 编译通过，`263/263` xUnit 核心单测通过（见 [`BiliIndexTranslate.Desktop/README.md`](BiliIndexTranslate.Desktop/README.md)） |
| [`bili-study-sandbox/`](bili-study-sandbox/) | **零依赖本地浏览器预览沙盒（Node.js 标准库）**：无需重载扩展即可在本地 `http://localhost:3001` 实时体验划词、学习卡片、`Alt+S` 画布框选识图、术语表与 `Alt+A` 双语对照 | 运行 `node bili-study-sandbox/server.js` 即可启动，默认只监听 `127.0.0.1` |

---

## 核心特性（v1.5.0 多模态全能旗舰版）

### 1. Zero-OCR 多模态识图框选直译 + 学习卡片全闭环（Chrome `Alt+S` / Windows `Alt+Shift+S`）
- **突破不可选中像素边界**：无需外接任何第三方 OCR 引擎，直接调用 `Index-Translate-35B-A3B` 旗舰 MoE 视觉多模态能力，一气呵成读懂**生肉漫画气泡、论文扫描图、架构示意图、游戏外语 UI 以及 YouTube/B 站暂停视频硬字幕帧**。35B-A3B 首字比 2B/9B 慢，但译文和回填原文最完整（2B 不回填原文，2B 和 9B 在日文与多行样例中会重复句子），实测数据见下方「识图速度实测」。
- **`〖译文〗` → `〖原文〗` 双轨流式提取，无缝打通词典级学习卡片**：译文优先逐字流式上屏，紧接着自动回填图片中的原始外语文本——原本无法选中的图片像素即刻点亮 **发音朗读** 与 **`S` 键伴随语言学习卡片**（音标/假名、词性、辨析、助记与双语例句一键拆解），并同步遵守用户自定义**术语表**！
- **100% 纯内存裁剪，零临时文件落盘**：网页端基于 `<canvas>` 内存裁剪、Windows 端基于 `MemoryStream` 物理像素冻结截图裁剪（同时支持托盘菜单一键翻译 `Win+Shift+S` 剪贴板截图），绝不在磁盘写入任何截图缓存。

### 2. 免本地代理 `Alt+A` 视口流式双语对照（Chrome 扩展）
- **免 Python 网桥原生直连**：在浏览器网络层自动剥离扩展 `Origin` 头并注入 `enable_thinking: false`，装好扩展即刻直连，无本地回环代理冲突。
- **`IntersectionObserver` 视口优先 + `〖P0〗` 锚点合并 SSE 流式上屏**：按下 `Alt+A` 仅优先翻译当前屏幕可见及预读区段落，滚动加载自动续翻；右下角微透胶囊一键切换 **`双语` ↔ `仅译`**、**`2B` ↔ `9B`**，再次按 `Alt+A` 100% 无损还原原网页。
- **遇到限流会自动重试**（划词、识图、学习卡片和整页翻译都适用）：上游返回 429 或 500/502/503/504 时，同一线路最多再试 2 次（等待服从 `Retry-After`，要求等待超过 8 秒则不重试）；网络错误、超时和取消不重试。
- **跟随页面变化**：页面新增的段落会被扫描并翻译（页面一直在变动时最迟约 2 秒扫描一次）；页面改了已翻译段落的文字，会按新原文重译；页面用同样的文字重建段落而抹掉译文时，把译文放回去（同一段最多 3 次）；页面删掉的段落不再计入进度。
- **只注释生词（可选，默认关闭）**：不想整段翻译、只想读原文时，在选项页「只注释生词」一节勾选并保存，再按 `Alt+A`，浮动条上会多一个「注释 ↔ 翻译」按钮。注释模式下，只给超出你水平线的词加一行小标签（「词 · 释义」），显示在段落下方，不改动原文。
  - **标“会了”和撤销**：点一个标签，再点出现的「会了」，这个词在本页的标签都会消失，浮动条上显示「已标记 X」和「撤销」（约 6 秒内有效）。之后想撤销，到选项页的「已存的词」里逐个删除，或一键清空。
  - **水平线**：中文按 HSK 等级（HSK 1 到 6 级，外加 7-9 级），英文、西班牙文按词频排名（最常用的前 1000 / 2000 / 3000 / 5000 / 10000 / 20000 个词），水平线以内的词视为认识。
  - **支持的语言**：中文、英文、西班牙文页面；日文和其他语言的页面照常整段翻译。目标语言只能是中文或 English，释义用对应的语言。
  - **限制**：DeepLX 和自定义接口不支持，浮动条上不显示这个按钮。非官方的 OpenAI 兼容地址可以用，但效果不保证，模型回复的格式不对时，这一段退回整段翻译。目标语言是中文时，中文段落本来就不翻译，所以中文注释对目标语言是 English 的用户才有用。整页同一个词只注第一次，每段最多注 8 个词。

### 3. 官方 `instTrans` 动态命中术语表 & 伴随语言学习卡片（双端通用）
- **强化学习原生槽位对齐**：支持每行一条 `Source => Target` 指定译名，或单写一行 `Flash-Attention` 表示**免译原样锁定**；文本划词按需零冗余注入，多模态识图同步注入 `【术语对照】`。
- **双速语音跟读 + 自测遮罩 + 严格零留痕**：词头与例句支持 `1x` / `0.7x` 慢速跟读，支持 `M` 键一键切换 `9B` ↔ `35B-A3B` 旗舰精析，纯内存 LRU 秒开（`⚡瞬时`），零历史落盘。
- **无障碍**：面板正文用 `aria-live` 播报、完成时才读一遍，打开时不抢焦点、关闭时还焦点，按钮有可见的键盘焦点轮廓，并遵循系统“减少动态效果”设置。

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

1. **Chrome / Edge 浏览器扩展**：下载 Release 中的 `bili-index-translate-chrome-v1.5.0.zip` 解压 → 打开 `chrome://extensions` 开启右上角**开发者模式** → **加载已解压的扩展程序** 选择 `bili-index-translate/` 目录。
2. **Windows 桌面端（.NET 10 WPF）**，两种方式任选其一：
   - **安装程序（推荐）**：下载 Release 中的 `BiliIndexTranslate.Desktop-win-Setup.exe` 并运行。只为当前用户安装（不需要管理员权限），自带运行时，会创建开始菜单和桌面快捷方式，并且能自动更新（设置里可关闭自动检查，也可以随时点“检查更新”）。可在 Windows 设置的“应用”里卸载。安装版会定期访问 GitHub（`api.github.com` 和 Releases 的下载地址）检查并下载更新，请求里不带任何个人信息或使用数据。
   - **免安装 ZIP**：下载 `BiliIndexTranslate-Desktop-win-x64-selfcontained-v1.5.0.zip`（自带运行时单文件版）或 `BiliIndexTranslate-Desktop-win-x64-v1.5.0.zip`（轻量版），解压运行 `BiliIndexTranslate.exe`。免安装版不会检查更新，请从 GitHub 下载新版本。
3. **本地预览沙盒**：运行 `node bili-study-sandbox/server.js` 并访问 `http://localhost:3001`。
   沙盒默认只监听本机 `127.0.0.1`，并且只应答用本机名字（`localhost`、`127.0.0.1`）寻址、由沙盒页面自己发出的请求；确需局域网访问时设置环境变量 `HOST=0.0.0.0`（此时不做这项检查；注意局域网里任何人都能通过它使用服务器上配置的 key，启动时会打印警告），端口用 `PORT` 修改。

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

## 界面语言

- 扩展界面有英文和简体中文两种：选项页、翻译面板、学习卡片、双语对照条、右键菜单，以及扩展自己产生的错误提示。在选项页的 `Interface language`（界面语言）里选择，默认「自动」：浏览器界面是中文（任何地区）时显示简体中文，其余显示英文。
- 在选项页改选语言会立即预览，保存后才对其他界面生效；已打开的网页不需要刷新。
- 扩展管理页里的描述、快捷键说明和工具栏按钮提示由浏览器按自身语言从 `_locales/` 读取，不受该设置影响。
- 模型返回的译文和词卡内容、服务端返回的错误原文不翻译。文案集中在 `bili-index-translate/i18n.js`。
- 桌面端同样有英文和简体中文：设置 → Reading（阅读）→ `Interface language`（界面语言），默认跟随 Windows 显示语言。保存后已打开的窗口和托盘菜单立即切换，不需要重启。翻译核心给出的错误详情和诊断日志保持英文。文案集中在 `BiliIndexTranslate.Core/Localization/UiText.cs`。

---

## 网络与隐私

- 扩展默认直连 `index-translate.bilibili.com`。官方接口对带扩展 `Origin` 头的请求返回 HTTP 412，后台在运行时登记一条 `declarativeNetRequest` 动态规则，在网络层去掉该头并把客户端名设为 `Index-Translate-Client/1.0`。规则的发起方限定为本扩展，网页自己发往同一域名的请求不会被改动（1.5.0 之前规则写在 `dnr_rules.json` 里，不区分发起方）。
- 页面离开时清空内存缓存。只有页面本身离开才清空，页面里的框架（广告、嵌入内容）卸载不会清空。
- 中继（Relay prefix）只在选项页手动填写后使用。直连返回 412 时只显示错误和提示，不会自动改走任何第三方中继。中继会收到选中的文本、图片和 API key，只填写你信任的地址。
- 桌面端同理：中继需要在设置里手动开启，应用不会自行切换线路。
- 扩展的 API key 只存在本机的 `chrome.storage.local`，不随浏览器账号同步；其余设置仍在 `chrome.storage.sync`。1.4.1 及更早版本把 key 存在同步存储里，升级后第一次读取设置时会把它移到本机并删除同步副本。其他设备上的旧版本因此读不到同步的 key，需要在那台设备上重新填写一次。
- 右键图片翻译最多下载 8 MB：响应头声明的大小超限时不读取响应体，没有声明时边下载边计数，超限即中断；下载超过 35 秒按超时处理。
- 「只注释生词」是对“零历史”说法的一个例外（扩展其余部分不保存翻译内容）：
  - 默认关闭。开启后，你标为“会了”的词存在本机的 `chrome.storage.local`，内容只有词本身，不存句子、网址和时间；不同步、不上传。
  - 可以随时在选项页清空，也可以逐个删除。
  - 关闭后，翻译页面时不再读写这些词；已存的词会保留，选项页仍可查看、逐个删除或清空。「恢复默认」只关开关，同样不清词表。
  - 开启后，被挑出的生词所在的段落原文，会像整段翻译一样发往你配置的接口。
- 后台按消息类型校验发送方：检查 API 配置、迁移设置只接受本扩展的设置页，划词/识图等只接受标签页里的页面脚本，翻译两者皆可；五个流式端口（划词、学习卡片、识图、双语对照、注释）只接受标签页里的页面脚本。注释相关的消息里，标记（`BILI_ANNOTATE_MARK`）只接受标签页，删除、清空、列表（`BILI_ANNOTATE_REMOVE` / `BILI_ANNOTATE_CLEAR` / `BILI_ANNOTATE_LIST`）只接受设置页。来源不符的请求直接拒绝，不执行任何操作。

---

## 词表数据来源

「只注释生词」用的词表放在 `bili-index-translate/data/`，随扩展打包，不联网下载：

- 英文、西班牙文的词频排名来自 `wordfreq` 3.1.1，作者 Robyn Speer，数据许可 CC BY-SA 4.0（要求署名，衍生数据以相同许可共享）。
- 中文的 HSK 等级来自 HSK 3.0 词表，取自 [ivankra/hsk30](https://github.com/ivankra/hsk30)，该仓库声明的许可是 MIT；官方原文 GF 0025-2021 的版权状态未确认，MIT 标注不能证明官方授权。
- 许可、来源提交和处理方式见 [`bili-index-translate/data/LICENSE-DATA.md`](bili-index-translate/data/LICENSE-DATA.md)。
- 重新生成词表：运行 [`tools/build-wordlists.py`](tools/build-wordlists.py)。

## 许可证

本项目采用 [Apache License 2.0](LICENSE)，版权声明见 [NOTICE](NOTICE)。浏览器扩展、Windows 桌面端和本地预览沙盒的项目代码均适用此许可证。以下内容不在此列，仍遵循各自的许可证：

- `bili-index-translate/data/` 里的词表数据，见 [`LICENSE-DATA.md`](bili-index-translate/data/LICENSE-DATA.md)（其中词频数据为 CC BY-SA 4.0）。
- 第三方依赖。

此许可证不授予哔哩哔哩等第三方的商标权，也不改变上游翻译服务的使用条款。

之后的发布包都附带 LICENSE 和 NOTICE：扩展包放在 `bili-index-translate/` 里，桌面端 zip 放在可执行文件旁，安装版放在安装目录里。v1.5.0 及更早的已发布资产保持原样。

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

## 页面开销实测

2026-11-15 在一台 Intel N100 的 Linux 机器上用无头 Chrome for Testing 156 测得，每项 3 次取中位数。上游是本地模拟服务，每 1 ms 发一个 2 字符的增量（比真实模型密，用来放大每条增量的成本）。数字取自 Chrome 的 `Performance.getMetrics`：主线程时间是网页主线程忙碌的总时间，包含脚本、样式、重排和绘制。「改动前」是 1.5.0 分支上合并流式更新之前的代码。

| 场景 | | 主线程时间 | 重排次数 |
|---|---|---|---|
| 划词，3000 字符的译文分 1500 条增量到达 | 改动前 | 1906 ms | 1503 |
| | 改动后 | 154 ms | 66 |
| `Alt+A`，40 段的页面，首屏 9 段流式翻译 | 改动前 | 471 ms | 114 |
| | 改动后 | 133 ms | 64 |
| `Alt+A`，6000 段的页面，首屏 9 段流式翻译 | 改动前 | 1923 ms | 82 |
| | 改动后 | 813 ms | 54 |
| `Alt+A` 开着，在 6000 段的页面上滚动 90 帧（不开时 55 ms） | 改动前 | 504 ms | 55 |
| | 改动后 | 164 ms | 55 |

- 划词那一项从发出请求到译文显示完整，改动前 2433 ms，改动后 1867 ms：改动前主线程约八成时间在忙，成了瓶颈。
- 做法：后台把进度消息合并发送，第一条立即发，之后每 33 ms 最多一条，只带最新状态，`Alt+A` 只发有变化的段落；页面每个动画帧最多重画一次，文字没变的段落不动。
- 6000 段的页面上，浏览器每帧都要重新检查所有被监听的段落。扩展最多监听离视口最近的 400 段，滚动后重新挑选；等待中的段落不超过 400 时全部监听，行为与之前相同。
- 6000 段页面上剩下的开销主要来自译文撑高段落后，其后所有内容的重排和重绘。
### 空闲时的固定开销（用户什么都没做）

与不加载扩展相比的差值，7 轮的中位数，同一台 Intel N100。

| 场景 | 主线程 | 脚本执行 | JS 内存 | 事件监听 |
|---|---|---|---|---|
| 普通网页（没有 iframe） | 差值小于测量波动 | +1.7 ms | +268 KB | 5 个 |
| 30 个同源 iframe 的页面 | +147 ms | +24 ms | +6621 KB（约 6.6 MB） | 155 个 |

- 把主脚本换成空文件后，30 个 iframe 的页面仍然多出 76 ms 和 5655 KB，所以大部分是浏览器为每个 frame 建立扩展脚本环境的固定成本，主脚本自己每个 frame 只占约 0.4 到 0.7 ms。
- 因此没有做「推迟初始化」一类的改动；只有不往 iframe 里注入才能明显省下来，但那样 iframe 里的划词翻译会失效。
- 重跑：`BIT_PERF_VARIANTS=<一个临时目录> npm run perf -- idle-cost`（约 4 分钟）。

- 重新测量：`npm run perf`；与另一份扩展代码比较时用 `BIT_EXTENSION_DIR=<目录> npm run perf`。数字随机器变化，只适合在同一台机器上比较。

---

## 开发与测试

| 检查 | 命令 |
|---|---|
| 扩展与沙盒单测（Node 22+，零依赖） | `node --test "tests/**/*.test.js"` 或 `npm test`（`tests/extension/sender.test.js` 覆盖消息与端口的发送方校验） |
| 扩展端到端测试（Playwright + Chromium） | `npm ci`，`npx playwright install chromium`，然后 `npm run test:e2e` |
| JS 语法检查 | `node --check bili-index-translate/background.js`（其余脚本同理） |
| 页面开销测量（不在 CI 里，见「页面开销实测」） | `npm run perf` |
| 桌面端构建 | `dotnet build BiliIndexTranslate.Desktop/BiliIndexTranslate.sln -c Release` |
| 桌面端单测 | `dotnet test BiliIndexTranslate.Desktop/tests/BiliIndexTranslate.Core.Tests -c Release` |

- `tests/extension/` 覆盖术语表解析、提示词构造、识图双轨解析、段落锚点、学习卡 JSON 修复与流式解析，四条流式请求路径的分块、取消和 JSON 回退，并用模拟的 `fetch` 验证 412 时只发一次请求、不自动改走中继；`deeplx.test.js` 和 `custom.test.js` 覆盖 DeepLX 格式与自定义模板的请求构造、占位符转义、响应解析和各条功能路径；`settings.test.js` 覆盖 API key 从同步存储到本机存储的迁移；`image-fetch.test.js` 覆盖右键图片下载的 8 MB 上限和超时；`i18n.test.js` 检查两种界面语言的文案键、占位符和选项页标记一致，以及后台的菜单标题和错误文案跟随设置；`ports.test.js` 覆盖流式端口：进度消息的合并、`Alt+A` 只发有变化的段落、请求发出前失败也给出最终消息、端口断开时取消上游；`streaming.test.js` 还覆盖没有换行结尾的最后一行、流里的 `error` 事件，以及超时按空闲时间计（一直有数据的长回复不被切断，停滞的流报超时）；`direct-rule.test.js` 覆盖直连规则的内容和只在缺失或不同时写入；`retry.test.js` 覆盖遇到 429 和 5xx 时的自动重试（等待时间、`Retry-After`、取消、不该重试的情形）。单测在 Node 里没有浏览器语言，界面文案按英文断言。
- `tests/e2e/` 在无头 Chromium 里加载未打包的扩展，后端换成 `tests/support/mock-upstream.js`（本地 OpenAI 兼容模拟服务，按提示词返回确定的译文、词卡和识图结果）。用例覆盖划词流式与取消、术语表注入、412 与显式中继、学习卡片快捷键、`Alt+A` 双语对照（视口优先、跳过中文和代码、滚动续翻、900 段的长页面上跳转后续翻、页面持续变动时仍会扫描、页面改写或重建已翻译段落后的重译与放回、仅译与模型切换、无损还原）、后台一端断开时面板报错、`Alt+S` 框选裁剪、右键图片（含超过 8 MB 时中断下载）、选项页（含旧版本 API key 的迁移）、上游先返回 429 再恢复时划词仍显示译文、DeepLX 接口格式（模拟服务的 `/translate` 端点）和自定义模板（模拟服务里一个形状自拟的 `/custom-api` 端点）。每个用例结束时检查没有请求离开 `127.0.0.1`。`network.spec.js` 让浏览器把官方域名解析到本机的模拟服务，检查直连规则只改扩展自己发往官方地址的请求：网页发往同一地址的请求、扩展发往其他地址的请求都保持原样。`sandbox.spec.js` 在不装扩展的浏览器里打开预览沙盒，检查它引用的扩展文件都能加载、内容脚本运行无报错（不触发翻译，沙盒的翻译请求会发往真实接口），以及沙盒拒绝用别的主机名寻址或由别的网站发起的请求。夹具把界面语言固定为简体中文（浏览器自身语言随机器而不同），`i18n.spec.js` 另在英文和「自动」下走一遍面板、学习卡片、双语对照条和选项页，并检查设置改变后已打开的页面随之切换。右键菜单和扩展快捷键无法在 Playwright 里直接点击，用例在 service worker 里派发与 Chrome 相同的事件。
- 图标：扩展的 `icons/icon16|32|48|128.png` 和桌面端的 `app.ico` 由同一个脚本 `BiliIndexTranslate.Desktop/tools/make-icon.py` 画出（只用 Python 标准库），改图标后重新运行它；`tests/extension/manifest.test.js` 检查 manifest 引用的文件和图标尺寸。
- GitHub Actions（`.github/workflows/ci.yml`）在推送到 `main`、所有 PR 和手动触发时运行以上检查（`npm run perf` 页面开销测量不在 CI 里）；端到端测试失败时上传 Playwright 报告和 trace，桌面端在 `windows-latest` 上构建、测试，并试打一次发布包和一次 Velopack 安装包（都不上传）。桌面端的 NuGet 依赖（含间接依赖）只要有已公开的漏洞，CI 就会失败。Dependabot（`.github/dependabot.yml`）每周检查 Actions、npm 和 NuGet 依赖。
- 发布：先把 `bili-index-translate/manifest.json` 的 `version` 和 `BiliIndexTranslate.Desktop/Directory.Build.props` 的 `<Version>` 改成同一个版本号并合入 `main`，再推送 `vX.Y.Z` tag。`.github/workflows/release.yml` 会跑全部单测，用 `.github/scripts/package-release.ps1` 打出 3 个 ZIP 和 `SHA256SUMS.txt`，再用 `vpk`（Velopack 1.2.161）打出桌面端的安装程序 `*-Setup.exe`、`*-full.nupkg`、`releases.win.json`（仓库里已有 Velopack 版本时还有增量包 `*-delta.nupkg`），并把安装程序和更新包的校验值追加进 `SHA256SUMS.txt`，一起建成同一个草稿 Release，核对说明后手动发布。**安装版的自动更新只读取已发布（非草稿）的 Release，所以只有点了“发布”之后，已安装的用户才会收到这个版本。**发布流程会先完整跑一遍 CI（含端到端），并要求 tag 指向 `main` 上的提交，任何一项不满足都不会打包。tag 与两处版本号不一致或工作区有未提交改动时脚本直接失败。本地预演：`pwsh .github/scripts/package-release.ps1 -OutDir <空目录>`。
