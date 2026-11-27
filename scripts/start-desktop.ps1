[CmdletBinding()]
param([string]$ServerUrl = "", [switch]$ClientOnly)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$desktopCandidates = @(
    (Join-Path $projectRoot 'dist\redesign\desktop\CharacterConceptAI.Desktop.exe'),
    (Join-Path $projectRoot 'src\CharacterConceptAI.Desktop\bin\Release\net10.0-windows\CharacterConceptAI.Desktop.exe')
)
$desktopExe = $desktopCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $desktopExe) { throw 'Chưa có bản build mới. Chạy dotnet build CharacterConceptAI.slnx -c Release trước.' }

if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    $configPath = Join-Path (Split-Path -Parent $desktopExe) 'client-config.json'
    if (Test-Path -LiteralPath $configPath) { $ServerUrl = (Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json).ServerBaseUrl }
    if (-not [string]::IsNullOrWhiteSpace($env:CHARACTER_CONCEPT_AI_SERVER_URL)) { $ServerUrl = $env:CHARACTER_CONCEPT_AI_SERVER_URL }
    if ([string]::IsNullOrWhiteSpace($ServerUrl)) { $ServerUrl = 'http://127.0.0.1:5000' }
}
$serverUri = [Uri]$ServerUrl
if (-not $serverUri.IsAbsoluteUri -or $serverUri.UserInfo -or $serverUri.Query -or $serverUri.Fragment -or
    ($serverUri.Scheme -ne 'https' -and -not ($serverUri.Scheme -eq 'http' -and $serverUri.IsLoopback))) {
    throw 'Dùng địa chỉ HTTPS cho máy chủ LAN. HTTP chỉ dành cho máy cục bộ.'
}

$healthy = $false
$serverContract = ''
try {
    $probe = Invoke-WebRequest -Uri ($ServerUrl.TrimEnd('/') + '/api/v1/health') -UseBasicParsing -TimeoutSec 3
    $healthy = $probe.StatusCode -eq 200
    $serverContract = ($probe.Content | ConvertFrom-Json).desktopContractVersion
} catch { }

if (-not $healthy -and -not $ClientOnly -and $serverUri.IsLoopback) {
    $serverCandidates = @(
        (Join-Path $projectRoot 'dist\redesign\server\CharacterConceptAI.Server.exe'),
        (Join-Path $projectRoot 'src\CharacterConceptAI.Server\bin\Release\net10.0\CharacterConceptAI.Server.exe')
    )
    $serverExe = $serverCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($serverExe) {
        # SQL/configuration must already exist; never migrate, seed passwords, or start a GPU workload here.
        $serverProcess = Start-Process -FilePath $serverExe -ArgumentList @('--urls', $ServerUrl.TrimEnd('/')) -WorkingDirectory (Split-Path -Parent $serverExe) -WindowStyle Hidden -PassThru
        for ($attempt = 0; $attempt -lt 12; $attempt++) {
            if ($serverProcess.HasExited) { break }
            try { $probe = Invoke-WebRequest -Uri ($ServerUrl.TrimEnd('/') + '/api/v1/health') -UseBasicParsing -TimeoutSec 1; $healthy = $probe.StatusCode -eq 200; $serverContract = ($probe.Content | ConvertFrom-Json).desktopContractVersion } catch { }
            if ($healthy) { break }
            Start-Sleep -Milliseconds 500
        }
    }
}
if ($healthy) {
    Write-Host 'Máy chủ đã phản hồi kiểm tra kết nối.'
    if ($serverContract -and $serverContract -ne 'uiux-v1') { Write-Warning 'Phiên bản máy chủ chưa phù hợp. Khởi động lại máy chủ bằng bản mới trong dist/redesign/server.' }
    elseif (-not $serverContract) { Write-Warning 'Máy chủ hiện có chưa báo hợp đồng UI mới. Nếu thư viện không tải được, hãy khởi động lại server bằng bản mới; launcher không tự dừng tiến trình đang dùng.' }
}
else { Write-Warning 'Máy chủ chưa phản hồi. Ứng dụng vẫn mở để bạn kiểm tra địa chỉ/kết nối. Không có dữ liệu hoặc tài khoản được tạo tự động.' }

Start-Process -FilePath $desktopExe -ArgumentList @('--server', $ServerUrl.TrimEnd('/')) -WorkingDirectory (Split-Path -Parent $desktopExe)
Write-Host 'Đã mở Gula Studio — tự động kết nối máy chủ cục bộ.'
