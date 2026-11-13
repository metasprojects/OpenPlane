# Builds the OpenPlane release files into out/release:
#   OpenPlane-<version>-win-x64.zip   the app (self-contained: players don't need .NET), with the mod and Blueprinter inside
#   OpenPlane-mod.zip                 the mod alone, laid out for BepInEx (unzip into the game folder; needs Blueprinter).
#   OpenPlane-nomm.zip                the same files with no folders, for NOMM: it unpacks a mod into
#                                     BepInEx/plugins/<NOMNOM id> itself. Same name every release: NOMNOM picks the asset
#                                     by name (releaseAssetName).
#   Meta-OpenPlane-<version>.zip      the Thunderstore package (mod + Blueprinter, which isn't on Thunderstore)
#   OpenPlane-starter-planes.zip      the four starter planes as a plane pack (the same ones the app adds)
#   com.meta.openplane.json           the NOMNOM manifest for this release (hash filled in), for the NOMNOM pull request
#   SHA256SUMS.txt                    checksums of the zips
# Needs the .NET 10 SDK, and the game's DLLs in lib/game plus Blueprinter in lib/blueprinter (see README).
# Every file that goes into a zip is checked first: no .pdb files, no build paths from this computer, the licence texts
# of everything shipped in licenses/. The script stops (and makes no zips) when a check fails.
# Usage: powershell -ExecutionPolicy Bypass -File tools/package.ps1 [-Dotnet <path to dotnet>] [-SkipTests] [-AllowMissingNotices]
#   -AllowMissingNotices  build even when a licence text can't be found (for test builds only; never for a release)
param(
    [string]$Dotnet = "dotnet",
    [switch]$SkipTests,
    [switch]$AllowMissingNotices,
    # Where the releases are published (GitHub owner/repository): used in the NOMNOM manifest and Thunderstore website_url.
    [string]$Repo = "metasprojects/OpenPlane",
    # The Nuclear Option version this release was tested on.
    [string]$GameVersion = "0.34.6"
)
$ErrorActionPreference = "Stop"
# UTF-8 without a byte-order mark (Windows PowerShell's -Encoding utf8 adds one; Thunderstore and JSON readers reject it).
function Write-Utf8([Parameter(Mandatory)][string]$Path, [Parameter(ValueFromPipeline)][string]$Text) {
    begin { $all = New-Object System.Text.StringBuilder }
    process { [void]$all.AppendLine($Text) }
    end { [System.IO.File]::WriteAllText($Path, $all.ToString(), (New-Object System.Text.UTF8Encoding($false))) }
}
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "No <Version> in Directory.Build.props" }

$gameDll = Join-Path $root "lib\game\Assembly-CSharp.dll"
if (-not (Test-Path $gameDll)) { throw "lib\game is missing the game's DLLs, so the mod can't be built. See README 'Building'." }
if (-not (Get-ChildItem (Join-Path $root "lib\blueprinter") -Filter "Blueprinter*.dll" -ErrorAction SilentlyContinue)) {
    throw "lib\blueprinter has no Blueprinter DLL. See README 'Building'."
}

if (-not $SkipTests) {
    & $Dotnet test OpenPlane.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was packaged." }
}

$out = Join-Path $root "out\release"
$appName = "OpenPlane-$version-win-x64"
$appDir = Join-Path $out $appName
$modStage = Join-Path $out "mod-stage"
New-Item -ItemType Directory -Force $out | Out-Null
# Start clean: no zips, checksums or staging folders from an earlier run (of this or another version).
Get-ChildItem $out -File | Where-Object { $_.Extension -eq ".zip" -or $_.Name -eq "SHA256SUMS.txt" } | Remove-Item -Force
foreach ($d in @($appDir, $modStage, (Join-Path $out "ts-stage"), (Join-Path $out "starter-stage"))) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
Get-ChildItem $out -File -Filter "com.meta.openplane.json" -ErrorAction SilentlyContinue | Remove-Item -Force

# Build the mod on its own first: publishing with -r win-x64 would build it into a win-x64 subfolder, and the app
# copies the mod from the plain Release folder, so it must be fresh there.
& $Dotnet build src\OpenPlane.Mod\OpenPlane.Mod.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Building the mod failed." }

& $Dotnet publish src\OpenPlane.App\OpenPlane.App.csproj -c Release -r win-x64 --self-contained true -o $appDir
if ($LASTEXITCODE -ne 0) { throw "Publishing the app failed." }

