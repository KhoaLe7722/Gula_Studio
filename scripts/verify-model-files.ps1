# Optional read-only model file presence+SHA256; specify YOUR ComfyUI folder.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ComfyRoot)
$ErrorActionPreference = 'Stop'
$files = @(
 'models\diffusion_models\z_image_turbo_int8_convrot.safetensors',
 'models\text_encoders\qwen_3_4b_fp8_mixed.safetensors',
 'models\vae\ae.safetensors'
)
foreach ($file in $files) {
  $p = Join-Path $ComfyRoot $file
  if (-not (Test-Path $p -PathType Leaf)) { Write-Warning "MISSING: $p"; continue }
  $f = Get-Item $p
  Write-Host "PRESENT: $p | $([math]::Round($f.Length / 1GB,3)) GiB"
  $sha = Get-FileHash -Path $p -Algorithm SHA256
  Write-Host ("SHA256: " + $sha.Hash)
}
Write-Host 'Hashes are recorded locally only. Compare to trusted published checksum if available. Hashing alone does not prove correct model execution.'
