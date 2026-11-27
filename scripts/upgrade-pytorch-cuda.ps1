[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "  CharacterConceptAI - PyTorch CUDA Upgrade Helper (Z-Image)     " -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# 1. Check NVIDIA driver version first to prevent breaking CUDA support
$smi = & nvidia-smi --query-gpu=name,driver_version --format=csv,noheader 2>$null
if (-not $smi) {
    throw "Khong tim thay nvidia-smi. Vui long kiem tra driver NVIDIA."
}
$parts = $smi.Split(',') | ForEach-Object { $_.Trim() }
$gpuName = $parts[0]
$driverStr = $parts[1]
$driverVer = [double]$driverStr

Write-Host "GPU:            $gpuName"
Write-Host "NVIDIA Driver:  $driverStr"

if ($driverVer -lt 570.0 -and -not $Force) {
    Write-Host ""
    Write-Warning "Driver NVIDIA hien tai ($driverStr) qua cu (chi ho tro toi da CUDA 12.0)."
    Write-Host "De mo khoa PyTorch 2.8+ (cu128/cu130), comfy_kitchen CUDA kernel va DynamicVRAM:" -ForegroundColor Yellow
    Write-Host "  Buoc 1: Cap nhat NVIDIA Driver cho RTX 4050 Laptop len ban >= 572.x (khuyen nghi >= 580.x)" -ForegroundColor White
    Write-Host "          Tai tai: https://www.nvidia.com/en-us/drivers/ (hoac mo NVIDIA App)." -ForegroundColor Gray
    Write-Host "  Buoc 2: Khoi dong lai may, sau do chay lai script nay:" -ForegroundColor White
    Write-Host "          powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor Green
    exit 2
}

$indexUrl = if ($driverVer -ge 580.0) {
    "https://download.pytorch.org/whl/cu130"
} else {
    "https://download.pytorch.org/whl/cu128"
}

Write-Host "Driver dat yeu cau! Dang nang cap PyTorch tu: $indexUrl" -ForegroundColor Green
Write-Host "Vui long dong ComfyUI truoc khi tiep tuc..." -ForegroundColor Yellow

& python -m pip install --no-cache-dir --upgrade torch torchvision torchaudio --index-url $indexUrl

Write-Host "`nKiem tra lai PyTorch + CUDA:" -ForegroundColor Cyan
& python -c "import torch; print('PyTorch:', torch.__version__, '| CUDA:', torch.version.cuda, '| Available:', torch.cuda.is_available())"
Write-Host "Hoan tat nang cap!" -ForegroundColor Green