foreach ($need in @("OpenPlane.exe", "mod\OpenPlane\OpenPlane.Mod.dll", "mod\OpenPlane\OpenPlane.Core.dll", "mod\Blueprinter\LICENSE.txt")) {
    if (-not (Test-Path (Join-Path $appDir $need))) { throw "The published app is missing $need." }
}
if (-not (Get-ChildItem (Join-Path $appDir "mod\Blueprinter") -Filter "Blueprinter*.dll")) { throw "The published app is missing Blueprinter." }
Copy-Item LICENSE, README.md, THIRD-PARTY-NOTICES.md, CHANGELOG.md $appDir

# Debug symbols stay on the build machine: they are not needed to run OpenPlane.
Get-ChildItem $appDir -Recurse -File -Include *.pdb | Remove-Item -Force

# ------------------------------------------------------------------ licence texts of everything shipped
$nuget = $env:NUGET_PACKAGES
if (-not $nuget) { $nuget = Join-Path $env:USERPROFILE ".nuget\packages" }
$deps = Get-Content (Join-Path $appDir "OpenPlane.deps.json") -Raw
function Get-PackageVersion([string]$name) {
    $m = [regex]::Match($deps, '"' + [regex]::Escape($name) + '/([0-9][^"]*)"')
    if ($m.Success) { return $m.Groups[1].Value } else { return $null }
}
$licenses = Join-Path $appDir "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
$index = New-Object System.Collections.Generic.List[string]
$index.Add("Licence texts of the third-party software shipped with OpenPlane $version (see THIRD-PARTY-NOTICES.md).")
$index.Add("OpenPlane itself is MIT-licensed: see LICENSE next to OpenPlane.exe.")
$index.Add("")
$missing = New-Object System.Collections.Generic.List[string]
function Add-Licence([string]$source, [string]$target, [string]$covers) {
    if ($source -and (Test-Path -LiteralPath $source)) {
        Copy-Item $source (Join-Path $licenses $target)
        $index.Add("$target" + ": " + $covers)
    } else {
        $missing.Add("$covers (looked for $source)")
    }
}
$netVer = Get-PackageVersion "runtimepack.Microsoft.NETCore.App.Runtime.win-x64"
$aspVer = Get-PackageVersion "runtimepack.Microsoft.AspNetCore.App.Runtime.win-x64"
$jsonVer = Get-PackageVersion "Newtonsoft.Json"
$photinoVer = Get-PackageVersion "Photino.NET"
$nativeVer = Get-PackageVersion "Photino.Native"
$net = Join-Path $nuget "microsoft.netcore.app.runtime.win-x64\$netVer"
$asp = Join-Path $nuget "microsoft.aspnetcore.app.runtime.win-x64\$aspVer"
Add-Licence (Join-Path $root "licenses\Apache-2.0.txt") "Photino-Apache-2.0.txt" "Photino.NET $photinoVer and Photino.Native $nativeVer by TryPhotino (Photino.NET.dll, Photino.Native.dll), Apache License 2.0"
$webview = $null
if (Test-Path (Join-Path $nuget "microsoft.web.webview2")) {
    $webview = Get-ChildItem (Join-Path $nuget "microsoft.web.webview2") -Recurse -File -Filter "LICENSE.txt" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
}
if ($webview) { $webview = $webview.FullName } else { $webview = Join-Path $root "licenses\WebView2-LICENSE.txt" }   # the same text, kept in the repo
Add-Licence $webview "WebView2-LICENSE.txt" "WebView2Loader.dll (Microsoft Edge WebView2 SDK, shipped inside Photino.Native)"
Add-Licence (Join-Path $net "LICENSE.TXT") "dotnet-runtime-LICENSE.txt" ".NET runtime $netVer (the runtime files next to OpenPlane.exe), MIT"
Add-Licence (Join-Path $net "THIRD-PARTY-NOTICES.TXT") "dotnet-runtime-THIRD-PARTY-NOTICES.txt" ".NET runtime $netVer, third-party notices"
Add-Licence (Join-Path $asp "LICENSE.txt") "aspnetcore-LICENSE.txt" "ASP.NET Core runtime $aspVer (Microsoft.AspNetCore.*.dll), MIT"
Add-Licence (Join-Path $asp "THIRD-PARTY-NOTICES.TXT") "aspnetcore-THIRD-PARTY-NOTICES.txt" "ASP.NET Core runtime $aspVer, third-party notices"
Add-Licence (Join-Path $nuget "newtonsoft.json\$jsonVer\LICENSE.md") "Newtonsoft.Json-LICENSE.md" "Newtonsoft.Json $jsonVer by James Newton-King (Newtonsoft.Json.dll), MIT"
Add-Licence (Join-Path $root "src\OpenPlane.App\wwwroot\vendor\three\LICENSE") "three.js-LICENSE.txt" "three.js (wwwroot/vendor/three), MIT"
Add-Licence (Join-Path $root "src\OpenPlane.App\ThirdParty\Blueprinter-LICENSE.txt") "Blueprinter-LICENSE.txt" "Blueprinter by nikkorap (mod/Blueprinter), MIT"
$index | Set-Content -Encoding utf8 (Join-Path $licenses "README.txt")
if ($missing.Count -gt 0) {
    $list = ($missing | ForEach-Object { "  - $_" }) -join [Environment]::NewLine
    if (-not $AllowMissingNotices) {
        throw ("These licence texts weren't found, so no release was made:" + [Environment]::NewLine + $list + [Environment]::NewLine +
               "Restore the package that carries each one (for WebView2: the Microsoft.Web.WebView2 NuGet package) and run this again.")
    }
    Write-Warning ("Missing licence texts (test build only, not for release):" + [Environment]::NewLine + $list)
}

