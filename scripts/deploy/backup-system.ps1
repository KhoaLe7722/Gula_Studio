<#
.SYNOPSIS
    Coordinated Backup Script for CharacterConceptAI (Database + Media).
.DESCRIPTION
    Creates a synchronized, verified backup of the CharacterConceptAI SQL Server database
    and the physical media storage root. Generates a cryptographic manifest (SHA-256)
    for both the database backup file and every stored image asset.
.PARAMETER ServerInstance
    SQL Server instance name (defaults to 'localhost').
.PARAMETER DatabaseName
    Database name to back up (defaults to 'CharacterConceptAI_DEV').
.PARAMETER MediaRoot
    Path to the physical media directory.
.PARAMETER BackupDestination
    Root folder where timestamped backup packages will be saved.
.PARAMETER BackupId
    Custom backup identifier (defaults to CCA_BACKUP_yyyyMMdd_HHmmss).
#>
[CmdletBinding()]
param(
    [string]$ServerInstance = "localhost",
    [string]$DatabaseName = "CharacterConceptAI_DEV",
    [string]$MediaRoot = "",
    [string]$BackupDestination = "",
    [string]$BackupId = ""
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " CharacterConceptAI - Coordinated System Backup" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Resolve BackupId and Destination
if ([string]::IsNullOrWhiteSpace($BackupId)) {
    $BackupId = "CCA_BACKUP_" + (Get-Date -Format "yyyyMMdd_HHmmss")
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

if ([string]::IsNullOrWhiteSpace($BackupDestination)) {
    if (Test-Path "D:\CharacterConceptAIData\Backups") {
        $BackupDestination = "D:\CharacterConceptAIData\Backups"
    } else {
        $BackupDestination = Join-Path $repoRoot "dist\backups"
    }
}

$targetBackupDir = Join-Path $BackupDestination $BackupId
New-Item -Path $targetBackupDir -ItemType Directory -Force | Out-Null
Write-Host "[1/5] Backup target directory created: $targetBackupDir" -ForegroundColor Green

# 2. Resolve MediaRoot
if ([string]::IsNullOrWhiteSpace($MediaRoot)) {
    $candidates = @(
        "D:\CharacterConceptAIData\Media",
        (Join-Path $repoRoot "src\CharacterConceptAI.Server\MediaStorage"),
        (Join-Path $repoRoot "tests\CharacterConceptAI.Tests\bin\Debug\net10.0\MediaStorage")
    )
    foreach ($cand in $candidates) {
        if (Test-Path $cand) {
            $MediaRoot = $cand
            break
        }
    }
}

if (-not (Test-Path $MediaRoot)) {
    Write-Warning "Media root '$MediaRoot' does not exist. Creating empty media directory for backup."
    $MediaRoot = Join-Path $targetBackupDir "Media_Empty"
    New-Item -Path $MediaRoot -ItemType Directory -Force | Out-Null
}
Write-Host "[2/5] Using MediaRoot: $MediaRoot" -ForegroundColor Green

# 3. Perform SQL Server Backup
$bakFileName = "$DatabaseName.bak"
$bakFilePath = Join-Path $targetBackupDir $bakFileName

Write-Host "[3/5] Performing SQL Server native backup of '$DatabaseName' to '$bakFilePath'..." -ForegroundColor Yellow
$backupSql = "BACKUP DATABASE [$DatabaseName] TO DISK = N'$bakFilePath' WITH FORMAT, INIT, STATS = 20, CHECKSUM;"

$sqlcmdCmd = Get-Command "sqlcmd" -ErrorAction SilentlyContinue
if ($sqlcmdCmd) {
    & sqlcmd -S $ServerInstance -d master -Q $backupSql -b
    if ($LASTEXITCODE -ne 0) {
        throw "sqlcmd backup failed with exit code $LASTEXITCODE"
    }
} else {
    # Fallback to ADO.NET
    Add-Type -AssemblyName "System.Data"
    $connStr = "Server=$ServerInstance;Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $backupSql
        $cmd.CommandTimeout = 300
        $cmd.ExecuteNonQuery() | Out-Null
    } finally {
        $conn.Close()
    }
}

if (-not (Test-Path $bakFilePath)) {
    throw "Backup file '$bakFilePath' was not created."
}

$bakHash = (Get-FileHash -Path $bakFilePath -Algorithm SHA256).Hash
$bakSize = (Get-Item $bakFilePath).Length
Write-Host "      Database backup successful. Size: $([Math]::Round($bakSize / 1MB, 2)) MB | SHA256: $bakHash" -ForegroundColor Green

# 4. Copy and Hash Media Assets
Write-Host "[4/5] Copying media assets and calculating hashes..." -ForegroundColor Yellow
$targetMediaDir = Join-Path $targetBackupDir "Media"
New-Item -Path $targetMediaDir -ItemType Directory -Force | Out-Null

$mediaFilesList = @()
if (Test-Path $MediaRoot) {
    $sourceFiles = Get-ChildItem -Path $MediaRoot -Recurse -File
    $count = 0
    foreach ($file in $sourceFiles) {
        $count++
        $relPath = $file.FullName.Substring($MediaRoot.Length).TrimStart('\', '/')
        $destFile = Join-Path $targetMediaDir $relPath
        $destParent = Split-Path $destFile -Parent
        if (-not (Test-Path $destParent)) {
            New-Item -Path $destParent -ItemType Directory -Force | Out-Null
        }
        Copy-Item -Path $file.FullName -Destination $destFile -Force

        $fileHash = (Get-FileHash -Path $destFile -Algorithm SHA256).Hash.ToLowerInvariant()
        $mediaFilesList += [PSCustomObject]@{
            RelativePath = $relPath.Replace('\', '/')
            SizeBytes    = $file.Length
            Sha256       = $fileHash
        }
    }
    Write-Host "      Copied and hashed $count media files." -ForegroundColor Green
}

# 5. Query DB ImageAssets Metadata for Cross-Verification
Write-Host "[5/5] Extracting database asset catalog for verification..." -ForegroundColor Yellow
$querySql = "SELECT CAST(Id AS NVARCHAR(36)) AS Id, CAST(ProjectId AS NVARCHAR(36)) AS ProjectId, StorageKey, Sha256, SizeBytes FROM [$DatabaseName].dbo.ImageAssets"
$dbAssetsList = @()

try {
    Add-Type -AssemblyName "System.Data"
    $connStr = "Server=$ServerInstance;Database=$DatabaseName;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $querySql
        $reader = $cmd.ExecuteReader()
        while ($reader.Read()) {
            $dbAssetsList += [PSCustomObject]@{
                Id         = $reader["Id"].ToString()
                ProjectId  = $reader["ProjectId"].ToString()
                StorageKey = $reader["StorageKey"].ToString()
                Sha256     = $reader["Sha256"].ToString().ToLowerInvariant()
                SizeBytes  = [int64]$reader["SizeBytes"]
            }
        }
        $reader.Close()
    } finally {
        $conn.Close()
    }
} catch {
    Write-Warning "Could not extract DB asset catalog via ADO.NET: $_"
}

# 6. Generate Manifest
$manifest = [PSCustomObject]@{
    ManifestVersion   = "1.0"
    BackupId          = $BackupId
    TimestampUtc      = (Get-Date).ToUniversalTime().ToString("o")
    ServerInstance    = $ServerInstance
    DatabaseName      = $DatabaseName
    BackupFileName    = $bakFileName
    BackupFileSha256  = $bakHash
    BackupFileBytes   = $bakSize
    MediaFilesCount   = $mediaFilesList.Count
    DbAssetsCount     = $dbAssetsList.Count
    MediaFiles        = $mediaFilesList
    DatabaseAssets    = $dbAssetsList
    HostEnvironment   = [PSCustomObject]@{
        MachineName   = $env:COMPUTERNAME
        OsVersion     = [System.Environment]::OSVersion.VersionString
        UserName      = $env:USERNAME
    }
}

$manifestPath = Join-Path $targetBackupDir "manifest.json"
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Encoding UTF8

Write-Host "==========================================================" -ForegroundColor Green
Write-Host " BACKUP COMPLETE" -ForegroundColor Green
Write-Host " Backup ID:        $BackupId" -ForegroundColor White
Write-Host " Manifest:         $manifestPath" -ForegroundColor White
Write-Host " Database Backup:  $bakFilePath ($([Math]::Round($bakSize / 1MB, 2)) MB)" -ForegroundColor White
Write-Host " Media Files:      $($mediaFilesList.Count)" -ForegroundColor White
Write-Host " DB Asset Records: $($dbAssetsList.Count)" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Green
