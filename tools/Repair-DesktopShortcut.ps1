# Repairs the legacy desktop shortcut whose IconLocation pointed into an
# MSIX versioned installation directory and became blank after an update.
$ErrorActionPreference = 'Stop'
$package = Get-AppxPackage -Name KieranLucas.SnapStack | Select-Object -First 1
if (-not $package) { throw 'Install SnapStack before repairing its shortcut.' }

$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'SnapStack.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$appId = "shell:AppsFolder\$($package.PackageFamilyName)!App"
if ((Test-Path $shortcutPath) -and
    $shortcut.Arguments -ne $appId) {
    throw "Refusing to alter an unrelated shortcut: $shortcutPath"
}

$sourceIcon = Join-Path $package.InstallLocation 'Assets\SnapStack.ico'
if (-not (Test-Path $sourceIcon)) { throw "Package icon missing: $sourceIcon" }
$iconDirectory = Join-Path $env:LOCALAPPDATA 'SnapStack\Assets'
New-Item -ItemType Directory -Force -Path $iconDirectory | Out-Null
$stableIcon = Join-Path $iconDirectory 'SnapStack.ico'
Copy-Item -LiteralPath $sourceIcon -Destination $stableIcon -Force

$shortcut.TargetPath = Join-Path $env:WINDIR 'explorer.exe'
$shortcut.Arguments = $appId
$shortcut.IconLocation = "$stableIcon,0"
$shortcut.Description = 'Capture screenshots with SnapStack'
$shortcut.Save()
Write-Host "Desktop shortcut icon repaired: $shortcutPath"