# ------------------------------------------------------------------ the mod alone: BepInEx/plugins/OpenPlane/...
$modPlugins = Join-Path $modStage "BepInEx\plugins\OpenPlane"
New-Item -ItemType Directory -Force $modPlugins | Out-Null
Copy-Item (Join-Path $appDir "mod\OpenPlane\*.dll") $modPlugins
Copy-Item LICENSE (Join-Path $modPlugins "LICENSE.txt")
@"
OpenPlane mod $version for Nuclear Option (BepInEx 5).
Needs Blueprinter 2.1.1 or newer by nikkorap: https://github.com/nikkorap/NOBlueprinter-Releases
Unzip into the Nuclear Option folder (the one that holds NuclearOption.exe).
Put .oplane plane files in the game's planes folder:
  Windows:              %USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\OpenPlane\Aircraft
  Steam Deck and Linux: ~/.local/share/Steam/steamapps/compatdata/2168680/pfx/drive_c/users/steamuser/AppData/LocalLow/Shockfront/NuclearOption/OpenPlane/Aircraft
Steam Deck and Linux (Proton): add this to Nuclear Option's launch options in Steam once, so BepInEx loads:
  WINEDLLOVERRIDES="winhttp=n,b" %command%
A second planes folder can be set as ExtraAircraftFolder in BepInEx/config/com.meta.openplane.cfg.
Hosts (single-player, lobbies, dedicated servers) can limit OpenPlane planes in the same file, section [Multiplayer]:
  AllowOpenPlanePlanes, MaxThrustToWeight, MaxHardpoints (0 = no limit).
Everyone in a multiplayer game needs the same .oplane files.
The OpenPlane app (in the main OpenPlane download, for Windows) does all of this for you.
"@ | Set-Content -Encoding utf8 (Join-Path $modPlugins "README.txt")

# ------------------------------------------------------------------ the starter planes as a plane pack
# The same four planes the app's "Add starter planes" makes (fixed ids, so everyone's copies match in multiplayer).
# For players without the app (Steam Deck, servers): unzip into the planes folder.
& $Dotnet build src\OpenPlane.Cli\OpenPlane.Cli.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Building the command-line tool failed." }
$starterStage = Join-Path $out "starter-stage"
if (Test-Path $starterStage) { Remove-Item $starterStage -Recurse -Force }
& (Join-Path $root "src\OpenPlane.Cli\bin\Release\net10.0\openplane.exe") starters -o $starterStage | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Making the starter planes failed (or their flight check found a problem)." }
if ((Get-ChildItem $starterStage -Filter *.oplane).Count -ne 4) { throw "The starter pack should hold 4 planes." }

