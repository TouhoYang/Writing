# Fetch the Android SDK pieces needed to build the APK.
# The SDK is NOT shipped with this project (it is ~347 MB after extraction),
# so run this once before build-apk.ps1:
#     powershell -ExecutionPolicy Bypass -File .\获取安卓SDK.ps1
# It downloads into .\android-sdk\ and can be deleted again at any time.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sdk  = Join-Path $root 'android-sdk'
New-Item -ItemType Directory -Force -Path $sdk | Out-Null

$items = @(
  @{ name = 'build-tools r34'; url = 'https://dl.google.com/android/repository/build-tools_r34-windows.zip'; zip = (Join-Path $sdk 'build-tools.zip'); out = (Join-Path $sdk 'build-tools') },
  @{ name = 'platform 34';     url = 'https://dl.google.com/android/repository/platform-34-ext7_r03.zip';     zip = (Join-Path $sdk 'platform.zip');    out = (Join-Path $sdk 'platform') }
)

foreach ($it in $items) {
  if (-not (Test-Path $it.zip)) {
    Write-Host ('downloading ' + $it.name + ' ...')
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-WebRequest -Uri $it.url -OutFile $it.zip -UseBasicParsing -TimeoutSec 1800
    $sw.Stop()
    Write-Host ('  ' + [math]::Round((Get-Item $it.zip).Length / 1MB, 1) + ' MB in ' + [math]::Round($sw.Elapsed.TotalSeconds) + 's')
  }
  if (-not (Test-Path $it.out)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($it.zip, $it.out)
  }
  Write-Host ('[ok] ' + $it.name)
}

$bt = Join-Path $sdk 'build-tools\android-14\aapt2.exe'
$aj = Join-Path $sdk 'platform\android-34\android.jar'
if ((Test-Path $bt) -and (Test-Path $aj)) {
  Write-Host 'Android SDK ready. Now run: powershell -ExecutionPolicy Bypass -File .\build-apk.ps1'
} else {
  Write-Host 'WARNING: expected files were not found - the upstream zip layout may have changed.'
  Write-Host ('  expected: ' + $bt)
  Write-Host ('  expected: ' + $aj)
}
