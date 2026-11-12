# Builds the three release ZIPs and SHA256SUMS.txt into an empty output folder.
# The extension ZIP holds the committed bili-index-translate/ folder; each desktop ZIP holds one
# single-file BiliIndexTranslate.exe (framework-dependent, and self-contained with compression).
[CmdletBinding()]
param(
  [string]$Tag = "",
  [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$root = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$extDir = Join-Path $root "bili-index-translate"
$desktopDir = Join-Path $root "BiliIndexTranslate.Desktop"
$appProject = Join-Path $desktopDir "src/BiliIndexTranslate.App/BiliIndexTranslate.App.csproj"

function Invoke-Checked([string]$File, [string[]]$Arguments) {
  & $File @Arguments
  if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

$manifest = Get-Content -LiteralPath (Join-Path $extDir "manifest.json") -Raw | ConvertFrom-Json
$version = [string]$manifest.version
$propsVersion = [string](Select-Xml -LiteralPath (Join-Path $desktopDir "Directory.Build.props") -XPath "//Version").Node.InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "manifest.json version '$version' is not MAJOR.MINOR.PATCH" }
if ($propsVersion -ne $version) { throw "Directory.Build.props Version '$propsVersion' does not match manifest.json '$version'" }
if ($Tag -and $Tag -ne "v$version") { throw "Tag '$Tag' does not match version v$version" }

$dirty = @(git -C $root status --porcelain -- "bili-index-translate" "BiliIndexTranslate.Desktop")
if ($dirty.Count -gt 0) {
  if ($Tag) { throw "Uncommitted changes under bili-index-translate/ or BiliIndexTranslate.Desktop/; build a release from a clean checkout" }
  Write-Warning "Uncommitted changes found: the extension ZIP is taken from HEAD, the desktop build from the working tree"
}

if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
if ((Test-Path -LiteralPath $OutDir) -and (Get-ChildItem -LiteralPath $OutDir -Force | Select-Object -First 1)) {
  throw "Output folder '$OutDir' is not empty"
}
$work = Join-Path $OutDir "work"
New-Item -ItemType Directory -Force -Path $work | Out-Null

function New-ExeZip([string]$ZipPath, [string]$Exe) {
  $zip = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
  try {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $Exe, "BiliIndexTranslate.exe", [IO.Compression.CompressionLevel]::Optimal)
  } finally {
    $zip.Dispose()
  }
}

function Get-ZipEntries([string]$ZipPath) {
  $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
  try { return @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
}

# Extension: the committed files at HEAD with LF endings, exactly as in the repository. Files Chromium
# writes into the folder (such as _metadata/) and Windows CRLF checkouts do not leak into the ZIP.
$tracked = @(git -C $root ls-tree -r --name-only HEAD -- "bili-index-translate")
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw "git ls-tree returned no extension files" }
$referenced = @($manifest.background.service_worker, $manifest.options_ui.page) +
  @($manifest.content_scripts | ForEach-Object { $_.js + $_.css }) +
  @($manifest.declarative_net_request.rule_resources | ForEach-Object { $_.path }) +
  @($manifest.icons.PSObject.Properties | ForEach-Object { $_.Value }) +
  @($manifest.action.default_icon.PSObject.Properties | ForEach-Object { $_.Value }) +
  @("_locales/$($manifest.default_locale)/messages.json")
foreach ($file in $referenced) {
  if ($tracked -notcontains "bili-index-translate/$file") { throw "manifest.json references '$file', which is not committed" }
}
$extZip = Join-Path $OutDir "bili-index-translate-chrome-v$version.zip"
Invoke-Checked "git" @("-C", $root, "-c", "core.autocrlf=false", "-c", "core.eol=lf", "archive", "--format=zip", "-o", $extZip, "HEAD", "--", "bili-index-translate")

$variants = @(
  [pscustomobject]@{
    Zip = "BiliIndexTranslate-Desktop-win-x64-v$version.zip"
    Folder = "framework-dependent"
    Args = @("--self-contained", "false")
  },
  [pscustomobject]@{
    Zip = "BiliIndexTranslate-Desktop-win-x64-selfcontained-v$version.zip"
    Folder = "self-contained"
    Args = @("--self-contained", "true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true")
  }
)
foreach ($variant in $variants) {
  $publishDir = Join-Path $work $variant.Folder
  Invoke-Checked "dotnet" (@("publish", $appProject, "-c", "Release", "-r", "win-x64", "-p:PublishSingleFile=true", "-o", $publishDir, "--nologo") + $variant.Args)
  $exe = Join-Path $publishDir "BiliIndexTranslate.exe"
  $fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
  if ($fileVersion -ne "$version.0") { throw "$($variant.Folder) exe has FileVersion '$fileVersion', expected '$version.0'" }
  New-ExeZip (Join-Path $OutDir $variant.Zip) $exe
}

# git archive also writes an entry for every folder; only the files are compared.
$actualExt = @(Get-ZipEntries $extZip | Where-Object { -not $_.EndsWith("/") } | Sort-Object)
$expectedExt = @($tracked | Sort-Object)
if (Compare-Object $actualExt $expectedExt) { throw "Extension ZIP entries differ from the committed files" }
foreach ($variant in $variants) {
  $entries = @(Get-ZipEntries (Join-Path $OutDir $variant.Zip))
  if ($entries.Count -ne 1 -or $entries[0] -ne "BiliIndexTranslate.exe") { throw "$($variant.Zip) must contain only BiliIndexTranslate.exe" }
}
$assets = @(Get-ChildItem -LiteralPath $OutDir -File -Filter "*.zip" | Sort-Object Name)
$sums = $assets | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + $_.Name }
[IO.File]::WriteAllText((Join-Path $OutDir "SHA256SUMS.txt"), (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

$assets | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
"SHA256SUMS.txt written for $($assets.Count) assets (version $version)"