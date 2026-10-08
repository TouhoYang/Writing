# 构建脚本
# 依赖:Windows 系统自带 .NET Framework 4.8 的 csc.exe(无需安装 SDK、无需联网下载)
# 输出:Release\写作.exe(绿色免安装单文件)
$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$src = Join-Path $PSScriptRoot 'Writing'
$out = Join-Path $PSScriptRoot 'Release'
if (-not (Test-Path $csc)) { throw '未找到 csc.exe' }
New-Item -ItemType Directory -Force -Path $out | Out-Null
& $csc /nologo /target:winexe /optimize+ /langversion:5 /codepage:65001 `
  /win32manifest:"$src\app.manifest" /win32icon:"$src\app.ico" /resource:"$src\app.ico",app.ico `
  /out:"$out\写作.exe" `
  /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:System.Xml.dll /r:System.Windows.Forms.dll /r:System.Security.dll `
  /r:"$fw\WPF\WindowsBase.dll" /r:"$fw\WPF\PresentationCore.dll" /r:"$fw\WPF\PresentationFramework.dll" `
  "$src\Program.cs" "$src\MainWindow.cs" "$src\Theme.cs" "$src\GitPanel.cs" "$src\TextStats.cs" "$src\TextEncodingHelper.cs" "$src\Settings.cs"
if ($LASTEXITCODE -ne 0) { throw "编译失败,exit=$LASTEXITCODE" }
Write-Host "构建成功: $out\写作.exe"
