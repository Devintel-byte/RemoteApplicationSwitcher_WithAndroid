<#
.SYNOPSIS
  Builds everything you deploy: the Show Control Agent installer for show PCs and the hub package
  for your Linux server.

.DESCRIPTION
  1. Publishes the agent as one self-contained ShowControlAgent.exe (the .NET runtime is inside it,
     so show PCs need nothing else installed).
  2. Makes a portable zip (exe + install.ps1) that works without any installer tools.
  3. If Inno Setup 6 is installed, compiles ShowControlAgent-Setup-<version>.exe
     (with -Interactive it offers to install Inno Setup through winget first).
  4. Publishes the hub for Linux (self-contained) and packs it with its install script:
     showcontrol-hub-<version>-linux-x64.tar.gz (see docs/HUB-HOSTING.md).
  5. Publishes the hub for Windows (the venue's "site hub") with its start-up installer:
     ShowControlHub-<version>-windows.zip (unzip on the PC, run Install-Hub.cmd).

  Everything lands in installer\Output, with a log in installer\Output\build-log.txt.
  BUILD.cmd in the project folder runs this with -Interactive.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>
param(
    [switch]$SkipZip,
    [switch]$SkipHub,
    [switch]$Interactive
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $PSScriptRoot "Output"
New-Item -ItemType Directory -Force -Path $output | Out-Null
$log = Join-Path $output "build-log.txt"
try { Start-Transcript -Path $log -Force | Out-Null } catch { }

function Get-ProjectVersion([string]$csproj) {
    [xml]$proj = Get-Content $csproj
    $v = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if ($v) { return $v } else { return "1.0.0" }
}

function Find-Iscc {
    return @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

$ok = $true
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "The .NET 8 SDK is needed on this build PC (not on the show PCs): winget install Microsoft.DotNet.SDK.8"
    }
    Write-Host "dotnet $(dotnet --version)"

    # ---------------- agent ----------------
    $project = Join-Path $root "agent\ShowControl.Agent\ShowControl.Agent.csproj"
    $publish = Join-Path $root "publish\agent"
    $version = Get-ProjectVersion $project
    Write-Host ""
    Write-Host "== Show Control Agent $version ==" -ForegroundColor Cyan

    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    Write-Host "Publishing (self-contained, single file)..."
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none `
        -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (agent) failed" }

    $exe = Join-Path $publish "ShowControlAgent.exe"
    if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing" }
    Write-Host ("  {0} ({1} MB)" -f $exe, [math]::Round((Get-Item $exe).Length / 1MB, 1))

    if (-not $SkipZip) {
        Copy-Item (Join-Path $PSScriptRoot "install.ps1") $publish -Force
        $zip = Join-Path $output "ShowControlAgent-$version-portable.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip
        Write-Host "  $zip" -ForegroundColor Green
    }

    $iscc = Find-Iscc
    if (-not $iscc -and $Interactive -and (Get-Command winget -ErrorAction SilentlyContinue)) {
        $answer = Read-Host "Inno Setup (free) is needed for the Setup.exe. Install it now with winget? [Y/n]"
        if ($answer -eq "" -or $answer -match "^[Yy]") {
            winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
            $iscc = Find-Iscc
        }
    }
    if ($iscc) {
        Write-Host "Compiling the installer with Inno Setup..."
        & $iscc "/Q" "/DAppVersion=$version" "/DSourceDir=$publish" (Join-Path $PSScriptRoot "ShowControlAgent.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
        Write-Host "  $(Join-Path $output "ShowControlAgent-Setup-$version.exe")" -ForegroundColor Green
    } else {
        Write-Host "  Inno Setup 6 isn't installed, so only the portable zip was made (winget install JRSoftware.InnoSetup)." -ForegroundColor Yellow
    }

    # ---------------- hub (Linux server package) ----------------
    if (-not $SkipHub) {
        $hubProject = Join-Path $root "hub\ShowControl.Hub\ShowControl.Hub.csproj"
        $hubVersion = Get-ProjectVersion $hubProject
        $hubPublish = Join-Path $root "publish\hub-linux-x64"
        Write-Host ""
        Write-Host "== Show Control Hub $hubVersion (Linux server package) ==" -ForegroundColor Cyan
        if (Test-Path $hubPublish) { Remove-Item $hubPublish -Recurse -Force }
        dotnet publish $hubProject -c Release -r linux-x64 --self-contained true `
            -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=none `
            -o $hubPublish
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (hub) failed" }
        $deploy = Join-Path $root "hub\deploy"
        foreach ($f in "install-hub.sh", "install-hub-vps.sh", "showcontrol-hub.service", "apache-ssl.conf", "apache-std.conf") {
            Copy-Item (Join-Path $deploy $f) $hubPublish -Force
        }
        $tgz = Join-Path $output "showcontrol-hub-$hubVersion-linux-x64.tar.gz"
        if (Test-Path $tgz) { Remove-Item $tgz -Force }
        # tar.exe ships with Windows 10 1803 and later.
        tar -czf $tgz -C $hubPublish .
        if ($LASTEXITCODE -ne 0) { throw "Packing the hub failed (tar)" }
        Write-Host ("  {0} ({1} MB)" -f $tgz, [math]::Round((Get-Item $tgz).Length / 1MB, 1)) -ForegroundColor Green

        # ---------------- hub (Windows site hub) ----------------
        $hubWin = Join-Path $root "publish\hub-win-x64"
        Write-Host ""
        Write-Host "== Show Control Hub $hubVersion (Windows site hub) ==" -ForegroundColor Cyan
        if (Test-Path $hubWin) { Remove-Item $hubWin -Recurse -Force }
        dotnet publish $hubProject -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=none `
            -o $hubWin
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (hub, Windows) failed" }
        foreach ($f in "install-hub-windows.ps1", "Install-Hub.cmd") {
            Copy-Item (Join-Path $deploy "windows\$f") $hubWin -Force
        }
        $hubZip = Join-Path $output "ShowControlHub-$hubVersion-windows.zip"
        if (Test-Path $hubZip) { Remove-Item $hubZip -Force }
        Compress-Archive -Path (Join-Path $hubWin "*") -DestinationPath $hubZip
        Write-Host ("  {0} ({1} MB)" -f $hubZip, [math]::Round((Get-Item $hubZip).Length / 1MB, 1)) -ForegroundColor Green
    }
}
catch {
    $ok = $false
    Write-Host ""
    Write-Host "BUILD FAILED: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Write-Host ""
    if ($ok) { Write-Host "Done. Files are in $output" -ForegroundColor Green }
    try { Stop-Transcript | Out-Null } catch { }
}
if (-not $ok) { exit 1 }
