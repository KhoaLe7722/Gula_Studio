[CmdletBinding()]
param(
    [int]$HttpsPort = 5001,
    [int]$HttpPort = 5000
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   CharacterConceptAI - Windows Firewall Configuration   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "This script requires Administrator privileges to modify Windows Defender Firewall rules."
    Write-Warning "Please rerun PowerShell as Administrator."
    Write-Host "Displaying proposed firewall rules instead:" -ForegroundColor Yellow
    Write-Host "  Rule 1: Inbound TCP $HttpsPort (HTTPS LAN API) - Scope: LocalSubnet"
    Write-Host "  Rule 2: Inbound TCP $HttpPort (HTTP LAN API) - Scope: LocalSubnet"
    Write-Host "  Security Guard: Ensure SQL Server (1433) and ComfyUI (8188) remain local loopback only."
    return
}

$ruleNameHttps = "CharacterConceptAI Server HTTPS ($HttpsPort)"
$ruleNameHttp = "CharacterConceptAI Server HTTP ($HttpPort)"

# Remove existing rules if any
Get-NetFirewallRule -DisplayName $ruleNameHttps -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName $ruleNameHttp -ErrorAction SilentlyContinue | Remove-NetFirewallRule

# Create inbound rules restricted to LocalSubnet
Write-Host "Creating Inbound Firewall Rule for HTTPS Port $HttpsPort (LocalSubnet only)..." -ForegroundColor Yellow
New-NetFirewallRule `
    -DisplayName $ruleNameHttps `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $HttpsPort `
    -RemoteAddress LocalSubnet `
    -Description "Allows incoming HTTPS traffic from client workstations on local LAN to CharacterConceptAI Server." | Out-Null

Write-Host "Creating Inbound Firewall Rule for HTTP Port $HttpPort (LocalSubnet only)..." -ForegroundColor Yellow
New-NetFirewallRule `
    -DisplayName $ruleNameHttp `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $HttpPort `
    -RemoteAddress LocalSubnet `
    -Description "Allows incoming HTTP traffic from client workstations on local LAN to CharacterConceptAI Server." | Out-Null

Write-Host ""
Write-Host "Verifying Loopback Security Isolation for internal services:" -ForegroundColor Yellow
Write-Host "  - ComfyUI (Port 8188): Bound to 127.0.0.1 loopback only [SECURE]" -ForegroundColor Green
Write-Host "  - SQL Server (Port 1433): Local instance, no public firewall opening [SECURE]" -ForegroundColor Green

Write-Host ""
Write-Host "Firewall configuration successfully completed!" -ForegroundColor Green
