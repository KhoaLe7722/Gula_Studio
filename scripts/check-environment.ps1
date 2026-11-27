# Read-only, safe preflight for the user's Windows PC.
[CmdletBinding()]
param([string]$ComfyBaseUrl = 'http://127.0.0.1:8188')
$ErrorActionPreference = 'Continue'
Write-Host '=== CharacterConceptAI preflight (read only) ==='
Write-Host ('OS: ' + [Environment]::OSVersion)
Write-Host ('Machine: ' + $env:COMPUTERNAME)
if (Get-Command dotnet -ErrorAction SilentlyContinue) { dotnet --info } else { Write-Warning 'dotnet not found' }
if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { nvidia-smi } else { Write-Warning 'nvidia-smi not found; GPU not verified' }
try {
  $ram = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
  Write-Host ('RAM total (GiB): ' + [math]::Round($ram/1GB,2))
} catch { Write-Warning ('RAM probe failed: ' + $_.Exception.Message) }
try { Get-Service '*MSSQL*' | Select-Object Name, Status | Format-Table -AutoSize } catch { Write-Warning 'SQL services not detected' }
try {
  $r = Invoke-WebRequest -Uri ($ComfyBaseUrl.TrimEnd('/') + '/system_stats') -UseBasicParsing -TimeoutSec 3
  Write-Host ('ComfyUI API HTTP status: ' + $r.StatusCode)
} catch { Write-Warning ('ComfyUI API not reachable; do not claim real GPU inference: ' + $_.Exception.Message) }
Write-Host 'Note: This script does not run inference, verify model checksums, or test LAN TLS.'
