[CmdletBinding()]
param(
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path "$ScriptDir\..\..").Path

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "dist\database"
}
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$sqlFile = Join-Path $OutputDir "deploy_migrations.sql"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   CharacterConceptAI - EF Core Idempotent SQL Generator  " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Project: src\CharacterConceptAI.Server"
Write-Host "Output Script: $sqlFile"
Write-Host ""

$serverProj = Join-Path $RepoRoot "src\CharacterConceptAI.Server\CharacterConceptAI.Server.csproj"

& dotnet ef migrations script --idempotent --project $serverProj --output $sqlFile

if ($LASTEXITCODE -ne 0) {
    throw "Failed to generate EF Core migration SQL script."
}

Write-Host ""
Write-Host "Idempotent deployment SQL script generated successfully!" -ForegroundColor Green
Write-Host "DBAs can review and run this script via SQL Server Management Studio (SSMS) or sqlcmd." -ForegroundColor Cyan
