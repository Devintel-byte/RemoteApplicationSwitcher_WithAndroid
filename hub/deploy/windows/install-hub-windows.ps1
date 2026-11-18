<#
.SYNOPSIS
  Installs the Show Control Hub on a Windows PC at the venue (the "site hub"): it starts with Windows,
  runs in the background (no window, no sign-in needed) and restarts itself if it ever stops.

.DESCRIPTION
  - Program:  C:\Program Files\ShowControl\Hub\ShowControlHub.exe
  - Data:     C:\ProgramData\ShowControl\Hub   (accounts, PCs, kiosks… and hub.log)
  - Runs as:  a scheduled task "ShowControl Hub" at system start-up, as the LOCAL SERVICE account
  - Network:  port 47900, opened in Windows Firewall for private and domain networks only (not public)

  It's separate from your hosted hub (e.g. https://hub.movingsurface.com.ng): this one serves the local
  network. Link the two on either hub's "Linked hubs" page and they stay in sync.

  Run it again to update (data is kept). -Uninstall removes it (data is kept unless -RemoveData).

.EXAMPLE
  Right-click Install-Hub.cmd → Run as administrator
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install-hub-windows.ps1 -Port 47910
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File install-hub-windows.ps1 -Uninstall
#>
param(
    [int]$Port = 47900,
    [string]$Source = $PSScriptRoot,
    [switch]$Uninstall,
    [switch]$RemoveData,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$TaskName = "ShowControl Hub"
$RuleName = "Show Control Hub"
$InstallDir = Join-Path $env:ProgramFiles "ShowControl\Hub"
$DataDir = Join-Path $env:ProgramData "ShowControl\Hub"
$Exe = Join-Path $InstallDir "ShowControlHub.exe"

function Done([int]$code) {
    if (-not $NoPause) { Write-Host ""; Read-Host "Press Enter to close" | Out-Null }
    exit $code
}

# Needs administrator rights (Program Files, scheduled task, firewall). Ask for them if missing.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Port", $Port, "-Source", "`"$Source`"")
    if ($Uninstall) { $argList += "-Uninstall" }
    if ($RemoveData) { $argList += "-RemoveData" }
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    exit 0
}

function Stop-Hub {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    }
    Get-Process -Name "ShowControlHub" -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

try {
    if ($Uninstall) {
        Write-Host "Removing the Show Control Hub..." -ForegroundColor Cyan
        Stop-Hub
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
        if ($RemoveData -and (Test-Path $DataDir)) { Remove-Item $DataDir -Recurse -Force; Write-Host "  Data deleted." }
        else { Write-Host "  Data kept in $DataDir (delete the folder to remove it)." }
        Write-Host "Removed." -ForegroundColor Green
        Done 0
    }

    $srcExe = Join-Path $Source "ShowControlHub.exe"
    if (-not (Test-Path $srcExe)) { throw "ShowControlHub.exe isn't next to this script ($Source). Unzip the whole package first." }

    Write-Host "Installing the Show Control Hub (site hub)..." -ForegroundColor Cyan

    # 1. Program files
    Stop-Hub
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item $srcExe $InstallDir -Force
    Copy-Item $PSCommandPath (Join-Path $InstallDir "install-hub-windows.ps1") -Force
    Write-Host "  1/5 Program in $InstallDir"

    # 2. Data folder, writable by the service account; port in settings.json
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $acl = Get-Acl $DataDir
    $rule = New-Object Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\LOCAL SERVICE", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($rule)
    Set-Acl $DataDir $acl
    $settingsPath = Join-Path $DataDir "settings.json"
    $settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    $settings | Add-Member -NotePropertyName port -NotePropertyValue $Port -Force
    $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8
    $log = Join-Path $DataDir "hub.log"
    if ((Test-Path $log) -and (Get-Item $log).Length -gt 10MB) { Move-Item $log "$log.old" -Force }
    Write-Host "  2/5 Data in $DataDir (port $Port)"

    # 3. Scheduled task: at start-up, hidden, as LOCAL SERVICE, restarts if it stops
    $cmdArgs = "/c `"`"$Exe`" --data `"$DataDir`" >> `"$log`" 2>&1`""
    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument $cmdArgs -WorkingDirectory $InstallDir
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\LOCAL SERVICE" -LogonType ServiceAccount -RunLevel Limited
    $taskSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Hidden
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $taskSettings `
        -Description "Show Control Hub for this venue's network (port $Port). Installed by install-hub-windows.ps1." -Force | Out-Null
    Write-Host "  3/5 Starts with Windows in the background (Task Scheduler: '$TaskName')"

    # 4. Firewall: private and domain networks only, never public Wi-Fi
    Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $RuleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Private, Domain `
        -Description "Phones, PCs and kiosks on this network reach the Show Control Hub." | Out-Null
    Write-Host "  4/5 Firewall open on port $Port for private networks"

    # 5. Start it and check
    Start-ScheduledTask -TaskName $TaskName
    $ok = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        try { $h = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/health" -TimeoutSec 2; if ($h.ok) { $ok = $true; break } } catch { }
    }
    if (-not $ok) { throw "The hub didn't start. See $log" }
    Write-Host "  5/5 Running: version $($h.version)" -ForegroundColor Green

    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -match '^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)' -and $_.AddressState -eq 'Preferred' } |
        Select-Object -ExpandProperty IPAddress
    Write-Host ""
    Write-Host "Open the hub:" -ForegroundColor Cyan
    Write-Host "  On this PC:          http://localhost:$Port"
    foreach ($ip in $ips) { Write-Host "  On this network:     http://${ip}:$Port" }
    $codeFile = Join-Path $DataDir "setup-code.txt"
    if (Test-Path $codeFile) {
        Write-Host ""
        Write-Host "First time: setup code  $((Get-Content $codeFile -Raw).Trim())" -ForegroundColor Yellow
        Write-Host "  Choose 'Join an existing hub' to link it to your hosted hub (make a link code there under Linked hubs),"
        Write-Host "  or create a new admin account if this is your only hub."
    }
    Write-Host ""
    Write-Host "Tip: give this PC a fixed IP address (a DHCP reservation on the router) so the address above doesn't change."
    Done 0
}
catch {
    Write-Host ""
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Done 1
}
