param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Approximately Up Demo"
)

$ErrorActionPreference = "Stop"

$cfg = Join-Path $GameDir "BepInEx\config\BepInEx.cfg"
$interop = Join-Path $GameDir "BepInEx\interop"

if (-not (Test-Path $cfg)) {
    throw "BepInEx.cfg not found. Launch the game once with BepInEx installed first."
}

# Unity 6 interop can emit duplicate <>O types; this regex gives them unique names.
$content = Get-Content $cfg -Raw
if ($content -notmatch 'UnhollowerDeobfuscationRegex\s*=\s*\^<>\.\+\$') {
    $content = $content -replace 'UnhollowerDeobfuscationRegex\s*=.*', 'UnhollowerDeobfuscationRegex = ^<>.+$'
    Set-Content -Path $cfg -Value $content -NoNewline
    if (Test-Path $interop) { Remove-Item -Recurse -Force $interop }
    Write-Host "Applied interop fix; it regenerates on next launch."
} else {
    Write-Host "Interop fix already applied."
}
