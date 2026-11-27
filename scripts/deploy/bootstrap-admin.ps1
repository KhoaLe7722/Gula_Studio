[CmdletBinding()]
param(
    [string]$Email = "admin@cca.local",
    [string]$Password = ""
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   CharacterConceptAI - System Administrator Bootstrap   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($Password)) {
    # Check environment variable first
    if (-not [string]::IsNullOrWhiteSpace($env:CHARACTER_CONCEPT_AI_ADMIN_PASSWORD)) {
        $Password = $env:CHARACTER_CONCEPT_AI_ADMIN_PASSWORD
        Write-Host "Read admin password from environment variable." -ForegroundColor Gray
    }
    else {
        # Prompt interactively
        $secPwd = Read-Host -Prompt "Enter secure password for initial administrator ($Email)" -AsSecureString
        $BSTR = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secPwd)
        $Password = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($BSTR)
        [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($BSTR)
    }
}

if ([string]::IsNullOrWhiteSpace($Password) -or $Password.Length -lt 12) {
    throw "Admin password must be at least 12 characters long with uppercase, lowercase, digit and symbol."
}

Write-Host "Setting environment variables for one-time initialization..." -ForegroundColor Yellow
$env:Admin__BootstrapEnabled = "true"
$env:Admin__Email = $Email
$env:Admin__Password = $Password

Write-Host "Administrator bootstrap configuration ready." -ForegroundColor Green
Write-Host "When CharacterConceptAI.Server starts up, it will provision the initial admin only if no SystemAdmin exists; it never resets passwords for '$Email'." -ForegroundColor Cyan
Write-Host "Clear these environment variables after first startup for security." -ForegroundColor Yellow
