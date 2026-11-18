<#
.SYNOPSIS
  Installs (or removes) Show Control Agent on this PC without an installer tool.

.DESCRIPTION
  Run from the folder with ShowControlAgent.exe (the portable zip). It asks for admin rights for the
  machine-wide part (copy to Program Files, Windows Firewall rule, Start menu shortcut); the per-user
  part (start when you sign in, PC name, starting the agent) runs as you. Settings in
  %LOCALAPPDATA%\ShowControl are kept across upgrades and uninstalls.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install.ps1
  powershell -ExecutionPolicy Bypass -File install.ps1 -PcName "Stage Left"
  powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
param(
    [string]$PcName = "",
    [switch]$NoStartup,
    [switch]$NoFirewall,
    [switch]$Uninstall,
    # Internal: the elevated copy only does the machine-wide part.
    [switch]$MachineOnly
)

$ErrorActionPreference = "Stop"
$appName = "Show Control Agent"
$exeName = "ShowControlAgent.exe"
$target = Join-Path $env:ProgramFiles $appName
$targetExe = Join-Path $target $exeName
$shortcut = Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "$appName.lnk"

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-MachinePart {
    Get-Process -Name "ShowControlAgent" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    & netsh advfirewall firewall delete rule name="$appName" | Out-Null

    if ($Uninstall) {
        Remove-Item $shortcut -ErrorAction SilentlyContinue
        Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
        return
    }

    $source = Join-Path $PSScriptRoot $exeName
    if (-not (Test-Path $source)) { throw "Run this from the folder that contains $exeName" }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item $source $targetExe -Force
    Get-ChildItem $PSScriptRoot -File | Where-Object { $_.Name -notin @($exeName, "install.ps1") } |
        Copy-Item -Destination $target -Force

    if (-not $NoFirewall) {
        # One rule for the program; profile=any because venue networks are often "Public" to Windows.
        & netsh advfirewall firewall add rule name="$appName" dir=in action=allow program="$targetExe" enable=yes profile=any | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($shortcut)
    $lnk.TargetPath = $targetExe
    $lnk.WorkingDirectory = $target
    $lnk.Description = "Show Control: start and close show apps from your phone"
    $lnk.Save()
}

function Invoke-UserPart {
    if (-not (Test-Path $targetExe)) { return }
    if ($Uninstall) {
        Start-Process $targetExe -ArgumentList "--startup", "off" -Wait
        return
    }
    if ($PcName) { Start-Process $targetExe -ArgumentList "--set-name", "`"$PcName`"" -Wait }
    $startup = if ($NoStartup) { "off" } else { "on" }
    Start-Process $targetExe -ArgumentList "--startup", $startup -Wait
    Start-Process $targetExe
}

if ($MachineOnly) { Invoke-MachinePart; exit }

# Uninstall: remove the per-user startup entry while the exe still exists.
if ($Uninstall) { Invoke-UserPart }

if (Test-Admin) {
    Invoke-MachinePart
} else {
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-MachineOnly")
    if ($NoFirewall) { $argList += "-NoFirewall" }
    if ($Uninstall) { $argList += "-Uninstall" }
    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "The admin part didn't finish (exit code $($p.ExitCode))" }
}

if ($Uninstall) {
    Write-Host "$appName removed. Its settings are still in $env:LOCALAPPDATA\ShowControl (delete that folder to forget them)." -ForegroundColor Green
} else {
    Invoke-UserPart
    Write-Host "$appName installed in $target and started. Look for its icon in the system tray." -ForegroundColor Green
}
