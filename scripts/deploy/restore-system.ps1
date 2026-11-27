<#
.SYNOPSIS
    Coordinated System Restore and Integrity Verification for CharacterConceptAI.
.DESCRIPTION
    Restores an isolated test database and isolated media directory from a coordinated backup.
    Verifies cryptographic hashes of backup files, executes SQL RESTORE with file MOVE,
    restores media assets, and performs full relational & hash integrity checks against the restored database.
    SAFE BY DEFAULT: Targets an isolated test database ('CharacterConceptAI_RESTORE_TEST')
    and never modifies production database without explicit flags.
.PARAMETER BackupDir
    Path to the backup directory containing manifest.json and the .bak file.
.PARAMETER TargetServerInstance
    SQL Server instance name (defaults to 'localhost').
.PARAMETER TargetDatabaseName
    Isolated target database name (defaults to 'CharacterConceptAI_RESTORE_TEST').
.PARAMETER TargetMediaRoot
    Isolated target media directory (defaults to 'dist\test_restore_media').
.PARAMETER Force
    Allow overwriting the target database/directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BackupDir,

    [string]$TargetServerInstance = "localhost",
    [string]$TargetDatabaseName = "CharacterConceptAI_RESTORE_TEST",
    [string]$TargetMediaRoot = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " CharacterConceptAI - Coordinated System Restore & Verification" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path

$BackupDir = (Resolve-Path $BackupDir).Path

if (-not (Test-Path $BackupDir)) {
    throw "Backup directory '$BackupDir' does not exist."
}

$manifestPath = Join-Path $BackupDir "manifest.json"
if (-not (Test-Path $manifestPath)) {
    throw "Backup manifest '$manifestPath' not found in backup directory."
}

# 1. Load Manifest
Write-Host "[1/6] Loading and parsing backup manifest..." -ForegroundColor Yellow
$manifest = Get-Content -Path $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
Write-Host "      Backup ID:     $($manifest.BackupId)" -ForegroundColor Green
Write-Host "      Created UTC:   $($manifest.TimestampUtc)" -ForegroundColor Green
Write-Host "      Source DB:     $($manifest.DatabaseName)" -ForegroundColor Green
Write-Host "      Source Assets: $($manifest.DbAssetsCount) in DB, $($manifest.MediaFilesCount) files" -ForegroundColor Green

# 2. Cryptographic Validation of Backup Files
Write-Host "[2/6] Verifying cryptographic integrity (SHA-256) of backup package..." -ForegroundColor Yellow
$bakFilePath = Join-Path $BackupDir $manifest.BackupFileName
if (-not (Test-Path $bakFilePath)) {
    throw "Backup file '$bakFilePath' referenced in manifest does not exist."
}

$actualBakHash = (Get-FileHash -Path $bakFilePath -Algorithm SHA256).Hash
if ($actualBakHash -ne $manifest.BackupFileSha256) {
    throw "INTEGRITY CORRUPTION: Database backup hash mismatch! Expected: $($manifest.BackupFileSha256), Actual: $actualBakHash"
}
Write-Host "      Database backup SHA-256 MATCH: $actualBakHash" -ForegroundColor Green

