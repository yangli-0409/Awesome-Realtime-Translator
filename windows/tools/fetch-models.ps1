<#
.SYNOPSIS
  下载本机识别和翻译需要的模型文件。

.DESCRIPTION
  正式安装包里自带模型，这个脚本是给从源码构建的人用的。

  为什么应用自己不下载：产品的核心承诺是「全程不联网」。一旦应用在运行时
  发网络请求，这条承诺就破了，而且用户没法验证它只下了模型。所以下载这件事
  要么由安装包完成，要么由开发者显式跑这个脚本——应用本身永远不碰网络。

.EXAMPLE
  pwsh tools/fetch-models.ps1
#>

[CmdletBinding()]
param(
    # 语音识别模型。base 在准确率和速度之间比较平衡；
    # 机器慢可以换 tiny（75MB，准确率明显下降），要更准可以换 small（466MB，CPU 上会吃力）。
    [ValidateSet('tiny', 'base', 'small', 'medium')]
    [string]$WhisperModel = 'base',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root 'models'
New-Item -ItemType Directory -Force -Path $models | Out-Null

function Get-File {
    param([string]$Url, [string]$Destination, [string]$Label)

    if ((Test-Path $Destination) -and -not $Force) {
        $size = [math]::Round((Get-Item $Destination).Length / 1MB, 1)
        Write-Host "  已存在，跳过：$Label ($size MB)" -ForegroundColor DarkGray
        return
    }

    Write-Host "  下载 $Label …" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null

    $temp = "$Destination.part"
    try {
        # 先下到 .part 再改名：中断之后不会留下一个看起来完整、实际半截的文件，
        # 那种文件会让应用在加载模型时报出莫名其妙的错。
        Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
        Move-Item -Force $temp $Destination
        $size = [math]::Round((Get-Item $Destination).Length / 1MB, 1)
        Write-Host "    完成（$size MB）" -ForegroundColor Green
    }
    catch {
        if (Test-Path $temp) { Remove-Item -Force $temp }
        throw
    }
}

Write-Host "`n[1/2] 语音识别模型 whisper-$WhisperModel" -ForegroundColor White
Get-File `
    -Url "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-$WhisperModel.bin" `
    -Destination (Join-Path $models 'ggml-base.bin') `
    -Label "ggml-$WhisperModel.bin"

Write-Host "`n[2/2] 翻译模型 opus-mt-en-zh" -ForegroundColor White
$mt = Join-Path $models 'opus-mt-en-zh'
$base = 'https://huggingface.co/onnx-community/opus-mt-en-zh/resolve/main'

foreach ($f in @('vocab.json', 'source.spm', 'target.spm', 'config.json')) {
    Get-File -Url "$base/$f" -Destination (Join-Path $mt $f) -Label $f
}

foreach ($f in @('encoder_model_quantized.onnx', 'decoder_model_quantized.onnx')) {
    Get-File -Url "$base/onnx/$f" -Destination (Join-Path $mt "onnx/$f") -Label $f
}

Write-Host "`n全部就绪，模型在 $models" -ForegroundColor Green
Write-Host "发布时把这个 models 目录放到 exe 旁边。`n" -ForegroundColor DarkGray
