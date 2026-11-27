# Bili Index Translate — Desktop

A quiet reading companion for Windows. Select text in another program; a small card with the
Chinese translation appears above the selection and goes away when you clear the selection, click
elsewhere, or press Esc. No visible window otherwise, no shortcut needed for normal use.

Built fresh in C# / .NET 10 (WPF for the card, UI Automation for reading selections).

> **Status.** The code compiles cleanly on Windows (0 warnings) and the platform-independent core —
> state machine, placement math, translation client, vision and study-card parsers, cache, settings —
> is covered by 266 passing unit tests. CI builds and tests every push and pull request on
> `windows-latest`. On a Windows 11 desktop the app starts, registers its hotkeys, enforces a single
> instance, opens Settings and exits cleanly from the tray. Selection reading, the popup card, the
> error card, cache hits, foreground and minimize dismissal and the unsupported-control path have
> been walked through against a local mock endpoint; the snip overlay, SAPI TTS and Chromium itself
> have not. `docs/TESTING.md` lists what is verified and what is still open.

## Run

Requirements: Windows 10 1809+ / Windows 11 (x64). The installer and the self-contained zip bring their own
runtime; the framework-dependent zip needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
To build from source you need the .NET 10 SDK.

| What | How |
|---|---|
| **Installed version** (recommended) | Run `BiliIndexTranslate.Desktop-win-Setup.exe` from the [Releases](https://github.com/sundtfbekasmjuvh/bili-index-translate-study/releases) page. It installs for the current user only (no administrator rights) into `%LocalAppData%\BiliIndexTranslate.Desktop`, adds Start menu and desktop shortcuts, starts the app, and updates itself (see [Upgrading](#upgrading)). Uninstall it in Windows Settings → Apps. |
| Portable version (no installer) | Unzip `BiliIndexTranslate-Desktop-win-x64-selfcontained-vX.Y.Z.zip` (or the lighter framework-dependent `BiliIndexTranslate-Desktop-win-x64-vX.Y.Z.zip`) and run `BiliIndexTranslate.exe`. It never checks for or downloads updates. |
| Prebuilt (framework-dependent) | `publish\BiliIndexTranslate.exe` |
| Build and run from source | `tools\run.cmd` (add `--welcome` to force the welcome window) |
| Build a release folder | `tools\build.cmd` → `publish\` (runs the tests first) |
| Self-contained single exe (no runtime install) | `tools\build-selfcontained.cmd` → `publish-sc\` (~139 MB) |
| Unit tests | `tools\test.cmd` or `dotnet test tests\BiliIndexTranslate.Core.Tests` (a bare `dotnet test` on the solution would also start the UI tests) |
| UI tests (select → streamed card → Esc; needs a Windows desktop) | `tools\test-ui.cmd`, see [docs/TESTING.md](docs/TESTING.md#ui-tests-flaui) |

If the settings file cannot be written, the Settings window says so and stays open, and a
background save reports it once through a tray notice; [docs/TESTING.md](docs/TESTING.md) describes
one cause (a checkout under a directory with a Low integrity label).

The app lives in the notification area. Right-click the icon for: automatic translation on/off,
"Don't translate in *app*", translate clipboard text, Settings, Diagnostics, Vocabulary, Welcome, Exit (and, while a
downloaded update is waiting, Restart and update).
A second instance does not start twice; it opens the Settings window of the running one.
`BiliIndexTranslate.exe --diagnostics` opens the Diagnostics window right after start, and `--vocabulary` opens the Vocabulary window.

**Read aloud.** The speaker button on the card reads the original text (never the translation); Shift+click reads it slowly. The voice language comes from the Source language setting, or is guessed from the text when that is "auto" (short texts and single words may be guessed wrong). It uses the voices installed in Windows, and nothing is ever spoken automatically.

**Diagnostics bundle.** The Diagnostics window has an **Export diagnostics…** button that saves a ZIP for bug
reports: `report.txt` (what "Copy report" gives), `diagnostics.log` (last 2 MB, only if file logging is on and
the file exists), `crash.log` (last 512 KB, only if it exists), `system.txt` (app version, installed or portable,
Windows and .NET versions, architecture, languages, high contrast, light/dark, monitors, uptime, data directory)
and `settings.json` (redacted). It never contains selected text, translations, the API key, URL paths or queries
(only `scheme://host`), header values, the request body, glossary content, the user name or the machine name:
the profile path, user name and machine name are replaced by `%USERPROFILE%`, `%USERNAME%` and `%COMPUTERNAME%`,
and so is any other folder name under `X:\Users\` (such as the short `JOHNSM~1` form that temporary paths use).
The file is saved only where you choose in the Save dialog; nothing is uploaded.

## Using it

1. Select text with the mouse (or Shift+arrows / Ctrl+A) in a browser, editor, or document.
2. After the selection settles (~250 ms), the card appears above the first selected line
   (below if there is no room), translated into Simplified Chinese by default.
3. Clear the selection, click anywhere outside the card, or press Esc to dismiss it.
   The same selection does not reopen the card until you select something else.
4. Hover the card for controls: show original text, copy, keep open (pin), close. Click **`学习卡片`**
   in the card footer to open the ephemeral companion Language Study Card beside the translation
   (progressive SSE streaming, `9B深度` ↔ `⚡ 2B极速` model toggle, normal + `0.7x` slow shadowing
   pronunciation via Windows SAPI, IPA/reading, register badge, synonym/usage nuance,
   etymology/mnemonic, collocations, bilingual example, sentence grammar note, pageable key-vocab
   flashcards, and `自测翻转` active recall).
   Ctrl + mouse wheel over a card changes the text size (remembered). The star on a card adds it to the
   vocabulary book (see below).

Optional fallback: `Alt+Q` (configurable, can be disabled) asks for a translation on demand. If
UI Automation cannot read the selection, this — and only this — sends a single Ctrl+C and
translates the copied text, then puts your previous clipboard text back (kept out of Windows
clipboard history and the cloud clipboard; non-text content is left untouched). Automatic mode never
touches the clipboard.

### Screen snip: how the image is read

The tray menu and `Alt+Shift+S` snip a screen region (a clipboard image goes the same way). The setting
**Image reading** (Settings → Service) decides how the picture becomes a translation:

* **Automatic** (default) – with an OpenAI-compatible endpoint the image goes to the cloud vision
  model, as before. With a DeepLX or custom endpoint the text is read on this PC and only that text is
  translated, so snip works with every API format.
* **Always local OCR** – every format reads the image on this PC first (Windows' built-in
  `Windows.Media.Ocr`, offline), then translates the recognized text. The screenshot is not uploaded.
* **Always cloud vision model** – the image goes to the vision model. With DeepLX or a custom API the
  card says it is not supported and suggests Automatic or Local OCR; nothing is sent.

Local OCR recognizes the languages whose **OCR language packs** are installed in Windows (it uses your
Windows profile languages). If nothing is recognized, or no OCR engine can be created, the card says
so and points to Windows Settings → Time & language → Language & region, where you add the language
(with its OCR option). Complex layouts, stylized text and game HUDs usually read worse than with the
cloud vision model. The card shows the recognized text as the original and the translation below it,
with the caption "local OCR".

**Recognize screen text and copy** (tray menu) snips a region, recognizes the text locally and copies
it to the clipboard; the tray shows "Copied N characters" once, or the reason when no text was found.
It works with any API format and sends no request at all.

## Accessibility

When Windows high contrast is on, the card and the app's windows use the system's high-contrast colors and
follow the setting while the app runs; hovered and pressed buttons use the Highlight colors, and the screen snip overlay draws with system colors too. Screen readers announce the final translation (and error messages)
when it appears; text still streaming in is not announced.

## Settings (tray → Settings…)

* **Reading** – automatic translation, target language (简体中文 / 繁體中文 / English / 日本語 / 한국어),
  text size, light/dark/system theme, editable-field policy (default: skip single-line inputs such
  as address bars; multi-line editors are translated), show original by default, interface language
  (English or 简体中文; "Auto" follows the Windows display language). A change applies to open
  windows and the tray menu as soon as it is saved. Error details from the translation core and
  the diagnostics log stay in English.
* **Service** – API format (OpenAI-compatible by default, DeepLX-compatible, or a custom JSON template, see below),
  endpoint (base URL *or* full `/chat/completions` URL; the suffix is never
  duplicated), API key (stored with DPAPI for your Windows account), model
  (`Index-Translate-2B` / `9B` / `35B-A3B`, default 9B, free text for other OpenAI-compatible
  servers), relay toggle. **Save and test** saves what is on screen and tests exactly that.
  HTTP 412 from the direct endpoint is shown as a rejection with a hint about the relay; the app
  never switches routes by itself. **Image reading** (Automatic / Always local OCR / Always cloud
  vision model) sits below the vision model; the `visionMode` field of `settings.json` holds it, and
  a file without the field or with an unknown value means Automatic.
* **Applications** – per-process exclusions (also reachable from the tray menu).
* **Fallback shortcut** – enable/disable, change the key. Registration conflicts are reported in
  the tray and in Settings; automatic mode is unaffected.
* **Startup and updates** – "Start with Windows" (off by default; see below), "Check for updates
  automatically" (on by default, installed version only), the current version and a **Check for
  updates** button that reports the result in the window. In the portable version the update
  controls are disabled with a note.
* **Advanced** – settle delay (120–800 ms), UI Automation selection events on/off, diagnostics file
  logging.

**Start with Windows.** Ticking the box writes the value `BiliIndexTranslate` (the quoted path of the
running exe) under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`; clearing it deletes the value.
The state is read from the registry each time Settings opens and is not kept in `settings.json`. A
value that points to another exe (a portable copy, say) shows as not ticked; ticking it rewrites the
value with this exe's path. The installed exe lives in `...\BiliIndexTranslate.Desktop\current\`, a
path that does not change when the app updates. Uninstalling removes the value.

**DeepLX format.** The endpoint is the complete translate URL and is used as entered, for example
`https://host/translate?token={apiKey}` or `http://localhost:1188/translate`. `{apiKey}` is replaced
with the API key; without the placeholder the key is sent as a Bearer token, and an empty key sends
no credential. The request body is
`{"text", "source_lang", "target_lang"}` and the translation is read from `data`; an HTTP error or
a non-200 `code` in the body is shown as an error. This format only translates selected text: the
glossary and the model choices have no effect, and Study Card reports that it needs an
OpenAI-compatible endpoint without sending a request. Screen snip reads the image with the local
OCR instead (setting **Image reading**, see above) and translates the recognized text. With the relay enabled, the relay URL must
be the complete relayed translate URL.

**Custom format.** Any service that answers with JSON can be described by a template instead of by
code: the endpoint (it may carry placeholders), the method (`POST` or `GET`), request headers (one
`Name: value` per line), a request body, and a result path that says where the translation is in
the reply (`data`, `translations[0].text`); an optional error path names where the service puts its
error message. Placeholders: `{text}`, `{texts}` (the text as a JSON array, body only), `{apiKey}`,
`{target}` (`zh-Hans`, `en`), `{targetLang}` / `{targetLangLower}` (`ZH` / `zh`). Values are
URL-encoded in the endpoint and in form bodies, JSON-escaped in JSON bodies and stripped of line
breaks in headers. Presets for the DeepL API (Free / Pro) and LibreTranslate fill in the form. A
template that cannot work is rejected when saving. Like DeepLX, a custom API only translates
selected text.

Settings file: `%LocalAppData%\BiliIndexTranslate\settings.json`. Malformed files are backed up as
`settings.json.bad` and replaced with defaults; older files with a plain-text key are migrated.

## Vocabulary and Anki

Both cards have a star. On the selection card (hover the card; shown once the translation is complete) it
saves the **sentence**: the original text and its translation. On the Study card it saves the **word card** being
shown (term, reading, part of speech, register, meaning, nuance, memory aid, collocations, example and the card's
source language). Click a filled star again to remove the entry. The star is the only way anything gets saved:
nothing is recorded automatically, and selecting or translating text never writes to the book.

**Where it lives.** `vocabulary.json` in the data directory, next to `settings.json`
(`%LocalAppData%\BiliIndexTranslate\`). It is a plain, readable JSON file: not encrypted, never uploaded, and not
part of the diagnostics bundle. Delete the file (or use **Clear all** in the Vocabulary window) to erase it.
The app log only records how many entries there are, never their text.

**Vocabulary window.** Tray menu → **Vocabulary…** (or start with `--vocabulary`). Search (text, translation, meaning),
select entries and **Delete selected** (or press Delete), **Clear all…** (asks first). It updates when you click a star on a card.

**Export to Anki.** **Export to Anki…** saves the whole book as a text file
(`BiliIndexTranslate-anki-<date>.txt`; UTF-8, tab-separated, HTML on, with Anki file headers). In Anki 2.1.54 or newer choose
**File → Import** and pick the file. The deck **Bili Index Translate** is created when it does not exist, and notes get
tags (`bili-index-translate`, `word` or `sentence`, `lang-<source language>`). Sentences become Front = original,
Back = translation; words become Front = term, Back = reading, part of speech, meaning, nuance, example, memory aid and
collocations. Every note carries a stable GUID, so importing a newer export again updates the same notes instead of adding duplicates.

**Newer file.** If `vocabulary.json` was written by a newer version of the app (a higher `schemaVersion`), it is opened
read-only: you can view and export it, but stars and delete buttons do nothing, so nothing a newer version added is lost.
A file that cannot be parsed is moved to `vocabulary.json.bad-<time>` and the book starts empty.

## Privacy

Only the selected text and request metadata (model, target language) are sent to the configured
translation endpoint. No telemetry, no history, no persisted cache; the one thing the app writes about
your reading is the vocabulary book, and only when you click a star (see "Vocabulary and Anki": a local file, never
uploaded). Diagnostics never contain selected text, translations, vocabulary entries or keys (only lengths and a short fingerprint).

Screen snip with local OCR (Automatic with a DeepLX or custom endpoint, or Always local OCR) does not
upload the screenshot: the image is recognized in memory by Windows and only the recognized text is
sent to the translation endpoint. **Recognize screen text and copy** sends no request at all. Neither
the screenshot nor the recognized text is written to a file or to the diagnostics log (lengths only).
With the cloud vision model (OpenAI-compatible endpoint, Automatic or Always cloud) the screenshot is
sent to that endpoint, as before.

The installed version also contacts GitHub on its own: by default it regularly asks GitHub
(`api.github.com`, and the download address of the Releases assets) whether a newer version exists and
downloads it. Those requests carry no personal information and no usage data (no selected text, no
settings, no identifiers; no access token is used). You can turn this off in Settings → "Check for
updates automatically", and "Check for updates" then only runs when you press it. The portable
(zip) version never sends such requests.

## Project layout

```
src/BiliIndexTranslate.Core   platform-independent: session state machine, geometry/placement,
                              translation client, cache, settings, diagnostics (net10.0)
src/BiliIndexTranslate.App    WPF app: UIA observer, input hooks, popup, tray, settings UI, local OCR (net10.0-windows10.0.19041.0, runs on Windows 10 1809+)
tests/…Core.Tests             xunit tests for Core (run on any OS)
tools/                        run.cmd, build.cmd, build-selfcontained.cmd, test.cmd, make-icon.py
docs/ARCHITECTURE.md          boundaries, threads, state model, race handling
docs/TESTING.md               18 validation scenarios, app compatibility table, measurements
```

## Upgrading

**Installed version.** It updates itself: about a minute after start and then every 24 hours the app
checks the published GitHub releases of this repository, downloads a newer version in the background
(only the changed parts when a delta package exists), and tells you once in the tray. The tray menu
then offers **Restart and update**; if you simply exit the app from the tray, the update is installed
right after it closes, and the next start runs the new version. The **Check for updates** button in
Settings does the same on demand. The app is installed in `%LocalAppData%\BiliIndexTranslate.Desktop`,
apart from the settings and logs in `%LocalAppData%\BiliIndexTranslate`, so updating and uninstalling
leave your settings alone. To update by hand, run the newer `Setup.exe` over the old install.

**Portable version.** Replace the exe (or the `publish` folder when you rebuild). It does not
check for updates; download new versions from the Releases page.

In both cases settings and the encrypted key are kept; the schema is versioned and unknown fields
are ignored. Apart from the update packages of the installed version, nothing else is written to disk
unless file logging is on.

**Publishing (maintainers).** The release workflow builds the zips, the Velopack
files (`*-Setup.exe`, `*-full.nupkg`, a `*-delta.nupkg` when there is an earlier Velopack release,
`releases.win.json`) and `SHA256SUMS.txt` (checksums of the zips, the installer and the packages) into one
**draft** release. Installed apps read only *published* releases, so an
update reaches users only after the draft has been reviewed and published.