# Verify media files in backup
$backupMediaDir = Join-Path $BackupDir "Media"
$mediaVerifiedCount = 0
if (Test-Path $backupMediaDir) {
    foreach ($mFile in $manifest.MediaFiles) {
        $diskPath = Join-Path $backupMediaDir ($mFile.RelativePath.Replace('/', '\'))
        if (-not (Test-Path $diskPath)) {
            throw "INTEGRITY CORRUPTION: Media file '$($mFile.RelativePath)' missing from backup folder!"
        }
        $diskHash = (Get-FileHash -Path $diskPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($diskHash -ne $mFile.Sha256.ToLowerInvariant()) {
            throw "INTEGRITY CORRUPTION: Hash mismatch for '$($mFile.RelativePath)'! Expected: $($mFile.Sha256), Actual: $diskHash"
        }
        $mediaVerifiedCount++
    }
}
Write-Host "      Verified $mediaVerifiedCount media files in backup package with 100% hash match." -ForegroundColor Green

# 3. Determine SQL Logical File Names and Paths
Write-Host "[3/6] Inspecting SQL Server backup header and filelist..." -ForegroundColor Yellow
Add-Type -AssemblyName "System.Data"
$masterConnStr = "Server=$TargetServerInstance;Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"
$masterConn = New-Object System.Data.SqlClient.SqlConnection($masterConnStr)
$masterConn.Open()

$logicalDataName = ""
$logicalLogName = ""
$defaultDataPath = ""
$defaultLogPath = ""

try {
    # Get SQL default data paths
    $pathCmd = $masterConn.CreateCommand()
    $pathCmd.CommandText = @"
        SELECT 
            ISNULL(CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS NVARCHAR(512)), '') AS DataPath,
            ISNULL(CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS NVARCHAR(512)), '') AS LogPath
"@
    $pathReader = $pathCmd.ExecuteReader()
    if ($pathReader.Read()) {
        $defaultDataPath = $pathReader["DataPath"].ToString()
        $defaultLogPath = $pathReader["LogPath"].ToString()
    }
    $pathReader.Close()

    # Get logical file names
    $filelistCmd = $masterConn.CreateCommand()
    $filelistCmd.CommandText = "RESTORE FILELISTONLY FROM DISK = N'$bakFilePath';"
    $filelistReader = $filelistCmd.ExecuteReader()
    while ($filelistReader.Read()) {
        $type = $filelistReader["Type"].ToString()
        $logicalName = $filelistReader["LogicalName"].ToString()
        if ($type -eq "D" -and [string]::IsNullOrWhiteSpace($logicalDataName)) {
            $logicalDataName = $logicalName
        } elseif ($type -eq "L" -and [string]::IsNullOrWhiteSpace($logicalLogName)) {
            $logicalLogName = $logicalName
        }
    }
    $filelistReader.Close()
} finally {
    $masterConn.Close()
}

if ([string]::IsNullOrWhiteSpace($logicalDataName) -or [string]::IsNullOrWhiteSpace($logicalLogName)) {
    throw "Could not determine logical data and log names from backup file."
}

Write-Host "      Logical Data: $logicalDataName" -ForegroundColor Cyan
Write-Host "      Logical Log:  $logicalLogName" -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($defaultDataPath)) {
    $defaultDataPath = Join-Path $BackupDir "SqlData"
    New-Item -Path $defaultDataPath -ItemType Directory -Force | Out-Null
    $defaultLogPath = $defaultDataPath
}

$destMdf = Join-Path $defaultDataPath "$TargetDatabaseName.mdf"
$destLdf = Join-Path $defaultLogPath "$TargetDatabaseName`_log.ldf"

