# Testing

## Measured on Windows (2026-11-06)

Windows 11 Pro for Workstations (build 26200), .NET SDK 8.0.427, from the v1.4.0 sources.

| Check | Result |
|---|---|
| `dotnet build BiliIndexTranslate.sln -c Release` | 0 errors, 0 warnings |
| `dotnet test tests/BiliIndexTranslate.Core.Tests -c Release` | **253 passed, 0 failed** (session state machine, placement/anchoring, endpoint resolution, DeepLX format, custom API templates, response parsing, SSE transport, vision dual output, study cards, cache, coordinator races, settings load/migrate, interface text in both languages) |
| `dotnet publish … -r win-x64 --self-contained false` | `BiliIndexTranslate.exe` — PE32+ x64 GUI, version 1.4.0 embedded (864 KB folder) |
| `dotnet publish … --self-contained true -p:PublishSingleFile=true` | single 139 MB exe produced |
| Icon | `tools/make-icon.py` output inspected at 256 px |

Start-up reaches `ready` about 60 ms after `file logging enabled`. The idle process uses
18.5 MB private memory and 569 handles.

### End-to-end walkthrough (2026-11-06)

The app was driven against a self-built WPF source window (read-only multi-line `TextBox`)
and a local mock upstream on `127.0.0.1`, started from the framework-dependent publish output
with an isolated `USERPROFILE`, `diagnosticsFileLogging=true`, onboarding completed and both
hotkeys disabled. The source window is **not** a browser — Chromium's own UIA surface was not
exercised.

| # | Scenario | Result |
|---|---|---|
| 1 | Select text in a read-only multi-line text box | ✅ popup shown; probe `60 chars, 1 line(s), 12.0 ms`; one upstream request; card rect `230,76,686,212` |
| 2 | Click outside the card | ✅ `hidden: OutsideClick`; card gone 1.2 s later |
| 3 | Slow upstream (2.5 s) | ✅ loading state (`Translating`) observed before the result; `translate ok #14 via Direct 2501 ms` |
| 4 | HTTP 412 | ✅ error card: `Request rejected by the service (HTTP 412).` + hint about the relay; `Retry` and `Open settings` present; log `fail #18 Http 412 via Direct` |
| 5 | Control without a text pattern | ✅ `probe unsupported: no text pattern near focus (control type 50000, …)`; no text in the log line, no card |
| 6 | Foreground moves to another window | ✅ `hidden: ForegroundChanged` |
| 7 | Source window minimized | ✅ `track source minimized` → `hidden: SourceWindowLost` |
| 8 | Same selection repeated | ✅ first request sent, second served from the cache (0 upstream requests) |
| 9 | 200 repeated selections | ✅ 19.5 s, 20 samples, no upward drift |

Probe latency over the run was 2.4–12.7 ms; the two translation paths measured 1 ms (cache
warm mock) and 67 ms against the mock upstream.

### DeepLX format through the Settings window (2026-11-12)

Framework-dependent publish output of the working tree, driven through UI Automation against a
local DeepLX-style mock on `127.0.0.1` (`tests/support/mock-upstream.js`). The user's
`settings.json` was backed up first and restored afterwards (same SHA-256).

| # | Step | Result |
|---|---|---|
| 1 | Open Settings (second instance) with a settings file that has no `apiFormat` | ✅ `API format` lists `OpenAI-compatible (/chat/completions)` and `DeepLX-compatible (/translate)`; OpenAI-compatible is selected |
| 2 | Select DeepLX, endpoint `http://127.0.0.1:<port>/translate?token={apiKey}`, a test key, **Save and test** | ✅ `OK · 135 ms via direct · “译文：Hello, world.”`; the mock received one `POST /translate` with the key in the `token` query parameter, no `Authorization` header and the body `{"text":"Hello, world.","source_lang":"auto","target_lang":"ZH"}` |
| 3 | Saved file | ✅ `"apiFormat": "Deeplx"`, the endpoint with its `{apiKey}` placeholder, the key as a DPAPI blob (`apiKeyScheme: dpapi-user`); the test key does not appear in plain text |
| 4 | Restart, reopen Settings, **Save and test** | ✅ DeepLX still selected, endpoint kept, `OK · 131 ms via direct`; the mock received the same token again, so the key was read back from the file |
| 5 | Switch to OpenAI-compatible while the endpoint still contains `{apiKey}`, **Save and test** | ✅ rejected with `The endpoint contains {apiKey}, which the OpenAI-compatible format does not fill in. …`; the settings file is unchanged (message as of the custom-format build) |

Not exercised live with the DeepLX format: the selection popup, Study Card and screen snip (unit
tests only).

The same build started from `src\BiliIndexTranslate.App\bin\Release\net8.0-windows` translated
correctly, but on this machine neither `settings.json` nor `diagnostics.log` was written; from the
publish output both were. See the next section for the cause.

### Settings that cannot be written (2026-11-12)

Cause of the note above, found on this machine: the directory that holds the checkout carries a
Low mandatory integrity label that every file below it inherits
(`icacls <dir>` prints `Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)`). Windows starts an
executable with such a label at low integrity, and a low-integrity process may not write to
`%LocalAppData%`. Measured: the app started from `bin\Release` ran at integrity level `0x1000`
(Low) while the shell that started it ran at `0x2000` (Medium); a copy of `whoami.exe` placed in
`bin\` reported `Low Mandatory Level`, the original `Medium Mandatory Level`. The publish output
used in the walkthroughs was outside that directory. Security software was not involved.

The app used to log such a failure and nothing else. Now:

| # | Step | Result |
|---|---|---|
| 1 | Start from `bin\Release` (low integrity), open Settings, **Save** | ✅ the window stays open and shows `Settings were not saved (UnauthorizedAccessException: Access to the path '…\settings.json.tmp' is denied.). They stay in effect until the app exits.`; `settings.json` is unchanged (same SHA-256) and no `.tmp` file is left behind |

Not exercised live: the tray notice shown when a background save fails (text size or the
auto-translate toggle), and **Save and test** in this state (it reports the save failure together
with the result of the connection test).

If settings do not persist with `tools\run.cmd`, check the label with `icacls` and either build
to a directory without it (`dotnet publish … -o <dir>`) or move the checkout.

### Custom API template through the Settings window (2026-11-12)

Same setup as the DeepLX walkthrough above (publish output, UI Automation, the user's
`settings.json` backed up and restored with the same SHA-256), against the mock's `/custom-api`
endpoint, whose made-up shape (`{ q: [...], to }` with an `X-Api-Key` header →
`{ result: { items: [{ out }] } }`) can only be described by a template.

| # | Step | Result |
|---|---|---|
| 1 | Open Settings | ✅ `API format` lists three entries; the template fields are not shown |
| 2 | Select `Custom JSON API (template)` | ✅ the template fields appear |
| 3 | Preset `DeepL API Free` | ✅ endpoint `https://api-free.deepl.com/v2/translate`, headers `Authorization: DeepL-Auth-Key {apiKey}`, body `{"text": {texts}, "target_lang": "{targetLang}"}`, result path `translations[*].text`; status `Preset loaded: DeepL. …`; the settings file is unchanged |
| 4 | Custom with an empty result path, then with the body `{"q": {text}}`, **Save and test** | ✅ rejected with `The result path is empty. …` and `The request body is not valid JSON once the placeholders are filled in. …`; the settings file is unchanged both times |
| 5 | Fill in the mock's template and a test key, **Save and test** | ✅ `OK · … via direct · “译文：Hello, world.”`; the mock received `POST /custom-api` with `X-Api-Key` set to the key, no `Authorization` header and the body `{"q":["Hello, world."],"to":"zh"}` |
| 6 | Saved file | ✅ `"apiFormat": "Custom"` and the six `custom…` fields; the key as a DPAPI blob, not in plain text |
| 7 | Restart, reopen Settings, **Save and test** | ✅ Custom still selected, the template fields shown with the saved values, `OK · 134 ms via direct` |

