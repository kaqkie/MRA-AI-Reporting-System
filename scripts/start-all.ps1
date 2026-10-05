<#
  Starts everything the MRA Reporting Assistant needs, in the right order:
    1. llama-server (the AI model), unless it is already running on port 8080;
    2. the web app, unless it is already running on port 5080;
    3. opens http://localhost:5080 in the browser.
  Each one runs in its own minimised window, so its messages can still be read.
  Safe to run twice: anything already running is left alone.

  Run by hand:
    powershell -ExecutionPolicy Bypass -File .\scripts\start-all.ps1
  To run it automatically when you sign in to Windows, see install-autostart.ps1.
#>
param(
    [switch]$NoBrowser,
    [int]$ModelPort = 8080,
    [int]$AppPort   = 5080
)

$root    = Split-Path -Parent $PSScriptRoot
$webDir  = Join-Path $root "src\MraReporting.Web"
$llamaPs = Join-Path $PSScriptRoot "start-llama-server.ps1"

function Test-Listening([int]$port) {
    [bool](Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

function Wait-Url([string]$url, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try {
            Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5 | Out-Null
            return $true
        } catch {
            # Any HTTP answer (even 401 when Windows sign-in is on) means it is up.
            if ($_.Exception.Response) { return $true }
            Start-Sleep -Seconds 2
        }
    }
    return $false
}

# ---------------------------------------------------------------- 1. the AI model
if (Test-Listening $ModelPort) {
    Write-Host "AI model server is already running on port $ModelPort."
} else {
    Write-Host "Starting the AI model server..."
    Start-Process powershell -WindowStyle Minimized -ArgumentList @(
        "-NoExit", "-ExecutionPolicy", "Bypass", "-File", "`"$llamaPs`"", "-Port", $ModelPort)
    if (Wait-Url "http://127.0.0.1:$ModelPort/v1/models" 180) {
        Write-Host "AI model server is ready."
    } else {
        Write-Warning "The AI model server did not answer within 3 minutes. Open its window (minimised on the taskbar) to see why."
    }
}

# ---------------------------------------------------------------- 2. the web app
if (Test-Listening $AppPort) {
    Write-Host "Reporting app is already running on port $AppPort."
} else {
    # An old copy that is not listening any more still locks its files and stops the build.
    Get-Process MraReporting.Web -ErrorAction SilentlyContinue | Stop-Process -Force
    Write-Host "Starting the reporting app (the first start after an update takes longer, because it builds)..."
    Start-Process powershell -WindowStyle Minimized -WorkingDirectory $webDir -ArgumentList @(
        "-NoExit", "-Command", "dotnet run")
    if (Wait-Url "http://localhost:$AppPort/" 300) {
        Write-Host "Reporting app is ready."
    } else {
        Write-Warning "The reporting app did not start within 5 minutes. Open its window (minimised on the taskbar) to see the error."
        exit 1
    }
}

# ---------------------------------------------------------------- 3. the browser
if (-not $NoBrowser) {
    Start-Process "http://localhost:$AppPort/"
}
Write-Host "All running: http://localhost:$AppPort/"
