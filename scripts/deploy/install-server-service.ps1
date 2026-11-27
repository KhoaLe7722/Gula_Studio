[CmdletBinding()]
param(
    [string]$ServiceName = "CharacterConceptAIServer",
    [string]$DisplayName = "CharacterConceptAI Server Service",
    [string]$DataDirectory = "D:\CharacterConceptAIData",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path "$ScriptDir\..\..").Path
$ServerExe = Join-Path $RepoRoot "dist\server\CharacterConceptAI.Server.exe"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   CharacterConceptAI - Windows Service Management Tool   " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "Administrator privileges required to register or modify Windows Services."
    Write-Warning "Please rerun PowerShell as Administrator."
    Write-Host "Displaying proposed service configuration:" -ForegroundColor Yellow
    Write-Host "  Service Name: $ServiceName"
    Write-Host "  Binary Path:  $ServerExe"
    Write-Host "  Startup Type: Automatic"
    Write-Host "  Failure Recovery: Auto-restart on crash (after 60s)"
    Write-Host "  Data Directory:   $DataDirectory (Media, Logs, Backups)"
    return
}

if ($Uninstall) {
    Write-Host "Uninstalling service $ServiceName..." -ForegroundColor Yellow
    if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName
        Write-Host "Service $ServiceName removed successfully." -ForegroundColor Green
    }
    else {
        Write-Host "Service $ServiceName is not installed." -ForegroundColor Gray
    }
    return
}

# 1. Ensure Data Directory and NTFS Permissions
Write-Host "Configuring Data Directories at $DataDirectory..." -ForegroundColor Yellow
$mediaDir = Join-Path $DataDirectory "Media"
$logsDir = Join-Path $DataDirectory "Logs"
$backupsDir = Join-Path $DataDirectory "Backups"

New-Item -ItemType Directory -Path $mediaDir -Force | Out-Null
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
New-Item -ItemType Directory -Path $backupsDir -Force | Out-Null

# Configure ACL for Local Service / Network Service
try {
    $acl = Get-Acl $DataDirectory
    $accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\NETWORK SERVICE", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.AddAccessRule($accessRule)
    Set-Acl -Path $DataDirectory -AclObject $acl
    Write-Host "Configured NTFS FullControl permissions for NETWORK SERVICE on $DataDirectory." -ForegroundColor Green
}
catch {
    Write-Warning "Could not update ACL automatically: $_"
}

# 2. Check executable existence
if (-not (Test-Path $ServerExe)) {
    throw "Server executable not found at '$ServerExe'. Please run publish-all.ps1 first."
}

# 3. Create or update Windows Service using sc.exe
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Service $ServiceName already exists. Updating binary path..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe config $ServiceName binPath= "`"$ServerExe`"" start= auto displayname= "$DisplayName"
}
else {
    Write-Host "Creating Windows Service $ServiceName..." -ForegroundColor Yellow
    & sc.exe create $ServiceName binPath= "`"$ServerExe`"" start= auto displayname= "$DisplayName"
}

if ($LASTEXITCODE -ne 0) {
    throw "Failed to create service $ServiceName."
}

# 4. Configure automatic recovery on failure
Write-Host "Configuring automatic service failure recovery actions..." -ForegroundColor Yellow
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000//

# 5. Set description
& sc.exe description $ServiceName "CharacterConceptAI Server Web API & Background Queue Worker for collaborative character concept art generation."

Write-Host ""
Write-Host "Windows Service $ServiceName successfully installed and configured!" -ForegroundColor Green
Write-Host "To start the service: Start-Service -Name $ServiceName" -ForegroundColor Cyan
Write-Host "To inspect status:    Get-Service -Name $ServiceName" -ForegroundColor Cyan
