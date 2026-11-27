[CmdletBinding()]
param(
    [string]$ComfyDir = "",
    [string]$PythonExe = "",
    [int]$Port = 8188,
    [string]$ListenHost = "127.0.0.1"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ComfyDir) -or -not (Test-Path $ComfyDir)) {
    $repoParent = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
    $candidates = @(
        (Join-Path $env:GULA_STUDIO_HOME "ComfyUI"),
        (Join-Path $repoParent "ComfyUI"),
        (Join-Path $env:LOCALAPPDATA "GulaStudio\ComfyUI")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($altPath in $candidates) {
        if (Test-Path (Join-Path $altPath "main.py")) {
            $ComfyDir = $altPath
            break
        }
    }
}
if ([string]::IsNullOrWhiteSpace($ComfyDir) -or -not (Test-Path (Join-Path $ComfyDir "main.py"))) {
    throw "ComfyUI not found. Install it via Gula Studio -> File -> Setup & Model Manager or pass -ComfyDir."
}
if ([string]::IsNullOrWhiteSpace($PythonExe)) {
    $parentDir = Split-Path -Parent $ComfyDir
    $pyCandidates = @(
        (Join-Path $parentDir "python_embeded\python.exe"),
        (Join-Path $ComfyDir "venv\Scripts\python.exe"),
        (Join-Path $ComfyDir ".venv\Scripts\python.exe")
    )
    $PythonExe = ($pyCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($PythonExe)) { $PythonExe = "python" }
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "       Gula Studio - Supervised ComfyUI Host Service      " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Working Directory: $ComfyDir"
Write-Host "Python Runtime:    $PythonExe"
Write-Host "Binding Host:      $ListenHost (Loopback Only - Protected)"
Write-Host "Port:              $Port"
Write-Host ""

Write-Host "Supervising ComfyUI process under active GPU user session..." -ForegroundColor Yellow
Write-Host "Press Ctrl+C to terminate supervisor." -ForegroundColor Gray
Write-Host ""

$restartCount = 0
$maxRestarts = 10

while ($restartCount -lt $maxRestarts) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Starting ComfyUI daemon on $ListenHost`:$Port..." -ForegroundColor Cyan

    $proc = Start-Process `
        -FilePath $PythonExe `
        -ArgumentList "main.py --listen $ListenHost --port $Port --use-pytorch-cross-attention --fast fp16_accumulation --reserve-vram 0.8 --disable-auto-launch" `
        -WorkingDirectory $ComfyDir `
        -PassThru `
        -NoNewWindow

    # Wait for process to exit or monitor health
    $proc.WaitForExit()

    $exitCode = $proc.ExitCode
    $restartCount++
    Write-Warning "[$(Get-Date -Format 'HH:mm:ss')] ComfyUI exited with code $exitCode (Crash/Restart #$restartCount)."

    if ($exitCode -eq 0) {
        Write-Host "ComfyUI shut down cleanly. Exiting supervisor." -ForegroundColor Green
        break
    }

    Write-Host "Waiting 5 seconds before restarting..." -ForegroundColor Yellow
    Start-Sleep -Seconds 5
}
