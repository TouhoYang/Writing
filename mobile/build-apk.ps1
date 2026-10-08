# 写作 Writing · 安卓版一键打包脚本
# 不依赖 Gradle：aapt2 链接资源 -> javac 编译 -> d8 转 dex -> zipalign 对齐 -> apksigner 签名
# 三个坑位说明：
#   1) aapt2 / d8 都不支持中文路径 -> 先在纯 ASCII 的临时目录里构建，再拷回发布目录
#   2) javac 21 生成的匿名内部类会让 build-tools 34 的 R8/d8 报内部错误 -> 固定用 JDK 18
#   3) 原生工具的 stderr 警告不能当致命错误 -> ErrorActionPreference = Continue + 显式判 LASTEXITCODE
$ErrorActionPreference = 'Continue'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$sdk     = Join-Path $root 'android-sdk'
$bt      = Join-Path $sdk 'build-tools\android-14'
$ajSrc   = Join-Path $sdk 'platform\android-34\android.jar'
$proj    = Join-Path $root 'android'
$release = Join-Path $root 'Release'
$work    = Join-Path $env:SystemDrive 'mazi_apk_build'      # 必须是 ASCII 路径

foreach ($p in @($bt, $ajSrc, $proj)) { if (-not (Test-Path $p)) { throw "缺少构建组件: $p  -- 请先运行: powershell -ExecutionPolicy Bypass -File .\获取安卓SDK.ps1" } }
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $work, "$work\gen", "$work\classes", "$work\dex", $release | Out-Null

# 0) 同步工程到 ASCII 工作目录(含最新网页与 android.jar)
Copy-Item "$proj\*" $work -Recurse -Force
New-Item -ItemType Directory -Force -Path "$work\assets" | Out-Null
Copy-Item (Join-Path $root 'index.html') "$work\assets\index.html" -Force
Copy-Item $ajSrc "$work\android.jar" -Force
$aj = "$work\android.jar"
Write-Host "[0/6] 工程已同步到 $work"

# 1) 编译资源
& "$bt\aapt2.exe" compile --dir "$work\res" -o "$work\res.zip" 2>$null
if ($LASTEXITCODE -ne 0) { throw "aapt2 compile 失败" }
Write-Host "[1/6] 资源编译完成"

# 2) 链接资源 + 生成 R.java + 打包 assets
& "$bt\aapt2.exe" link -o "$work\base.apk" -I $aj --manifest "$work\AndroidManifest.xml" `
  -R "$work\res.zip" --java "$work\gen" -A "$work\assets" `
  --min-sdk-version 24 --target-sdk-version 34 --version-code 1 --version-name 1.0.0 `
  --auto-add-overlay 2>$null
if ($LASTEXITCODE -ne 0) { throw "aapt2 link 失败" }
Write-Host "[2/6] 资源链接完成"

# 3) 编译 Java(必须 JDK 11~18)
$javaHome = $null
foreach ($cand in (Get-ChildItem 'C:\Program Files\Java' -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)) {
  if ($cand.Name -match 'jdk-(\d+)') {
    $major = [int]$Matches[1]
    if ($major -ge 11 -and $major -le 18) { $javaHome = $cand.FullName; break }
  }
}
if (-not $javaHome) {
  $javaHome = (Get-ChildItem 'C:\Program Files\Java' -Directory -ErrorAction SilentlyContinue | Where-Object Name -like 'jdk-*' | Sort-Object Name -Descending | Select-Object -First 1).FullName
  Write-Host "      警告：未找到 JDK 11~18，回退到 $javaHome（javac 21+ 可能导致 d8 失败）"
}
$env:JAVA_HOME = $javaHome
$javac = Join-Path $javaHome 'bin\javac.exe'
$srcs  = @(Get-ChildItem "$work\src" -Recurse -Filter *.java | ForEach-Object FullName)
$srcs += @(Get-ChildItem "$work\gen" -Recurse -Filter *.java | ForEach-Object FullName)
& $javac -nowarn -source 8 -target 8 -bootclasspath $aj -encoding UTF-8 -d "$work\classes" $srcs 2>$null
if ($LASTEXITCODE -ne 0) { throw "javac 失败" }
Write-Host "[3/6] Java 编译完成 (javac: $javaHome)"

# 4) d8 转 dex
$classes = @(Get-ChildItem "$work\classes" -Recurse -Filter *.class | ForEach-Object FullName)
& "$bt\d8.bat" --lib $aj --min-api 24 --output "$work\dex" $classes 2>$null
if ($LASTEXITCODE -ne 0) { throw "d8 失败" }
Write-Host "[4/6] dex 生成完成"

# 5) 把 classes.dex 打进 APK(resources.arsc 保持未压缩,满足 Android 11+)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$dex = "$work\dex\classes.dex"
if (-not (Test-Path $dex)) { throw "缺少 classes.dex" }
$zip = [System.IO.Compression.ZipFile]::Open("$work\base.apk", 'Update')
try {
  $arsc = $zip.GetEntry('resources.arsc')
  if ($null -ne $arsc -and $arsc.CompressedLength -ne $arsc.Length) {
    $tmp = "$work\arsc.bin"
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($arsc, $tmp, $true)
    $arsc.Delete()
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $tmp, 'resources.arsc', [System.IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    Remove-Item $tmp -Force
    Write-Host "      resources.arsc 已改为未压缩存储"
  }
  $e = $zip.GetEntry('classes.dex')
  if ($null -ne $e) { $e.Delete() }
  [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $dex, 'classes.dex', [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
} finally { $zip.Dispose() }
Write-Host "[5/6] classes.dex 已打入 APK"

# 6) 对齐 + 签名
& "$bt\zipalign.exe" -f -p 4 "$work\base.apk" "$work\aligned.apk" 2>$null
if ($LASTEXITCODE -ne 0) { throw "zipalign 失败" }
$ks = "$work\debug.keystore"
$keytool = Join-Path $javaHome 'bin\keytool.exe'
& $keytool -genkeypair -keystore $ks -storepass android -keypass android -alias androiddebugkey `
    -dname "CN=Android Debug,O=Android,C=CN" -keyalg RSA -keysize 2048 -validity 10000 2>$null | Out-Null
$outApk = Join-Path $release '写作.apk'
& "$bt\apksigner.bat" sign --ks $ks --ks-pass pass:android --key-pass pass:android `
    --v1-signing-enabled true --v2-signing-enabled true --v3-signing-enabled true --v4-signing-enabled false --out $outApk "$work\aligned.apk" 2>$null
if ($LASTEXITCODE -ne 0) { throw "apksigner 失败" }
& "$bt\apksigner.bat" verify --print-certs $outApk 2>$null | Select-Object -First 5
Write-Host "[6/6] 签名完成"
Write-Host ("APK: " + $outApk + "  (" + [math]::Round((Get-Item $outApk).Length / 1KB) + " KB)")
