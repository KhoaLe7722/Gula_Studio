[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path "$ScriptDir\..\..").Path
$DistDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $RepoRoot ("dist\releases\release-{0}-{1}" -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N').Substring(0, 8))
} else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
$DesktopDist = Join-Path $DistDir "desktop"
$ServerDist = Join-Path $DistDir "server"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "   CharacterConceptAI - Self-Contained Publishing Tool    " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Repo Root: $RepoRoot"
Write-Host "Output Dist: $DistDir"
Write-Host "Target Runtime: $Runtime"
Write-Host "Configuration: $Configuration"
Write-Host ""

if (Test-Path -LiteralPath $DistDir) {
    throw "Refusing to overwrite an existing release directory. Choose a fresh OutputDirectory to preserve media, configuration and DataProtection keys."
}
New-Item -ItemType Directory -Path $DesktopDist -Force | Out-Null
New-Item -ItemType Directory -Path $ServerDist -Force | Out-Null

# 1. Publish Desktop Client (WPF Self-Contained win-x64)
Write-Host ">>> [1/2] Publishing Desktop Client (WPF self-contained win-x64)..." -ForegroundColor Yellow
$desktopProj = Join-Path $RepoRoot "src\CharacterConceptAI.Desktop\CharacterConceptAI.Desktop.csproj"
& dotnet publish $desktopProj -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true -o $DesktopDist

if ($LASTEXITCODE -ne 0) {
    throw "Failed to publish Desktop Client."
}
Write-Host "Desktop published successfully to: $DesktopDist" -ForegroundColor Green

# 2. Publish Server (Windows Service Self-Contained win-x64)
Write-Host ">>> [2/2] Publishing Server (Windows Service self-contained win-x64)..." -ForegroundColor Yellow
$serverProj = Join-Path $RepoRoot "src\CharacterConceptAI.Server\CharacterConceptAI.Server.csproj"
& dotnet publish $serverProj -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=false -o $ServerDist

if ($LASTEXITCODE -ne 0) {
    throw "Failed to publish Server."
}
Write-Host "Server published successfully to: $ServerDist" -ForegroundColor Green

# 3. Generate SHA-256 Checksums and Manifest
Write-Host ">>> Generating SHA-256 release checksums and manifest..." -ForegroundColor Yellow
$checksums = [System.Collections.Generic.List[string]]::new()
$manifestArtifacts = [System.Collections.Generic.List[object]]::new()

$desktopExe = Get-Item (Join-Path $DesktopDist "CharacterConceptAI.Desktop.exe")
if ($desktopExe) {
    $hash = (Get-FileHash $desktopExe.FullName -Algorithm SHA256).Hash
    $checksums.Add("$hash  desktop/CharacterConceptAI.Desktop.exe")
    $manifestArtifacts.Add(@{
        Component = "Desktop Client"
        File = "desktop/CharacterConceptAI.Desktop.exe"
        SizeBytes = $desktopExe.Length
        Sha256 = $hash
    })
}

$serverExe = Get-Item (Join-Path $ServerDist "CharacterConceptAI.Server.exe")
if ($serverExe) {
    $hash = (Get-FileHash $serverExe.FullName -Algorithm SHA256).Hash
    $checksums.Add("$hash  server/CharacterConceptAI.Server.exe")
    $manifestArtifacts.Add(@{
        Component = "Server Windows Service"
        File = "server/CharacterConceptAI.Server.exe"
        SizeBytes = $serverExe.Length
        Sha256 = $hash
    })
}

$checksumPath = Join-Path $DistDir "CHECKSUMS.txt"
[System.IO.File]::WriteAllLines($checksumPath, $checksums)
Write-Host "Checksums written to: $checksumPath" -ForegroundColor Cyan

$manifest = @{
    PublishedAt = (Get-Date).ToString("o")
    Runtime = $Runtime
    Configuration = $Configuration
    Artifacts = $manifestArtifacts
}
$manifestPath = Join-Path $DistDir "release_manifest.json"
$manifestJson = $manifest | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($manifestPath, $manifestJson)
Write-Host "Release manifest written to: $manifestPath" -ForegroundColor Cyan

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   PUBLISH COMPLETE! All artifacts ready for deployment. " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