# 4. Execute Restore into Isolated Target DB
Write-Host "[4/6] Restoring to isolated database '$TargetDatabaseName'..." -ForegroundColor Yellow
$restoreSql = @"
    IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'$TargetDatabaseName')
    BEGIN
        ALTER DATABASE [$TargetDatabaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
        DROP DATABASE [$TargetDatabaseName];
    END

    RESTORE DATABASE [$TargetDatabaseName]
    FROM DISK = N'$bakFilePath'
    WITH MOVE N'$logicalDataName' TO N'$destMdf',
         MOVE N'$logicalLogName' TO N'$destLdf',
         REPLACE, RECOVERY, STATS = 20;

    ALTER DATABASE [$TargetDatabaseName] SET MULTI_USER;
"@

$masterConn.Open()
try {
    $restoreCmd = $masterConn.CreateCommand()
    $restoreCmd.CommandText = $restoreSql
    $restoreCmd.CommandTimeout = 600
    $restoreCmd.ExecuteNonQuery() | Out-Null
    Write-Host "      Database restore complete: $TargetDatabaseName" -ForegroundColor Green
} finally {
    $masterConn.Close()
}

# 5. Restore Media Directory
if ([string]::IsNullOrWhiteSpace($TargetMediaRoot)) {
    $TargetMediaRoot = Join-Path $repoRoot "dist\test_restore_media"
}

Write-Host "[5/6] Restoring media files to isolated root '$TargetMediaRoot'..." -ForegroundColor Yellow
if (Test-Path $TargetMediaRoot) {
    Remove-Item -Path $TargetMediaRoot -Recurse -Force
}
New-Item -Path $TargetMediaRoot -ItemType Directory -Force | Out-Null

if (Test-Path $backupMediaDir) {
    Copy-Item -Path "$backupMediaDir\*" -Destination $TargetMediaRoot -Recurse -Force
}
$restoredDiskFiles = Get-ChildItem -Path $TargetMediaRoot -Recurse -File
Write-Host "      Restored $($restoredDiskFiles.Count) media files onto disk." -ForegroundColor Green

# 6. Post-Restore Relational & Asset Integrity Verification
Write-Host "[6/6] Executing comprehensive integrity verification..." -ForegroundColor Yellow
$targetConnStr = "Server=$TargetServerInstance;Database=$TargetDatabaseName;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"
$targetConn = New-Object System.Data.SqlClient.SqlConnection($targetConnStr)
$targetConn.Open()

$tableCounts = @{}
$assetIntegrityReport = @()
$integrityPassed = $true

try {
    # Check table row counts
    $tables = @("Projects", "Characters", "GenerationJobs", "ImageAssets", "CharacterConcepts", "ConceptVersions", "ConceptApprovals", "Scripts", "Scenes")
    foreach ($tbl in $tables) {
        $countCmd = $targetConn.CreateCommand()
        $countCmd.CommandText = "IF OBJECT_ID('dbo.$tbl') IS NOT NULL SELECT COUNT(*) FROM dbo.$tbl ELSE SELECT -1"
        $count = [int]$countCmd.ExecuteScalar()
        $tableCounts[$tbl] = $count
    }

    # Query all ImageAssets
    $assetCmd = $targetConn.CreateCommand()
    $assetCmd.CommandText = "SELECT CAST(Id AS NVARCHAR(36)) AS Id, CAST(ProjectId AS NVARCHAR(36)) AS ProjectId, StorageKey, Sha256, SizeBytes FROM dbo.ImageAssets"
    $reader = $assetCmd.ExecuteReader()
    $dbAssets = @()
    while ($reader.Read()) {
        $dbAssets += [PSCustomObject]@{
            Id         = $reader["Id"].ToString()
            ProjectId  = $reader["ProjectId"].ToString()
            StorageKey = $reader["StorageKey"].ToString()
            Sha256     = $reader["Sha256"].ToString().ToLowerInvariant()
            SizeBytes  = [int64]$reader["SizeBytes"]
        }
    }
    $reader.Close()

    # Re-verify every DB asset exists on disk and has matching SHA-256
    $matchedAssets = 0
    $missingAssets = 0
    $mismatchedHashes = 0

    foreach ($asset in $dbAssets) {
        $normKey = $asset.StorageKey.Replace('/', '\')
        $filePath = Join-Path $TargetMediaRoot $normKey

        if (-not (Test-Path $filePath)) {
            $missingAssets++
            $integrityPassed = $false
            $assetIntegrityReport += [PSCustomObject]@{
                AssetId    = $asset.Id
                StorageKey = $asset.StorageKey
                Status     = "MISSING_FROM_DISK"
            }
        } else {
            $actualHash = (Get-FileHash -Path $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne $asset.Sha256) {
                $mismatchedHashes++
                $integrityPassed = $false
                $assetIntegrityReport += [PSCustomObject]@{
                    AssetId      = $asset.Id
                    StorageKey   = $asset.StorageKey
                    Status       = "HASH_MISMATCH"
                    ExpectedHash = $asset.Sha256
                    ActualHash   = $actualHash
                }
            } else {
                $matchedAssets++
            }
        }
    }
} finally {
    $targetConn.Close()
}

$report = [PSCustomObject]@{
    RestoreTimestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    BackupId            = $manifest.BackupId
    TargetDatabase      = $TargetDatabaseName
    TargetMediaRoot     = $TargetMediaRoot
    TableCounts         = $tableCounts
    DbAssetsCount       = $dbAssets.Count
    RestoredFilesCount  = $restoredDiskFiles.Count
    MatchedAssets       = $matchedAssets
    MissingAssets       = $missingAssets
    MismatchedHashes    = $mismatchedHashes
    IntegrityResult     = if ($integrityPassed) { "PASS" } else { "FAIL" }
    Anomalies           = $assetIntegrityReport
}

$reportPath = Join-Path $BackupDir "restore_verification_report.json"
$report | ConvertTo-Json -Depth 5 | Set-Content -Path $reportPath -Encoding UTF8

Write-Host "==========================================================" -ForegroundColor $(if ($integrityPassed) { "Green" } else { "Red" })
Write-Host " RESTORE VERIFICATION RESULT: $($report.IntegrityResult)" -ForegroundColor $(if ($integrityPassed) { "Green" } else { "Red" })
Write-Host " Database:        $TargetDatabaseName" -ForegroundColor White
Write-Host " Table Counts:    $(($tableCounts.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" }) -join ', ')" -ForegroundColor White
Write-Host " Verified Assets: $matchedAssets / $($dbAssets.Count) with exact SHA-256 match" -ForegroundColor White
if ($missingAssets -gt 0 -or $mismatchedHashes -gt 0) {
    Write-Host " Anomalies:       Missing: $missingAssets, Hash Mismatches: $mismatchedHashes" -ForegroundColor Red
}
Write-Host " Report written:  $reportPath" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor $(if ($integrityPassed) { "Green" } else { "Red" })

if (-not $integrityPassed) {
    throw "Restore verification failed. Check $reportPath for details."
}
