<#
.SYNOPSIS
  打出可以直接分发的 Windows 包。

.DESCRIPTION
  产出 publish/AwesomeRealtimeTranslator-<版本>-win-<架构>/，里面是：

    AwesomeRealtimeTranslator.exe   单文件，自带 .NET 运行时
    models/ggml-base.bin            语音识别模型
    models/opus-mt-en-zh/           翻译模型
    使用说明.txt

  模型随包一起发，不在运行时下载——理由见 fetch-models.ps1 的说明。

.EXAMPLE
  pwsh tools/package.ps1
  pwsh tools/package.ps1 -Runtime win-arm64
#>

[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [switch]$SkipModels
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'src/App/App.csproj'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '0.1.0' }

$stage = Join-Path $root "publish/AwesomeRealtimeTranslator-$version-$Runtime"

Write-Host "`n[1/3] 构建 $Runtime …" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
dotnet publish (Join-Path $root 'src/App/App.csproj') `
    -c Release -r $Runtime -o $stage --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

# pdb 对用户没用，占地方
Get-ChildItem $stage -Filter *.pdb | Remove-Item -Force
Get-ChildItem $stage -Filter *.lib | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "`n[2/3] 放入模型 …" -ForegroundColor Cyan
if ($SkipModels) {
    Write-Host "  跳过（-SkipModels）。这样打出来的包开箱不能用。" -ForegroundColor Yellow
}
else {
    $models = Join-Path $root 'models'
    if (-not (Test-Path (Join-Path $models 'ggml-base.bin'))) {
        throw "找不到模型，先跑 tools/fetch-models.ps1"
    }
    Copy-Item -Recurse -Force $models (Join-Path $stage 'models')
}

Write-Host "`n[3/3] 写说明并压缩 …" -ForegroundColor Cyan
@"
面试字幕 $version （Windows）

怎么用
  1. 双击 AwesomeRealtimeTranslator.exe
  2. 字幕窗出现在屏幕顶部，点右上角 ▶ 开始听
  3. 不需要任何权限，也不需要重启

  Ctrl+Alt+T  开始 / 暂停
  Ctrl+Alt+H  显示 / 隐藏字幕窗

注意
  * models 文件夹必须和 exe 放在一起，删了就跑不起来。
  * 第一次启动要加载模型，大约三到五秒。
  * 它只听电脑播放的声音，不听麦克风——听得到对方，听不到你自己。
  * 字幕窗不会出现在录屏和屏幕共享里。第一次用之前，建议自己用会议软件的
    共享预览确认一次。
  * 全程不联网。这个程序没有任何联网代码。

免费 · MIT 开源
https://github.com/yangli-0409/Awesome-Realtime-Translator
"@ | Set-Content -Path (Join-Path $stage '使用说明.txt') -Encoding UTF8

$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "`n打好了：$zip （$size MB）`n" -ForegroundColor Green
