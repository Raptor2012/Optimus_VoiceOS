[CmdletBinding()]
param(
    [string]$ModelsRoot = $(if ($env:OPTIMUS_MODELS_DIR) { $env:OPTIMUS_MODELS_DIR } else { "D:\SamHaydenVoiceTool\models" }),
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# SHA-256 values are the immutable Git-LFS object hashes advertised by Hugging Face.
$models = @(
    @{
        Id = "qwen3.5-4b-q4_k_m"
        RelativePath = "qwen3.5-4b-q4_k_m\Qwen_Qwen3.5-4B-Q4_K_M.gguf"
        Url = "https://huggingface.co/bartowski/Qwen_Qwen3.5-4B-GGUF/resolve/main/Qwen_Qwen3.5-4B-Q4_K_M.gguf?download=true"
        Sha256 = "13c16f426047e2de38cd075bdade4a7bcbc8c774384876f677740cda65f8a983"
    },
    @{
        Id = "holo3.1-4b-q4_k_m"
        RelativePath = "holo3.1-4b-q4_k_m\Holo-3.1-4B.Q4_K_M.gguf"
        Url = "https://huggingface.co/prithivMLmods/Holo-3.1-4B-GGUF/resolve/main/Holo-3.1-4B.Q4_K_M.gguf?download=true"
        Sha256 = "44eaed360316d00fb66c2df346b3e307e8539aa08bb2ffca9a368fabfdfdab12"
    }
)

foreach ($model in $models) {
    $destination = Join-Path $ModelsRoot $model.RelativePath
    $directory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Force -Path $directory | Out-Null

    if ($Force -or -not (Test-Path -LiteralPath $destination)) {
        Write-Host "Downloading $($model.Id)..."
        Invoke-WebRequest -Uri $model.Url -OutFile $destination
    } else {
        Write-Host "Using existing $destination"
    }

    $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $model.Sha256) {
        throw "Checksum mismatch for $($model.Id): expected $($model.Sha256), got $actual"
    }
    Write-Host "Verified $($model.Id) ($actual)"
}