The very first request of step 5 took 10.6 s; the six requests after it, including the first
request of the restarted process, took 28–134 ms. The cause was not found: proxy auto-detection is
off on this machine and `127.*` is in the proxy bypass list. It did not happen again.

### Interface language through the Settings window (2026-11-12)

Same setup as the walkthroughs above (publish output outside the checkout, UI Automation, the
user's `settings.json` backed up and restored with the same SHA-256). Windows display language
`zh-CN`; the settings file had no `uiLanguage`. Started with `--welcome` so that a second window
is open while the setting changes.

| # | Step | Result |
|---|---|---|
| 1 | Start with a settings file that has no `uiLanguage` | ✅ Welcome window in Chinese (`安静的阅读伴侣`, buttons `设置…` / `开始阅读`): "Auto" follows the Windows language |
| 2 | Open Settings | ✅ title `Bili Index Translate — 设置`, buttons `取消` / `保存` / `保存并测试`; `Interface language` lists `自动（跟随 Windows 语言）` (selected), `English`, `简体中文` |
| 3 | Select English, **Save** | ✅ the file has `"uiLanguage": "en"`; the Welcome window, which stayed open, now reads `A quiet reading companion` / `Settings…` / `Start reading` |
| 4 | Reopen Settings | ✅ title `Bili Index Translate — Settings`, `Cancel` / `Save` / `Save and test`, English selected, the shortcut help and the `Show` button in English; selecting another language changes nothing until it is saved |
| 5 | Select 简体中文, **Save**, reopen Settings | ✅ `"uiLanguage": "zh-CN"`; Welcome and Settings in Chinese, including the help texts set from code (`在 UI Automation 读不到选中内容时使用…`) and the `显示` button |
| 6 | Select Auto, **Save** | ✅ `"uiLanguage": "auto"`; Welcome stays Chinese |

Not exercised live in either language: the tray menu, the translation popup, the Study Card, the
screen-snip overlay and the Diagnostics window (their text goes through the same table and binding;
the unit tests check that every key they use exists).

### Resource behaviour over 200 selections

Sampled every 10th selection of the app process:

| Metric | First sample | Last sample | Note |
|---|---|---|---|
| Handles | 569 | 576 | one reused popup window; no per-selection window leak |
| USER objects | 64 | 66 | flat after the first few selections |
| GDI objects | 17 | 17 | flat |
| Private bytes | 18.5 MB | 19.0 MB | +0.5 MB over 200 selections |
| Working set | 83.7 MB | 85.0 MB | flat |

The 200 selections used the same source text, so most were served from the translation cache;
this measures the UI/UIA path, not 200 upstream round trips.

Timing targets (200–350 ms settle, prompt loading, cache hits without network) are design
parameters, not results. The Diagnostics window reports p50/p95 per category while the app
runs — the categories are `Detect`, `Probe`, `Cache`, `Render`, `Http` and `Refresh`.

If `dotnet test` aborts on Windows with `Win32Exception (5): Access is denied` while the test host
looks up its parent process, check the checkout for the Low integrity label described under
"Settings that cannot be written": `testhost.exe` in `bin\` is then started at low integrity and
may not open the medium-integrity `dotnet` process that started it. On this machine the abort
reproduces with the default output directory and all tests pass with the output elsewhere. Point the
output somewhere else, for example `dotnet test … -o %TEMP%\bit-tests`.

## Checked on Linux (2026-11-15)

Arch Linux, .NET SDK 8.0.428, from the 1.5.0 sources plus the streaming changes (idle timeout for
streamed replies, errors reported inside a stream, `LatestValueRelay` for image-translation deltas).
The app cannot run on Linux, so this covers compilation and the platform-independent core only.

| Check | Result |
|---|---|
| `dotnet build BiliIndexTranslate.sln -c Release -warnaserror` (the app project sets `EnableWindowsTargeting`) | 0 errors, 0 warnings |
| `dotnet test tests/BiliIndexTranslate.Core.Tests -c Release` | **266 passed, 0 failed** (the 253 above plus `StreamingTests.cs`: stream deltas and in-stream errors, the idle timeout over a real socket, `LatestValueRelay`; and `SseLineTests.cs`: SSE line reading with CRLF, no space after `data:`, blank and comment lines, `[DONE]`) |

Not exercised on Windows yet: `AppHost` now hands streamed image-translation deltas to the popup
through `LatestValueRelay` at `DispatcherPriority.Background`. The relay itself is unit-tested; that
the popup still updates smoothly while an image translation streams, and that the final result is
never overwritten by a late delta, should be confirmed with scenario walkthroughs on Windows.

## Checked on Linux (2026-11-25): .NET 10 and the streaming selection card

.NET SDK 10.0.404. The app now targets .NET 10, and the selection card shows the translation while it
is generated (chat format only; DeepLX and custom templates are unchanged).

| Check | Result |
|---|---|
| `dotnet build BiliIndexTranslate.sln -c Release -warnaserror` | 0 errors, 0 warnings |
| `dotnet test tests/BiliIndexTranslate.Core.Tests -c Release` | **310 passed, 0 failed** (the 266 above plus `StreamingTranslationTests.cs`: partial text from the translator, `<think>` filtering, a server that ignores `stream`, in-stream errors, cancellation; the coordinator delivering partial text only while the request is current and never after the final result; the session showing it only while loading) |

Not exercised on Windows yet:

- The card fills in while the translation streams, with no stutter and without delaying input or rendering.
- A card above the selection grows upward without jumping or flickering (`SizeChanged` → `Replace`).
- The caption shows "生成中…" / "streaming…" while text arrives. When the final result lands, Copy, Pin and the Study Card button come back.
- Esc, an outside click or a new selection during streaming closes or replaces the card, and no older partial text is drawn afterwards.
- The real Index-Translate endpoint answers a streamed request with `text/event-stream`. Check this direct, through the relay, and with the HTTP 412 hint.
- A cache hit shows the final translation at once, without a streaming caption.

## Installer, start-up entry and updates (2026-11-25)

Velopack (1.2.161) installer and auto-update, plus the "Start with Windows" option.

Checked on Linux (.NET SDK 10): `dotnet build BiliIndexTranslate.sln -c Release -warnaserror` gives 0 errors and
0 warnings, and `dotnet test tests/BiliIndexTranslate.Core.Tests -c Release` passes **349** tests (the 318 before plus
`UpdateAndStartupTests.cs`: when the next automatic check happens, the quoted start-up value and the "does this value
start this exe" rule, update error text without URLs or tokens, and the `autoCheckForUpdates` setting with an old
settings file). CI also runs "Velopack pack (dry run)", which publishes the self-contained build, runs `vpk pack` and
checks that `*-Setup.exe` and the full package exist. None of the following can be done on Linux, and the dry run does
not install or start anything. Walk through these on Windows, with a build that has a version number higher than the
one you start from:

| # | Check | Expected |
|---|---|---|
| 1 | Run `BiliIndexTranslate.Desktop-win-Setup.exe` from a release build on a user without admin rights | Installs without a UAC prompt into `%LocalAppData%\BiliIndexTranslate.Desktop`; `current\BiliIndexTranslate.exe` exists; Start menu and desktop shortcuts; the app starts and shows its tray icon. `%LocalAppData%\BiliIndexTranslate` (settings, logs) is a different folder |
| 2 | Open Settings in the installed app | "Start with Windows" is not ticked (default); "Check for updates automatically" is ticked; the version shown is the installed one; the update controls are enabled |
| 3 | Tick "Start with Windows", Save, then look at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | Value `BiliIndexTranslate` = `"%LocalAppData%\BiliIndexTranslate.Desktop\current\BiliIndexTranslate.exe"` with the quotes. `settings.json` has no start-up field. Sign out and in: the app starts by itself |
| 4 | Change the value to another path by hand, reopen Settings | The box is not ticked. Tick it and Save: the value is the current exe path again |
| 5 | Untick the box and Save | The value is gone; reopening Settings shows it unticked |
| 6 | Publish a draft release of a higher version (assets uploaded by the release workflow), publish it, and press "Check for updates" in Settings | The window says a new version was found and is downloading; a tray notice "Version x has been downloaded and will be installed when you exit." appears once; the tray menu shows "Restart and update" |
| 7 | With the draft **unpublished**, press "Check for updates" | "You have the latest version." (drafts are invisible to the updater) |
| 8 | Wait for the automatic check: set "Check for updates automatically" on, start the app, and watch Diagnostics (turn on file logging) | A `check` line about 60 s after start, no more until 24 h later (or after sleep and wake past 24 h); with the option off, no `update` lines at all |
| 9 | Tray → Restart and update | The app exits, the new version installs and starts; Settings shows the new version; the start-up value still points to the same `current\` path and the app still starts at sign-in |
| 10 | Download an update again, then Tray → Exit | No window or restart; within a few seconds the new version is installed (check the version after starting it again) |
| 11 | Cut the network (or block `api.github.com`) and press "Check for updates" | The window shows a short failure reason (no URL, no token). The automatic check in the same situation shows nothing and only writes a diagnostics line |
| 12 | Run the portable zip build | The "Start with Windows" box works; "Check for updates automatically" and the button are disabled and the note "The portable build does not support automatic updates…" is shown; Wireshark/Fiddler shows no request to GitHub |
| 13 | Tick "Start with Windows", then uninstall in Windows Settings → Apps | The Run value `BiliIndexTranslate` is deleted; `%LocalAppData%\BiliIndexTranslate.Desktop` is gone; `%LocalAppData%\BiliIndexTranslate\settings.json` is still there |
| 14 | Install a second time over a running copy | Setup closes or replaces the running app cleanly; one tray icon after the start |

Not verified at all before the first release: the delta package (needs two Velopack releases), and that the
first release works with no earlier Velopack release to download.

## Local OCR screen snip (added on Linux, not yet run on Windows)

Written and compiled on Linux (`-warnaserror`: 0 warnings; Core tests **385**, the 349 before plus
`VisionModeTests.cs`: routing table, old/unknown `visionMode` values, line and word joining). The App now targets
`net10.0-windows10.0.19041.0`; `dotnet publish -r win-x64 -p:PublishSingleFile=true --self-contained false` succeeds on
Linux. `Windows.Media.Ocr` cannot run here, so every row below is **unverified** until done on Windows (do it on a
machine with only the English OCR pack and, separately, with the Chinese or Japanese pack added):

| # | Check | Expected |
|---|---|---|
| 1 | Settings → Service → Image reading | Three entries, default Automatic; the hint text is shown; the choice survives Save and a restart (`visionMode` in `settings.json`); delete the field or write `"visionMode": "x"` and the app opens with Automatic and the rest of the file intact |
| 2 | OpenAI-compatible endpoint, Automatic, snip a paragraph | Same as before: cloud vision model, caption "image", the screenshot is sent |
| 3 | DeepLX endpoint, Automatic, snip English text | No "not supported" card; loading "Recognizing text on this PC…", then the card with the recognized text as the original and the translation, caption "DeepLX · local OCR · direct"; the mock receives one request holding only text (check it with a proxy: no image bytes) |
| 4 | Custom API endpoint, Automatic, same | Same as row 3 with the custom API's label |
| 5 | OpenAI endpoint, Always local OCR | Recognized text is translated with the chat model and streams in; caption "<model> · local OCR"; no `image_url` in the request |
| 6 | DeepLX, Always cloud vision model | Error card "not supported" plus the hint to choose Automatic or Local OCR; no request leaves |
| 7 | Snip a region without text (a plain colour), local OCR | Error card "No text was recognized…" with the hint to add the language in Windows Settings > Time & language > Language & region |
| 8 | Remove all OCR language packs (or use an account whose profile languages have none), snip | The "no OCR language pack" error card, no crash |
| 9 | Snip Chinese / Japanese text with that language's pack installed | No spaces between characters in the original shown on the card; lines of a paragraph are joined without spaces; mixed Latin words keep one space |
| 10 | Snip a hyphenated English paragraph ("inter-" / "national") | Joined as "international" |
| 11 | Snip very large regions (4K, several monitors) and a clipboard image larger than `OcrEngine.MaxImageDimension` | Recognized without an error (scaled down); no visible freeze of the UI |
| 12 | Close the card (Esc or click out) while "Recognizing…" or translating; start a second snip while the first is loading | The closed card stays closed; only the newest snip's result is shown; the retry key repeats the last snip |
| 13 | Tray → Recognize screen text and copy, snip a paragraph | One tray notice "Copied N characters" (N = characters, not UTF-16 units); Ctrl+V pastes the text; no popup card; no network traffic at all (proxy) |
| 14 | Same on a region without text; with the clipboard locked by another program | A warning notice with the reason; no clipboard change |
| 15 | Same with a DeepLX/custom endpoint and with an endpoint that is unreachable | Works the same (no request is made) |
| 16 | Diagnostics (file logging on) after rows 3, 7 and 13 | Only lengths and failure kinds (`local OCR ok, chars=…`); no screenshot data and no recognized text |
| 17 | Windows 10 1809 / 1903 (lowest supported) and Windows 11 | The app starts and local OCR works; check that the self-contained and the framework-dependent single-file builds both start |
| 18 | Switch the interface language to English / 简体中文 with the tray menu closed | The new tray item, the Settings entries and the OCR messages follow the language |

## Baseline on the CI runner (2026-11-26)

Measured by UI tests 6–8 below on the `windows-latest` runner (Windows NT 10.0.26100, 4 logical processors) in CI
run 45792315547 (commit `6d04571`). The app runs from the Release build output (framework-dependent), not from the
single-file or installed build. Shared runners are noisy: read these as orders of magnitude and compare trends.

Note (numbers below unchanged): these were measured before the keyboard hook accepted keys injected by other
programs. Since then Ctrl+A in the WPF tests also triggers through the keyboard hook (KeyUp, 280 ms) next to the
UIA event (320 ms), so later latency runs are not directly comparable to this first baseline.

| Measure | Result |
|---|---|
| Ctrl+A → card with the text, cache hit (n = 10) | min 376 / median 414 / max 439 ms. 320 ms of that is the settle delay for a UIA selection event (250 ms + 70 ms); a mouse release waits 250 ms |
| Ctrl+A → first text, cache miss (first selection after start) | 815 ms; full text at 1521 ms (the mock sends the second and third segment 400 ms apart). The mock sends the first segment at once, so the first selection is about 400 ms slower than a cache hit. **Located and reduced (2026-11-26):** the app log split it into creating and showing the card window (264 ms) and the first network request up to text on the card (162 ms). The card window is now prepared when idle after start-up (about 170–260 ms, nothing shown); the first card then shows in about 30–40 ms, and the first text arrives at 506–627 ms instead of 782–815 ms (runs 45822245748, 45823738988). The first request's cost is left: removing it would mean contacting the translation service at start-up |
| Loading state | not observed: "first card visible" equals "first text". The loading text is either too short-lived to catch or not exposed to UIA |
| Process start → `ready` | 404–419 ms over 3 runs (polled every 200 ms); 19–21 ms from the app's first log line to `ready` |
| 100 selections (cache hits) | handles 750–766 (−2), GDI objects 32 (0), USER objects 66–67 (−1), private bytes 54–63 MB (+0.3 MB), working set 126–151 MB (−7.5 MB): no growth |
| Core unit-test coverage (coverlet) | lines 86.9 %, branches 71.7 % |

The walkthrough on 2026-11-06 measured a working set of about 85 MB on a real machine with .NET 8. The runner shows
126–151 MB with .NET 10; the two were not measured on the same machine, and private bytes are the steadier figure.

## Diagnostics bundle (unit tests)

`tests/BiliIndexTranslate.Core.Tests/DiagnosticBundleTests.cs`: `PrivacyScrubberTests` (profile path with both separators and any case, `Users\<name>`, whole-word user name, a 2-character user name is not replaced as a word, machine name, missing inputs, and any other folder under `X:\Users\`: the profile's 8.3 short name such as `RUNNER~1`, a name with spaces only up to the next separator, another account; `Public` and `Default` stay),
`SettingsRedactorTests` (origin-only URLs, masked header values, body and glossary as lengths, custom name only for a built-in preset, sentinels and the API key never in the output, and a reflection test that fails when a public settable `AppSettings` property is in neither the exported nor the excluded list),
`DiagnosticBundleTests` (`ReadTail`: missing file, small file, big file with marker and no partial first line, readable while another stream holds it open for writing; `Write`: entry names, UTF-8 without BOM, scrubbing). `StartupOptionsTests` covers `--diagnostics`. Unit test count after this change: 455 (CI run 45884262902). The UI test is row 22 below.

`tests/BiliIndexTranslate.Core.Tests/SpeechLanguageTests.cs`: `SpeechLanguage.Guess` for the selection card's speaker button (an explicit Source language setting wins over the text; "auto", empty and unknown values detect from the text; kana gives ja-JP even with Han or Latin words around it; Han only gives zh-CN; Hangul, Cyrillic, Greek, Thai, Hebrew, Arabic, Devanagari; Latin-script scoring for es/fr/de/en; empty and digits-only give en-US; emoji and astral characters do not crash). CI run 45937130471 Unit test count after this change: 483 (CI run 45937130471). Follow-up: `TryDetect` returns null instead of falling back to en-US (one ambiguous word, no letters, empty), and `Guess` keeps its en-US fallback.
`tests/BiliIndexTranslate.Core.Tests/VoiceMatcherTests.cs`: `VoiceMatcher.Pick` for the speaker button's voice choice (exact language match, the default voice preferred among exact matches, the same primary language as a fallback such as es-MX for es-ES and zh-TW for zh-CN, case-insensitive, no match and an empty list give none) and `LcidListToTags` for SAPI voices ("411" gives ja-JP, "409;9" gives en-US first, garbage entries ignored). CI run: TODO. `LanguageNamesTests.cs`: the language name in "No … voice is installed" follows the app's interface language (ja-JP → Japanese / 日语), uses only the primary subtag, and shows the tag itself for languages outside the table, so it never depends on the machine's culture data. Unit test count after this change: 561 (CI run 45950447164).

## Vocabulary book (unit tests)

`tests/BiliIndexTranslate.Core.Tests/VocabularyTests.cs`: `VocabularyEntryTests` (`ForStudyCard` copies every field, `ForSentence` keeps the source language only when it is explicit, identity ignores case and whitespace but not kind or target language, search in text / translation / meaning), `VocabularyStoreTests` (round trip, camelCase JSON with `schemaVersion` and string enums, missing file, a malformed file moved to `vocabulary.json.bad-<time>` with its original bytes, a second malformed file never overwrites the first backup, a newer `schemaVersion` loads read-only and the file stays byte for byte the same, an unreadable file is left alone, no temp file after saving, a failed save keeps the previous file and undoes the change, `Add` / `Toggle` / `Remove` / `RemoveMany` / `Clear` and `Changed`, logs without entry text), `AnkiExportTests` (the six header lines, sentence row, escaping of tab / newline / CR / quote / `<` / `&`, GUID `bit-<id>` stable across exports, sanitized tags, the word Back layout, UTF-8 without BOM, no temp file, temp file removed on failure). `StartupOptionsTests` covers `--vocabulary`. Unit test count after this change: 524 (CI run 45938525609). The UI tests are rows 24 and 25 below. Follow-up: a sentence saved with Source language "auto" stores the language its text clearly shows (`TryDetect`), an explicit setting still wins; Anki language tags keep only the primary subtag of a BCP-47 tag (`ja-JP` → `lang-ja`, `zh-Hant` → `lang-zh`). Unit test count after the follow-up: 545 (CI run 45946224132).

## UI tests (FlaUI)

`tests/BiliIndexTranslate.App.UiTests` (xunit, FlaUI.UIA3) drives the real app on a real desktop. It runs in
CI on `windows-latest` after the unit tests and cannot run on Linux (the solution only compiles there).

What each test does: it starts an in-process mock service (`HttpListener` on `http://localhost:<free port>/`;
a chat request with an `image_url` content part gets the vision dual-output answer `〖译文〗` + `MockUpstream.VisionTranslation`, `〖原文〗` + `MockUpstream.VisionSource`, streamed in three deltas like the text answer),
the target window `tests/BiliIndexTranslate.UiTestTarget` (a read-only multi-line `TextBox`, AutomationId
`SourceText`) and the app with `--data-dir <temp dir>`. The temp dir holds a `settings.json` written with
`SettingsStore` (mock `BaseUrl`, OpenAI format, model `Index-Translate-9B`, auto-translate on, onboarding done,
English UI, both hotkeys off unless a test turns the explicit one on, UIA selection events on unless a test turns
them off, diagnostics log on). `--data-dir` also gives that instance its own single-instance
mutex, so a running copy of the app on the same machine does not interfere.

