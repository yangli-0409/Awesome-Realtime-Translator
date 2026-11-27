<#
.SYNOPSIS
  打出可以直接分发的 Windows 包。

.DESCRIPTION
  产出 publish/AwesomeRealtimeTranslator-<版本>-win-<架构>/，里面是：

    AwesomeRealtimeTranslator.exe   托管代码 + .NET 运行时
    onnxruntime.dll 等原生库         翻译和 WPF 要用的，必须在 exe 旁边
    runtimes/win-*/                 whisper 的原生库，Whisper.net 按这个路径找
    models/ggml-base.bin            语音识别模型
    models/opus-mt-en-zh/           翻译模型
    使用说明.txt

  模型随包一起发，不在运行时下载——理由见 fetch-models.ps1 的说明。

  原生库不打进 exe（见 App.csproj 里 IncludeNativeLibrariesForSelfExtract 的注释）。
  所以整个目录要保持原样分发，用户不能只把 exe 拖出来。

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
字幕宝 $version （Windows）

怎么用
  1. 先把整个文件夹解压出来，放到一个固定位置
     （建议放在「文档」或「下载」里新建一个文件夹）
  2. 双击 AwesomeRealtimeTranslator.exe
  3. 字幕窗出现在屏幕顶部，点右上角 ▶ 开始听
  4. 不需要任何权限，也不需要重启

  Ctrl+Alt+T  开始 / 暂停
  Ctrl+Alt+H  显示 / 隐藏字幕窗

注意
  * 整个文件夹要保持原样，不要只把 exe 单独拖出来——models、runtimes
    和那几个 dll 都得跟它在一起，少一个就跑不起来。
    想要桌面快捷方式的话，右键 exe → 发送到 → 桌面快捷方式。
  * 放在系统盘上，别放在其他分区、外接硬盘、U 盘或网络盘上。
    有人放在 E 盘时遇到过语音识别模型加载直接失败，整个文件夹复制到
    C 盘下的「下载」里就正常了。原因还没查清。
  * 第一次启动要加载模型，大约三到五秒。
  * 译文现在是逐字流出来的，不再是整句一次性蹦出来。出字的快慢在
    设置里「显现节奏」那一项，原文和译文都归它管（0.3.0 之前那个
    滑块只管原文，调了对译文没有任何作用）。
  * 它只听电脑播放的声音，不听麦克风——听得到对方，听不到你自己。
  * 全程不联网。这个程序没有任何联网代码。
  * 这个程序没有买代码签名证书，Windows 可能弹「未知发布者」，极少数杀毒
    软件也可能误报。点「更多信息」→「仍要运行」。不放心就自己编译源码。

关于「字幕窗不出现在屏幕共享里」
  0.2.0 起，这条承诺有自动化测试盯着了：每次改代码，CI 都会在一台真
  Windows 上把窗口显示出来、真的截一次屏，确认它不在截图里。

  但那验的是 Windows 自带的截屏方式。会议软件的桌面客户端采集画面的
  方式不止一种，所以第一次正式用之前，还是建议你自己用会议软件的
  共享预览确认一次。也可以打开这个页面一分钟测完：
  https://yangli-0409.github.io/Awesome-Realtime-Translator/test/

出问题了
  托盘图标右键 →「打开日志文件夹」，里面的 log.txt 会写清失败原因。
  日志只记程序自己的状态，不记录任何识别出来的文字和译文，可以直接发给我。
  版本号在设置窗底部，也能把鼠标停在托盘图标上看到。

免费 · MIT 开源
https://github.com/yangli-0409/Awesome-Realtime-Translator
"@ | Set-Content -Path (Join-Path $stage '使用说明.txt') -Encoding UTF8

$zip = "$stage.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "`n打好了：$zip （$size MB）`n" -ForegroundColor Green