# ------------------------------------------------------------------ the Thunderstore package (r2modman / Thunderstore Mod Manager)
# Thunderstore puts plugins/<anything> in BepInEx/plugins/Meta-OpenPlane/. Blueprinter isn't on Thunderstore, so it is
# bundled (MIT, with its licence); BepInEx loads the newest copy if a player has another.
$tsStage = Join-Path $out "ts-stage"
if (Test-Path $tsStage) { Remove-Item $tsStage -Recurse -Force }
$tsPlugins = Join-Path $tsStage "plugins"
New-Item -ItemType Directory -Force (Join-Path $tsPlugins "OpenPlane"), (Join-Path $tsPlugins "Blueprinter") | Out-Null
Copy-Item (Join-Path $modPlugins "*") (Join-Path $tsPlugins "OpenPlane")
Copy-Item (Join-Path $appDir "mod\Blueprinter\*") (Join-Path $tsPlugins "Blueprinter")
Copy-Item (Join-Path $root "src\OpenPlane.App\Assets\openplane.png") (Join-Path $tsStage "icon.png")
$tsDescription = "Fly planes you converted from Flyout or built yourself in the OpenPlane app (.oplane files). Blueprinter add-on, MIT. Made with AI assistance."
if ($tsDescription.Length -gt 250) { throw "The Thunderstore description is over 250 characters." }
[ordered]@{
    name = "OpenPlane"
    version_number = $version
    website_url = "https://github.com/$Repo"
    description = $tsDescription
    dependencies = @("BepInEx-BepInExPack-5.4.2305")
} | ConvertTo-Json | Write-Utf8 (Join-Path $tsStage "manifest.json")
@"
# OpenPlane

