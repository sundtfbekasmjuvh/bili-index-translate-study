# Builds the three release ZIPs and SHA256SUMS.txt into an empty output folder.
# The extension ZIP holds the committed bili-index-translate/ folder; each desktop ZIP holds one
# single-file BiliIndexTranslate.exe. All ZIPs include the committed LICENSE and NOTICE files.
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
$legalFiles = @("LICENSE", "NOTICE")

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

$dirty = @(git -C $root status --porcelain -- "bili-index-translate" "BiliIndexTranslate.Desktop" "LICENSE" "NOTICE")
if ($LASTEXITCODE -ne 0) { throw "Could not inspect the release source status" }
if ($dirty.Count -gt 0) {
  if ($Tag) { throw "Uncommitted release source or license changes; build a release from a clean checkout" }
  Write-Warning "Uncommitted changes found: extension and license files are taken from HEAD, the desktop build from the working tree"
}

$trackedLegal = @(git -C $root ls-tree --name-only HEAD -- @legalFiles)
if ($LASTEXITCODE -ne 0 -or @(Compare-Object $legalFiles $trackedLegal).Count -gt 0) {
  throw "LICENSE and NOTICE must both be committed before packaging"
}

if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
if ((Test-Path -LiteralPath $OutDir) -and (Get-ChildItem -LiteralPath $OutDir -Force | Select-Object -First 1)) {
  throw "Output folder '$OutDir' is not empty"
}
$work = Join-Path $OutDir "work"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$legalArchive = Join-Path $work "legal.zip"
$legalDir = Join-Path $work "legal"
Invoke-Checked "git" (@("-C", $root, "-c", "core.autocrlf=false", "-c", "core.eol=lf", "archive", "--format=zip", "-o", $legalArchive, "HEAD", "--") + $legalFiles)
[IO.Compression.ZipFile]::ExtractToDirectory($legalArchive, $legalDir)

function Add-LegalFiles([IO.Compression.ZipArchive]$Zip, [string]$Prefix = "") {
  foreach ($file in $legalFiles) {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Zip, (Join-Path $legalDir $file), "$Prefix$file", [IO.Compression.CompressionLevel]::Optimal)
  }
}

function Assert-LegalFiles([string]$ZipPath, [string]$Prefix = "") {
  $zip = [IO.Compression.ZipFile]::OpenRead($ZipPath)
  try {
    foreach ($file in $legalFiles) {
      $entry = $zip.GetEntry("$Prefix$file")
      if ($null -eq $entry -or $entry.Length -eq 0) { throw "$ZipPath is missing a non-empty $Prefix$file" }
      $reader = [IO.StreamReader]::new($entry.Open())
      try { $actual = $reader.ReadToEnd() } finally { $reader.Dispose() }
      if ($actual -cne [IO.File]::ReadAllText((Join-Path $legalDir $file))) { throw "$ZipPath contains an altered $Prefix$file" }
    }
  } finally {
    $zip.Dispose()
  }
}

function New-ExeZip([string]$ZipPath, [string]$Exe) {
  $zip = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
  try {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $Exe, "BiliIndexTranslate.exe", [IO.Compression.CompressionLevel]::Optimal)
    Add-LegalFiles $zip
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
  @($manifest.declarative_net_request.rule_resources | ForEach-Object { $_.path })
foreach ($file in $referenced) {
  if ($tracked -notcontains "bili-index-translate/$file") { throw "manifest.json references '$file', which is not committed" }
}
$extZip = Join-Path $OutDir "bili-index-translate-chrome-v$version.zip"
Invoke-Checked "git" @("-C", $root, "-c", "core.autocrlf=false", "-c", "core.eol=lf", "archive", "--format=zip", "-o", $extZip, "HEAD", "--", "bili-index-translate")
$zip = [IO.Compression.ZipFile]::Open($extZip, [IO.Compression.ZipArchiveMode]::Update)
try { Add-LegalFiles $zip "bili-index-translate/" } finally { $zip.Dispose() }

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

$actualExt = @(Get-ZipEntries $extZip | Sort-Object)
$expectedExt = @(@("bili-index-translate/") + $tracked + @($legalFiles | ForEach-Object { "bili-index-translate/$_" }) | Sort-Object)
if (Compare-Object $actualExt $expectedExt) { throw "Extension ZIP entries differ from the committed files" }
Assert-LegalFiles $extZip "bili-index-translate/"
foreach ($variant in $variants) {
  $zipPath = Join-Path $OutDir $variant.Zip
  $entries = @(Get-ZipEntries $zipPath)
  if (Compare-Object $entries (@("BiliIndexTranslate.exe") + $legalFiles)) { throw "$($variant.Zip) must contain BiliIndexTranslate.exe, LICENSE and NOTICE" }
  Assert-LegalFiles $zipPath
}
$assets = @(Get-ChildItem -LiteralPath $OutDir -File -Filter "*.zip" | Sort-Object Name)
$sums = $assets | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + "  " + $_.Name }
[IO.File]::WriteAllText((Join-Path $OutDir "SHA256SUMS.txt"), (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

$assets | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
"SHA256SUMS.txt written for $($assets.Count) assets (version $version)"
