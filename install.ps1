param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Approximately Up Demo"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bepZip = Join-Path $root "vendor\BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip"
$unity6Zip = Join-Path $root "vendor\Il2CppInterop-Unity6-1.0.0.zip"
$monoModBackports = Join-Path $root "vendor\MonoMod.Backports.dll"
$monoModILHelpers = Join-Path $root "vendor\MonoMod.ILHelpers.dll"
$plugin = Join-Path $root "src\WooldrumsModestMenu\bin\Release\netstandard2.1\WooldrumsModestMenu.dll"
$legacyPluginNames = @("ApproximatelyUpEggMod.dll")

if (-not (Test-Path (Join-Path $GameDir "ApproximatelyUp.exe"))) {
    throw "Game directory not found or invalid: $GameDir"
}

if (-not (Test-Path $bepZip)) {
    throw "Missing BepInEx zip: $bepZip"
}

if (-not (Test-Path $unity6Zip)) {
    throw "Missing Unity 6 interop zip: $unity6Zip"
}

if (-not (Test-Path $monoModBackports)) {
    throw "Missing dependency: $monoModBackports"
}

if (-not (Test-Path $monoModILHelpers)) {
    throw "Missing dependency: $monoModILHelpers"
}

Push-Location $root
dotnet build "src\WooldrumsModestMenu" -c Release
Pop-Location

if (-not (Test-Path $plugin)) {
    throw "Build did not produce expected plugin DLL: $plugin"
}

$temp = Join-Path $env:TEMP ("WMM-BepInEx-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $temp | Out-Null
Expand-Archive -Force $bepZip $temp

Copy-Item -Path (Join-Path $temp "*") -Destination $GameDir -Recurse -Force
Remove-Item -LiteralPath $temp -Recurse -Force

$unity6Temp = Join-Path $env:TEMP ("WMM-Unity6Interop-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $unity6Temp | Out-Null
Expand-Archive -Force $unity6Zip $unity6Temp
$bepCore = Join-Path $GameDir "BepInEx\core"

# Unity 6 / metadata v31: patcha BepInEx core med Il2CppInterop-DLL:er som matchar denna zip.
Copy-Item -Path (Join-Path $unity6Temp "release\AsmResolver*.dll") -Destination $bepCore -Force
Copy-Item -Path (Join-Path $unity6Temp "release\AssetRipper*.dll") -Destination $bepCore -Force
Copy-Item -Path (Join-Path $unity6Temp "release\Cpp2IL.Core.dll") -Destination $bepCore -Force
Copy-Item -Path (Join-Path $unity6Temp "release\LibCpp2IL.dll") -Destination $bepCore -Force
Copy-Item -Path (Join-Path $unity6Temp "release\StableNameDotNet.dll") -Destination $bepCore -Force
Copy-Item -Path (Join-Path $unity6Temp "release\WasmDisassembler.dll") -Destination $bepCore -Force
Remove-Item -LiteralPath $unity6Temp -Recurse -Force
Copy-Item -Path $monoModBackports -Destination (Join-Path $bepCore "MonoMod.Backports.dll") -Force
Copy-Item -Path $monoModILHelpers -Destination (Join-Path $bepCore "MonoMod.ILHelpers.dll") -Force

$interopCache = Join-Path $GameDir "BepInEx\interop"
if (Test-Path $interopCache) {
    Remove-Item -LiteralPath $interopCache -Recurse -Force
}

$pluginsDir = Join-Path $GameDir "BepInEx\plugins"
New-Item -ItemType Directory -Force $pluginsDir | Out-Null

# Rensa gamla plugin-namn så bara nya DLL:en laddas.
foreach ($legacy in $legacyPluginNames) {
    $legacyPath = Join-Path $pluginsDir $legacy
    if (Test-Path $legacyPath) {
        Remove-Item -LiteralPath $legacyPath -Force
    }
}

Copy-Item -Path $plugin -Destination (Join-Path $pluginsDir "WooldrumsModestMenu.dll") -Force

Write-Host "Installed Wooldrum's Modest Menu to $GameDir"
Write-Host "Installed Unity 6 metadata-v31 interop support."
Write-Host "F8 to open and close. Other players shouldn't need this. If they don't have unlimited items, refresh co-op cap, kick them, then reinvite them."