| # | Test | Checks |
|---|---|---|
| 1 | `Select_shows_streamed_card_and_escape_closes_it` | Ctrl+A in the target → card (`TranslationText`) ends with the three streamed segments joined; the one request had `stream: true` and `model: Index-Translate-9B`; Esc hides the card |
| 2 | `Card_fills_in_while_streaming` | at least one state with only part of the text, and the caption (`Caption`) shows "streaming" |
| 3 | `Same_selection_again_is_served_from_the_cache` | after Esc, a click and a new Ctrl+A, the card shows the complete text from its first frame and the mock sees no second request |
| 4 | `Http_412_shows_an_error_card` | mock answers 412 → error card whose text contains `412` |
| 5 | `Edge_page_selection_shows_full_card_and_escape_closes_it` (`EdgeSelectionTests`) | Microsoft Edge (own temp profile, no `--force-renderer-accessibility`) opens a local page with one `<p>`; triple click on the paragraph (raw mouse down/up: FlaUI's `Mouse.Click` deliberately avoids double clicks) → card shows the complete text, every request had `stream: true`, Esc hides the card. No keyboard fallback. The test finds the paragraph through Edge's UIA tree, which turns on Chromium's accessibility by itself, so it does not show that the app does so alone. Skipped, not failed, when `msedge.exe` is not found (`%ProgramFiles(x86)%` / `%ProgramFiles%`, or `BIT_EDGE_EXE`). The test output has the app log; when no card appears it also has the focused element and the selection as UIA reports them, and `TestResults\ui-screenshots\` gets `<test>.uia-tree.txt` (first levels of the Edge UIA tree) |
| 6 | `Latency_from_selection_to_card` (`BaselineTests`) | measurement: one session; first selection (cache miss) times first card state, first partial text and final text from the moment Ctrl+A is sent, polled about every 10 ms; then 10 cache-hit cycles (min / median / max). The trigger is the UIA text-selection-changed event, and the mock's segment delay dominates the final time on a miss. Asserts only that every card reached the full text and the mock got a chat request |
| 7 | `Cold_start_time` (`BaselineTests`) | measurement: three sequential sessions; time from `Process.Start` of the app to the `ready` log line (polled every 200 ms, so up to 200 ms high), plus the app's own log time from first line to `ready` |
| 8 | `Resource_use_over_repeated_selections` (`BaselineTests`) | measurement: one warm-up selection, then 100 cycles of clear selection, Ctrl+A, wait for the card text; handle count, GDI and USER objects, private bytes and working set are sampled before the loop and every 10 cycles, with first-to-last deltas. Asserts that every card reached the full text and the mock got a chat request, and, between the first and the last sample (after the 100 cycles), limits against a real leak: handles ≤ +50, GDI objects ≤ +5, USER objects ≤ +5, private bytes ≤ +80 MB. The private-bytes limit is only a gross-leak catch: GC timing made one CI run read +45.7 MB that did not reproduce. The JSON is written before the limits are checked |
| 9 | `Shift_end_selection_triggers_the_card_without_uia_events` (`InputAndClipboardTests`) | session with `UseUiaSelectionEvents = false`: Ctrl+Home, Shift+End (keys injected by FlaUI, no app marker), Shift released → the card reaches the full text and the one chat request body contains the start of the target's text. Only the keyboard hook can trigger here; it fails when injected keys are ignored |
| 10 | `Hotkey_uses_clipboard_fallback_and_restores_the_previous_clipboard_text` (`InputAndClipboardTests`) | explicit hotkey on; the test puts a known text on the clipboard, clicks the target's `NoTextPatternBox` (custom control without a text pattern, copies its own fixed text on Ctrl+C), presses Alt+Q → card with the full text, the chat request body contains the control's text, and within 5 s the clipboard text is the known text again |
| 11 | `Edge_*` (`AppMatrixTests`, 4 tests: double click, triple click, drag, Ctrl+A) | App matrix, Microsoft Edge. One session per gesture, strict (fails when the card does not appear). Shared page `bit-page.html`: the measured line `Quick brown fox jumps over the lazy dog.` (32px monospace, `white-space:nowrap`, margin 40px) and below it a warm-up line `Warm up line for the reader.`. Warm-up ("first selection"): a freshly started program may not be ready for its first selection (CI saw a failed first probe in Chrome and Firefox), so before the measured gesture the test triple-clicks the warm-up line, waits up to 20 s for its card, presses Esc and waits until the card is gone. The outcome (pass/fail, time) is recorded as `firstSelection` in the JSON and shown in the table; it is informational, not asserted. The measured gesture's text is new, so it is not a cache hit, and the assertions use the last chat request, which must be a new one. Positions come from the line's UIA bounding rectangle (monospace, so a word's place follows from its character index). Double click on `brown` → request body contains `brown` and not the whole line; triple click → body contains the whole line; drag from just inside the line start to 60 % of its width → body contains `Quick`; keyboard: click into the page, Ctrl+A (pages have no caret, so Shift+arrows select nothing) → body contains the whole line. Double and triple clicks are raw mouse down/up pairs 50 ms apart |
| 12 | `Chrome_*` (`AppMatrixTests`, 4 tests) | Same as 11 with Google Chrome (`%ProgramFiles%`, `%ProgramFiles(x86)%` or `%LocalAppData%` `\Google\Chrome\Application\chrome.exe`; `BIT_CHROME_EXE` overrides), own profile, same flags as Edge |
| 13 | `Firefox_*` (`AppMatrixTests`, 4 tests) | Same as 11 with Mozilla Firefox (`%ProgramFiles%\Mozilla Firefox\firefox.exe`; `BIT_FIREFOX_EXE` overrides), started with `-profile <dir> -no-remote -new-instance -wait-for-browser <page>` (the launcher stays the parent, so killing the started process tree closes the browser); a `user.js` in the profile suppresses the first-run, welcome, default-browser, data-reporting and update prompts |
| 14 | `Notepad_*` (`AppMatrixTests`, 4 tests) | Same gestures with classic Notepad (`%WINDIR%\System32\notepad.exe`) on a .txt file with the line and the warm-up line (warm-up: double click on `reader`). Positions come from the Edit control's UIA text ranges. Triple click: n/a, the classic Edit control has no triple-click selection (CI saw an empty selection); that test is always skipped with that reason and the JSON and table show `n/a`. Keyboard: click into the editor, Ctrl+Home, Shift+End → body contains the whole line |
| 15 | `Edge_cold_chromium_triple_click_without_uia_from_the_test` (`AppMatrixTests`) | Cold Chromium. Rule: the test process sends no UI Automation request to Edge, so the app alone has to get Chromium to expose the selection. Edge's window is found with `EnumWindows` (title match), its page area with `EnumChildWindows` (class `Chrome_RenderWidgetHostHWND`), the rectangle with `GetWindowRect`, the scale with `GetDpiForWindow`. The page (`bit-cold-page.html`) positions the line absolutely (`left:40px; top:40px`), so the triple-click point is computed from the page area alone. No UIA tree or focus dump of Edge on failure either. The card is read from the app's own windows only. Asserts the card reaches the full text and the body contains the whole line; the output says whether the first triple click was enough (if not: 10 s wait, clear, one more triple click, 15 s wait) |
| 16 | `Final_translation_raises_LiveRegionChanged_with_the_translation_as_name` (`LiveRegionTests`) | one session: Ctrl+A makes the card window exist, Esc hides it; the test subscribes (FlaUI, `TreeScope.Subtree`) to UIA `LiveRegionChanged` on that window (found with Win32, handle taken while the card was visible); a second, different selection (Ctrl+Home, then Ctrl+Shift+Right three times, so not a cache hit and not the same selection) → within 5 s after the final text an event arrives from `TranslationText` whose `Name` is the full translation, and every event from that element carries the full text (no event for a partial text). Fails if a test process cannot receive the event |
| 17 | `App_follows_windows_high_contrast_and_still_shows_the_card` (`HighContrastTests`) | **changes the whole desktop of the machine running it.** `SystemParametersInfo(SPI_SETHIGHCONTRAST)` turns Windows high contrast on ("High Contrast Black", not persisted); the app logs `theme: high contrast` within 10 s; a selection still produces the card with the full text; two review screenshots (no pixel assertions) `TestResults/baseline/hc-card-hover.png` (mouse over the Copy button, hover = Highlight fill) and `hc-card-normal.png`; Alt+Shift+S (snip hotkey enabled in that session) opens the screen snip overlay, which logs `snip overlay shown (high contrast)` (asserted), then `TestResults/baseline/hc-snip.png` (full screen) is saved and Esc closes it (`snip overlay closed` asserted); high contrast is restored in a `finally` (to the state read with `SPI_GETHIGHCONTRAST`), and the app then logs `theme: light` or `theme: dark` within 10 s. A failing SPI call fails the test with the Win32 error (no silent skip). Own class so nothing else runs while the desktop is in that state. A second test in the class, `Card_hover_screenshot_in_the_normal_theme`, saves `light-card-hover.png` / `light-card-normal.png` with high contrast off, to compare the normal theme against an earlier run. All PNGs are in the `baseline` artifact |
| 18 | `Snip_drag_translates_through_vision_and_shows_the_card` (`VisionSnipTests`) | characterization of screen snip → vision translation → card. Session with the snip hotkey on (OpenAI format, VisionMode Auto = cloud vision); the test reads the `SourceText` rectangle, presses Alt+Shift+S, waits for `snip overlay shown`, drags a 300×120 px rectangle inside the text box (raw mouse down, 10 small moves, up) → the card (`TranslationText`) shows `MockUpstream.VisionTranslation` (the translation part of the mock's `〖译文〗…〖原文〗…` answer); the mock got exactly one chat request and its body has an `image_url` content part; Esc hides the card. The test output lists the steps and the app log lines with `snip`, `vision` and `translate` |
| 19 | `Open_settings_from_the_error_card_and_cancel_leaves_the_settings_file_unchanged` (`WindowTests`) | characterization of the Settings window. Session with the mock answering 412; Ctrl+A → error card; `settings.json` read as bytes; `OpenSettingsButton` on the card invoked (UIA invoke) → within 10 s a visible top-level window of the app process whose title contains `Settings` (Win32 enumeration by process, then `FromHandle`); its `CancelButton` invoked → the window is gone within 10 s and `settings.json` is byte for byte the same |
| 20 | `Welcome_window_opens_on_first_run_and_start_reading_saves_onboarding_completed` (`WindowTests`) | characterization of the Welcome window. Session started with `OnboardingCompleted = false` (`UiSession` parameter `onboardingCompleted`, default true) → the app opens the Welcome window: a visible window of the app process with a `Start reading` button (found by button name, since the title `Bili Index Translate` is shared with the card); the button invoked → the window is gone within 10 s and, within 5 s, `settings.json` has `onboardingCompleted` true (only that property is read as JSON; the file also holds the encrypted key) |
| 21 | `Selection_key_that_selects_nothing_leaves_a_log_line` (`InputAndClipboardTests`) | session with `UseUiaSelectionEvents = false`: Ctrl+Home, then Shift+Left (selects nothing) and Shift released, which the keyboard hook still reports as a selection gesture → within 10 s `diagnostics.log` has `no selection found after a selection key`; no card, no chat request. Before, an automatic probe that found no selection after a selection key logged nothing (seen in #26, where Firefox updated its selection late after Ctrl+A) |
| 22 | `Export_writes_a_scrubbed_zip_without_secrets_or_the_user_name` (`DiagnosticsExportTests`) | Diagnostics bundle. Session started with `--diagnostics` (`UiSession` parameter `appArgs`) and a `settings.json` with sentinel strings: `baseUrl` `https://diag-host.example/v1/SENTINEL1-path?token=SENTINEL2`, `customHeaders` `X-Test-Secret: SENTINEL3`, `glossaryText` `SENTINEL4 => x`, the plain v1 `apiKey` `SENTINEL5` (`plainApiKey`; the app reads and then protects it) and file logging on. `ExportDiagnosticsButton` invoked → the common Save dialog (a visible window of the app process with class `#32770`) is driven by UIA: file-name edit `1001` set to a path in the temp dir, Save button `1` invoked → `ExportStatus` reads `Saved: diag-test.zip`. The ZIP holds exactly `report.txt`, `system.txt`, `settings.json`, `diagnostics.log` (no `crash.log`); no entry contains `SENTINEL` or `Environment.UserName` (when 3 or more characters); `settings.json` contains `https://diag-host.example`; `system.txt` contains `%USERPROFILE%` when the data directory is under the profile. No entry contains a folder under `X:\Users\` other than `%USERNAME%`, `Public` or `Default` (the runner's `%TEMP%`, where the test data dir lives, is `C:\Users\RUNNER~1\...`). CI run 45884262902 |
| 23 | `Speak_button_appears_on_the_final_card_and_logs_one_request` (`SelectionCardTests`) | Ctrl+A, card with the full text, `SpeakButton` visible (found within 5 s), UIA invoke (a plain click, so normal speed) → within 5 s `diagnostics.log` has `speak original (lang en-US` (the target text is English). Then, within 5 s, `diagnostics.log` has one of `winrt voice`, `sapi voice` or `no voice for`; the test writes which one to its output and does not assert it (the runner's voices are unknown; `no voice for en-US` passes). Does not depend on audio: the log line is written before the voice is asked, and speech may fail on the runner. Row 23 with the voice check: CI run 45950447164, which logged `winrt voice en-US (Microsoft David)` and `winrt synthesized 167246 bytes (audio/wav)` and no playback failure (the test prints every speech log line). The text itself is never logged. `Http_412_shows_an_error_card` in the same class also asserts that `SpeakButton` is not on the error card. CI run 45937130471 |
| 24 | `Star_on_the_selection_card_adds_the_sentence_and_a_second_click_removes_it` (`VocabularyTests`) | vocabulary book, selection card. Ctrl+A in the target → the card shows the full mock text and `SaveWordButton` (named `Add to vocabulary`) is shown; no `vocabulary.json` exists yet. The button invoked → within 15 s `vocabulary.json` in the session data dir has exactly one entry: `kind` `sentence`, `text` equal to the target's text (read through UIA from `SourceText`, trimmed), `translation` equal to `MockUpstream.FullText`, `sourceLanguage` `en-US` (setting "auto", detected from the English target text), a target language and an id; the button is then named `Remove from vocabulary`. Invoked again → the file has no entries and the button is named `Add to vocabulary` again. CI run 45938525609 |
| 25 | `Vocabulary_window_lists_exports_to_anki_and_deletes_the_selected_entry` (`VocabularyTests`) | Vocabulary window and Anki export. `vocabulary.json` with one word and one sentence is written into the data dir before the app starts (`UiSession` parameter `seedDataDir`, using `VocabularyStore`); the app is started with `--vocabulary` → `EntryList` shows 2 items. `ExportButton` invoked → the Save dialog is driven by the shared `SaveDialog.SaveAs` helper (the same steps as test 22: file-name edit `1001` focused, Ctrl+A, the path typed with the keyboard, Save `1` invoked) → `StatusText` reads `Exported 2 entries to anki-test.txt`; the file at the typed path starts with `#separator:Tab`, has no byte order mark, no `.tmp` next to it, and 2 data rows that contain `"bit-`. The sentence item selected through UIA, `DeleteButton` (enabled by the selection) invoked → `vocabulary.json` has one entry, the word, and the list shows one item. CI run 45938525609 |

All tests in 11–15 skip, not fail, when their program is not installed. Process cleanup: before and after every
session with an external target, any process that owns a visible top-level window whose title starts with
`BIT Selection Test`, `BIT Cold Chromium Test` or `bit-line.txt` is killed (Win32 `EnumWindows` / `GetWindowText` /
`GetWindowThreadProcessId`; only those pids, never programs by name), because a launcher process can hand over to another
process and exit, so killing the started process does not always close the browser. Each outcome (app, gesture,
pass/fail/n/a, first selection, the selected text from the request body, time to card) goes to the test output and to
`TestResults/baseline/app-matrix-<app>.json` (one file per app, one entry per gesture; cold test: gesture
`cold-triple-click` in the Edge file). Time to card is measured from the end of the gesture and includes the mock's
three segments (about 0.8 s). The `Baseline summary` step renders an "App matrix" table from these files.

All card lookups (`CardText`, `CaptionText`, `ErrorText`, loading text) find the app's own top-level windows with Win32
(`EnumWindows`, process id, `IsWindowVisible`) and read them with `Automation.FromHandle`, so reading the card never
enumerates the desktop through UIA; a hidden window counts as no card.

Tests 6 and 7 only record numbers (no thresholds); test 8 records numbers and has the leak limits above. Each writes `TestResults/baseline/<test>.json`
(metrics plus OS version and processor count). In CI the `Baseline summary` step puts the tables, together with
the line and branch coverage of the Core unit tests (`coverlet.collector`, cobertura under `TestResults/coverage`),
in the job summary, and the artifact `baseline` holds the JSON files and the coverage XML. Numbers from shared
runners are noisy; compare trends, not single runs.

The card's translation text is read from the element's UIA `Name` (set in `PopupWindow.ShowTranslation`, so screen
readers read the translation; empty while loading). The fixed label "Translation" is now the element's `HelpText`
(before, the text was the `HelpText` and the label the `Name`; `UiSession.CardText` reads the `Name`). Error
title/message and the caption are read from their `Name`.

Run on a local Windows machine: `tools\test-ui.cmd` (builds the solution, then runs the tests). Do not touch the
mouse or keyboard while they run; the machine must be unlocked. The executables are found under
`src\BiliIndexTranslate.App\bin\Release\net10.0-windows*\` and
`tests\BiliIndexTranslate.UiTestTarget\bin\Release\net10.0-windows*\`; set `BIT_APP_EXE` / `BIT_TARGET_EXE`
to use other builds. On failure a full-screen screenshot plus the app's `diagnostics.log`, `crash.log` and
`settings.json` are saved to `TestResults\ui-screenshots\` (CI uploads that folder and the trx file). The temp
data dir and both processes are removed after every test.

## Scenarios

Status key: ✅ verified on Windows · ◐ partial · ❌ failed · ⬜ not tested yet.
Core-only logic that is covered by unit tests is marked (unit).

| # | Scenario | Expected | Status |
|---|---|---|---|
| 1 | Select a sentence in a browser | Card appears above the selection within settle delay + request time; no focus change in the browser | ◐ verified in a WPF text box and, since 2026-11-26, in Edge, Chrome and Firefox on the CI runner: double-click, triple-click, drag and Ctrl+A each produce the card with the selected text (UI tests 5 and 11–15). Card position and the browser keeping focus are not checked |
| 2 | Click to clear the selection | Card hides immediately on mouse-down; no reopen on mouse-up (probe → no selection) | ⬜ (session: unit) |
| 3 | Select A, then B while A is loading | A's request cancelled, card shows loading for B, A's late result never rendered | ⬜ (session + coordinator: unit) |
| 4 | Dismiss while a request is pending | Card stays hidden when the response arrives | ⬜ (unit) |
| 5 | Re-select the same text after dismissing | Stays quiet until a different selection or a clear | ◐ repeating a selection is served from the cache; the dismiss-then-reselect path is unit-tested only |
| 6 | Move the pointer into the card | Card remains; source keeps focus and selection | ⬜ |
| 7 | Copy from the card | Clipboard updated; source window not activated | ⬜ |
| 8 | Long translation | Card capped at 45 % of work area height, wheel scrolls the card (hook forwards wheel), source does not scroll | ⬜ |
| 9 | Selection at screen edges | Flips below when no room above; clamped to the work area with 8 px margin | ⬜ (unit) |
| 10 | Second monitor with different DPI | Card appears on the selection's monitor, sized with that monitor's scale; negative coordinates handled | ⬜ (unit for math) |
| 11 | Move / minimize the source window | Card follows on move (WinEvent → refresh); minimize hides it | ◐ minimize hides it (`source minimized` → `hidden: SourceWindowLost`); the move-follow refresh not exercised |
| 12 | Switch to another app (Alt+Tab or click) | Card hides; returning does not reopen it | ✅ `hidden: ForegroundChanged` when another window took the foreground |
| 13 | Unsupported control (no TextPattern) | Nothing appears; Diagnostics → Applications shows "unsupported", log line without text | ✅ `probe unsupported: no text pattern near focus (control type 50000, …)`, no text in the log, no card |
| 14 | Stalled UIA provider | Probe watchdog (2.5 s) reports failure, observer thread recovers on next request, no replacement threads | ⬜ |
| 15 | HTTP 412 / timeout / invalid JSON / empty output | Error card with concise message + route (direct/relay); no HTML rendered; no retry loop | ✅ HTTP 412 mapped to `Request rejected by the service (HTTP 412).` with the relay hint and both action buttons; other status codes are unit-tested |
| 16 | Start a second instance | Second process exits; running instance opens Settings | ✅ second process exited with code 0 after 234 ms; log `second instance signalled; opening settings`; Settings window visible |
| 17 | Exit from tray | Hooks, WinEvent hooks, UIA handlers, HttpClient, windows and tray icon released; process ends | ◐ tray menu → Exit: log `exit requested` → `observer thread stopped` → `stopped` in 2.1 s, process gone; individual handle release not inspected |
| 18 | 200 repeated selections | Stable handle count and memory (single reused popup window, bounded caches/logs) | ✅ handles 569 → 576, USER 64 → 66, GDI flat at 17, private bytes +0.5 MB over 200 selections (cache-served, see above) |

## Applications

| App | Mechanism expected | Status |
|---|---|---|
| Self-built WPF text box (test harness) | UIA TextPattern on the read-only editor | ✅ selection read in 2.4–12.7 ms, card and upstream request confirmed |
| Chrome / Edge (web page) | UIA TextPattern on the document (Chromium exposes it when a screen reader / UIA client is active; first probe may be slow) | ✅ on the `windows-latest` CI runner (2026-11-26, run 45806960089): double-click (word), triple-click (line), drag and Ctrl+A in Edge and Chrome, read from the document's TextPattern. Cold Edge (the test sends no UIA request to Edge, so the app is the only client): card on the first triple click. **First selection in a fresh Chrome:** before the probe retry it produced no card in 1 of 4 runs, in two CI runs; the probe found only the window pane (`unsupported: no text pattern near focus (control type 50033, Chrome_WidgetWin_1)`) because the page's accessibility tree was not ready yet. Since an unsupported or failed automatic probe is retried once after 400 ms, the first selection worked 12 of 12 times in Chrome (and in Edge and Firefox) over three CI runs (45810562350, 45812145114, 45813463450). The run that prints the probe log (45813463450) shows no retry, so the original failure did not occur there: the retry path is covered by unit tests, not yet by a real browser. The warm-up output in each run says when a retry happened. Real web pages not tested |
| Firefox | UIA TextPattern (Firefox's UIA support improved in 2024–2025 builds; older builds may be unsupported) | ✅ on the CI runner (2026-11-26, run 45806960089): all four gestures, and the first selection in a freshly started Firefox worked in 4 of 4 runs. In an earlier run, where Firefox windows from earlier tests were still open, the first UIA calls timed out (`GetFocusedElement failed (0x80131505)`). Real web pages not tested |
| Notepad | TextPattern on the editor control | ✅ classic Notepad (Edit control) on the CI runner: double-click, drag and Shift+End; triple-click does not exist in the Edit control (n/a). The Windows 11 Store Notepad is not on the runner and is not tested |
| Word | TextPattern on the document body | ⬜ not tested |
| VS Code / Electron editors | Monaco exposes a limited UIA text surface; may read as unsupported → use Alt+Q fallback | ⬜ not tested |
| Adobe Acrobat / PDF viewers | Highly variable; Edge PDF viewer behaves like a web page | ⬜ not tested |
| Single-line edit fields | Skipped by default (policy), unless enabled in Settings | ⬜ (policy: unit) |
| Password fields | Always skipped | ⬜ (policy: unit) |
| Console windows, games, custom-drawn controls | Unsupported; quiet failure | ✅ confirmed for a control without a text pattern |

## How to run the checks on Windows

1. `tools\run.cmd` — complete the welcome window (it has a demo paragraph for the first selection).
2. Open **Diagnostics…** from the tray and keep it on a second monitor if possible; it updates live.
3. Walk through the scenarios above; note p50/p95 from the Timings section after ~20 selections.
4. For scenario 14 use a UIA-heavy app under load, or temporarily set `ConnectionTimeout` lower in
   `UiaObserver` and probe a frozen window (`Suspend` it with Process Explorer).
5. For scenario 18 watch *Handles*, *USER Objects*, *GDI Objects* and *Private Bytes* in Task Manager
   / Process Explorer.