The in-game part of [OpenPlane](https://github.com/$Repo): it loads the planes you convert from Flyout or build in the
OpenPlane app (``.oplane`` files) into Nuclear Option $GameVersion. Made by Meta, with AI assistance. MIT licence.

- Put ``.oplane`` files in ``%USERPROFILE%\AppData\LocalLow\Shockfront\NuclearOption\OpenPlane\Aircraft`` (the OpenPlane
  app does this for you), then start the game. Each plane is offered in the hangars that offer the stock plane it is
  built on.
- Everyone in a multiplayer game needs the same planes. Hosts can limit them in ``BepInEx/config/com.meta.openplane.cfg``
  (section ``[Multiplayer]``).
- Includes [Blueprinter](https://github.com/nikkorap/NOBlueprinter-Releases) by nikkorap (MIT), which OpenPlane builds on.
  While Blueprinter is installed, the game only shows multiplayer lobbies that use the same mods.
- Not affiliated with Shockfront Studios or Stonext Games. Converted Flyout planes belong to the people who built them.
"@ | Write-Utf8 (Join-Path $tsStage "README.md")
Copy-Item (Join-Path $root "CHANGELOG.md") (Join-Path $tsStage "CHANGELOG.md")

# ------------------------------------------------------------------ checks on every file that will be shipped
$problems = New-Object System.Collections.Generic.List[string]
foreach ($dir in @($appDir, $modStage, $tsStage, $starterStage)) {
    Get-ChildItem $dir -Recurse -File -Include *.pdb | ForEach-Object { $problems.Add("debug symbols: " + $_.FullName) }
}
# No path from this computer and no personal name, in any file, binaries included (ASCII/UTF-8 and UTF-16 text).
$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$rootNorm = (Resolve-Path $root).Path.TrimEnd([char]92)
$needles = New-Object System.Collections.Generic.List[string]
# Extra words to search for (names, user folders) come from an optional private file that is never committed:
# .local/private-strings.txt, one word per line.
$privateWords = @()
$privateFile = Join-Path $root ".local\private-strings.txt"
if (Test-Path $privateFile) { $privateWords = @(Get-Content $privateFile | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }) }
foreach ($n in @($rootNorm, $rootNorm.Replace([string][char]92, '/')) + $privateWords) {
    $needles.Add($n)
    $needles.Add((($n.ToCharArray() | ForEach-Object { [string]$_ + [char]0 }) -join ''))
}
$scanned = 0
foreach ($dir in @($appDir, $modStage, $tsStage, $starterStage)) {
    $base = (Resolve-Path $dir).Path.TrimEnd([char]92) + [char]92
    Get-ChildItem $dir -Recurse -File | ForEach-Object {
        $scanned++
        $rel = $_.FullName.Substring($base.Length)
        foreach ($w in $privateWords) { if ($rel.IndexOf($w, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $problems.Add("private word in a file name: $rel") } }
        $text = $latin1.GetString([System.IO.File]::ReadAllBytes($_.FullName))
        foreach ($n in $needles) {
            if ($text.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $problems.Add("$rel contains '" + $n.Replace([string][char]0, '') + "'")
                break
            }
        }
    }
}
if ($problems.Count -gt 0) {
    throw ("The release would ship things it must not, so no zips were made:" + [Environment]::NewLine + (($problems | ForEach-Object { "  - $_" }) -join [Environment]::NewLine))
}
Write-Host "Checked $scanned files: no .pdb files, no build paths, no personal names."

# Zip with forward-slash entry names. (Windows PowerShell's Compress-Archive writes backslashes, which Linux, Proton
# and some mod managers unpack as files literally named "BepInEx\plugins\...".)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function New-Zip([string]$sourceDir, [string]$zipPath, [string]$prefix) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $base = (Resolve-Path $sourceDir).Path.TrimEnd([char]92) + [char]92
        Get-ChildItem $sourceDir -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($base.Length).Replace([string][char]92, '/')
            if ($prefix) { $rel = "$prefix/$rel" }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
}

$appZip = Join-Path $out "$appName.zip"
New-Zip $appDir $appZip $appName
$modZip = Join-Path $out "OpenPlane-mod.zip"
New-Zip $modStage $modZip ""
# NOMM unpacks into BepInEx/plugins/com.meta.openplane/ itself, so its zip holds the files with no folders (the
# BepInEx/plugins/OpenPlane layout would end up nested in there).
$nommZip = Join-Path $out "OpenPlane-nomm.zip"
New-Zip $modPlugins $nommZip ""
$tsZip = Join-Path $out "Meta-OpenPlane-$version.zip"
New-Zip $tsStage $tsZip ""
$starterZip = Join-Path $out "OpenPlane-starter-planes.zip"
New-Zip $starterStage $starterZip ""
Remove-Item $modStage, $tsStage, $starterStage -Recurse -Force

$sums = foreach ($z in @($appZip, $modZip, $nommZip, $tsZip, $starterZip)) {
    $h = (Get-FileHash $z -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h  $(Split-Path -Leaf $z)"
}
# LF line endings: `sha256sum -c SHA256SUMS.txt` (Linux, Steam Deck, Git Bash) fails on Windows' CRLF.
[IO.File]::WriteAllText((Join-Path $out "SHA256SUMS.txt"), (($sums -join "`n") + "`n"), [Text.Encoding]::ASCII)

# ------------------------------------------------------------------ the NOMNOM manifest for this release
# Goes in NOMNOM's modManifests folder by pull request (see its Docs/SCHEMA.md). With autoUpdateArtifacts, NOMNOM adds
# later GitHub releases itself, picking OpenPlane-nomm.zip by name.
$owner, $repoName = $Repo.Split('/')
$modHash = (Get-FileHash $nommZip -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    id = "com.meta.openplane"
    displayName = "OpenPlane"
    description = "Fly planes you converted from Flyout or built yourself with the OpenPlane app (.oplane files). Each plane is built on a stock airframe, with its own shape, flight model, colours, cockpit and weapon stations. Blueprinter add-on."
    tags = @("mod", "aircraft", "blueprinter")
    urls = @([ordered]@{ name = "info"; url = "https://github.com/$Repo" })
    authors = @("Meta")
    isClientOrServer = "Both"
    githubOwner = $owner
    githubRepoName = $repoName
    autoUpdateArtifacts = "True"
    isAiAssisted = "True"
    releaseAssetName = "OpenPlane-nomm.zip"
    artifacts = @([ordered]@{
        fileName = "OpenPlane-nomm.zip"
        version = $version
        category = "release"
        type = "plugin"
        gameVersion = $GameVersion
        downloadUrl = "https://github.com/$Repo/releases/download/v$version/OpenPlane-nomm.zip"
        hash = "sha256:$modHash"
        dependencies = @([ordered]@{ id = "com.nikkorap.blueprinter"; version = "2.1.1" })
    })
} | ConvertTo-Json -Depth 6 | Write-Utf8 (Join-Path $out "com.meta.openplane.json")
Write-Host ""
Write-Host "Release files in $out"
$sums | ForEach-Object { Write-Host "  $_" }
Get-ChildItem $out -Filter *.zip | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
if ($missing.Count -gt 0) { Write-Warning "This build is missing licence texts (see above): don't publish it." }
