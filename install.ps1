param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Approximately Up Demo"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bundle = Join-Path $root "vendor\bepinex"
$plugin = Join-Path $root "src\WooldrumsModestMenu\bin\Release\net6.0\WooldrumsModestMenu.dll"

if (-not (Test-Path (Join-Path $GameDir "ApproximatelyUp.exe"))) {
    throw "Game not found at: $GameDir (pass -GameDir with the folder holding ApproximatelyUp.exe)"
}

if (Get-Process ApproximatelyUp -ErrorAction SilentlyContinue) {
    throw "Close the game first, then re-run (it locks the plugin file)."
}

dotnet build (Join-Path $root "src\WooldrumsModestMenu") -c Release
if (-not (Test-Path $plugin)) {
    throw "Build did not produce: $plugin"
}

# --- Install / update BepInEx 6 (be.785) ---
$bep = Join-Path $GameDir "BepInEx"
New-Item -ItemType Directory -Force $bep | Out-Null

foreach ($f in @("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt")) {
    $src = Join-Path $bundle $f
    if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination (Join-Path $GameDir $f) -Force }
}

# CoreCLR runtime required by Doorstop for IL2CPP (dotnet\coreclr.dll).
$dotnetSrc = Join-Path $bundle "dotnet"
$dotnetDst = Join-Path $GameDir "dotnet"
if (-not (Test-Path $dotnetSrc)) {
    throw "Bundled CoreCLR runtime missing: $dotnetSrc"
}
if (Test-Path $dotnetDst) { Remove-Item -LiteralPath $dotnetDst -Recurse -Force }
Copy-Item -LiteralPath $dotnetSrc -Destination $dotnetDst -Recurse -Force

$coreDst = Join-Path $bep "core"
if (Test-Path $coreDst) { Remove-Item -LiteralPath $coreDst -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $bundle "core") -Destination $coreDst -Recurse -Force

# Keep an existing config; only drop ours in on a fresh install.
$cfgDir = Join-Path $bep "config"
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$cfgDst = Join-Path $cfgDir "BepInEx.cfg"
if (-not (Test-Path $cfgDst)) {
    Copy-Item -LiteralPath (Join-Path $bundle "BepInEx.cfg") -Destination $cfgDst -Force
}

# Force a clean interop rebuild against this core.
foreach ($d in @("interop", "cache")) {
    $p = Join-Path $bep $d
    if (Test-Path $p) { Remove-Item -LiteralPath $p -Recurse -Force }
}

# --- Install plugin ---
$pluginsDir = Join-Path $bep "plugins"
New-Item -ItemType Directory -Force $pluginsDir | Out-Null

Get-ChildItem -Path $pluginsDir -Recurse -File -Include "WooldrumsModestMenu.dll", "ApproximatelyUpEggMod.dll" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

Copy-Item -LiteralPath $plugin -Destination (Join-Path $pluginsDir "WooldrumsModestMenu.dll") -Force

& (Join-Path $root "fix-interop.ps1") -GameDir $GameDir

Write-Host "Installed to $GameDir. Launch the game and press F8."
