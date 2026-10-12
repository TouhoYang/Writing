# Build the HarmonyOS HAP (release build) with a custom version and channel.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\build-hap.ps1 0.1
#   powershell -ExecutionPolicy Bypass -File .\build-hap.ps1 0.1 trial
#   powershell -ExecutionPolicy Bypass -File .\build-hap.ps1 -Version 1.2.3 -Channel rel
#   powershell -ExecutionPolicy Bypass -File .\build-hap.ps1 0.1 -SkipInstall
#   powershell -ExecutionPolicy Bypass -File .\build-hap.ps1 0.1 -AllowDowngrade
#
# Channel:
#   rel    versionName 0.1.0          ->  writing-harmony-0.1.0.hap
#   trial  versionName 0.1.0-trial    ->  writing-harmony-0.1.0-trial.hap
#
# Version:
#   Accepts "0.1" or "1.2.3"; missing segments become 0.
#   versionCode = major*10000 + minor*100 + patch  (each segment 0..99)
#     "0.1"   -> versionName 0.1.0, versionCode 100
#     "1.2.3" -> versionName 1.2.3, versionCode 10203
#   HarmonyOS refuses to install a LOWER versionCode than what is on the device
#   (error 9568263). Use -AllowDowngrade to uninstall the old build first --
#   note that deletes the app private directory (drafts + cloned repos).
#
# What it does:
#   1) static check of index.html   (catches deleted functions / missing handlers)
#   2) sync index.html into the harmony rawfile
#   3) stamp versionName / versionCode into AppScope\app.json5
#   4) hvigorw assembleHap -p buildMode=release
#   5) copy the signed HAP to mobile\harmony\dist\
#   6) install to the connected device (unless -SkipInstall)
#   7) restore app.json5 from backup
#
# NOTE: this file is intentionally ASCII-only. Windows PowerShell 5.1 reads .ps1
# files as ANSI, so non-ASCII characters here would be garbled. All CJK handling
# (the output file name) is delegated to version_tool.py, which reads/writes UTF-8.

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version,

    [Parameter(Position = 1)]
    [ValidateSet('rel', 'release', 'trial', 'test', 'beta')]
    [string]$Channel = 'rel',

    # Do not install to the device after building.
    [switch]$SkipInstall,

    # If the device already has a HIGHER versionCode, HarmonyOS refuses the
    # install (9568263 install version downgrade). Passing this switch makes the
    # script uninstall the old build first -- WARNING: that deletes the app's
    # private directory, i.e. all drafts and cloned repositories.
    [switch]$AllowDowngrade,

    # Skip the index.html static check (not recommended).
    [switch]$SkipCheck
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

# Console encoding: the artifact file name contains CJK characters, and the
# default OEM code page (936 on a Chinese Windows) mangles it. Switch both the
# console and Python's stdio to UTF-8 so the log is readable and file names match.
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::InputEncoding  = [System.Text.Encoding]::UTF8
} catch {
    # Some hosts refuse to change this; not fatal.
}
$OutputEncoding = [System.Text.Encoding]::UTF8
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'
try { & chcp.com 65001 | Out-Null } catch { }

# ---------------------------------------------------------------- defaults
if (-not $Version) { $Version = '0.1' }

$harmony = Join-Path $root 'harmony'
$entry   = Join-Path $harmony 'entry'
$appJson = Join-Path $harmony 'AppScope\app.json5'

Write-Host ""
Write-Host "============================================================"
Write-Host " Build HarmonyOS HAP"
Write-Host "============================================================"

foreach ($p in @($harmony, $entry, $appJson)) {
    if (-not (Test-Path $p)) { throw "Missing project component: $p" }
}

# ---------------------------------------------------------------- python
# version_tool.py handles all UTF-8 / CJK work.
$python = $null
foreach ($cand in @('python', 'python3', 'py')) {
    $cmd = Get-Command $cand -ErrorAction SilentlyContinue
    if ($cmd) { $python = $cmd.Source; break }
}
if (-not $python) {
    throw "Python not found in PATH. Install Python 3, or run version_tool.py manually."
}

$versionTool = Join-Path $root 'version_tool.py'
if (-not (Test-Path $versionTool)) { throw "Missing: $versionTool" }

# ---------------------------------------------------------------- parse version
Write-Host "[1/7] resolving version ..."
$parseOut = & $python $versionTool parse $Version $Channel 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host ($parseOut | Out-String)
    throw "Invalid version / channel"
}
$meta = @{}
foreach ($line in $parseOut) {
    $s = [string]$line
    $i = $s.IndexOf('=')
    if ($i -gt 0) { $meta[$s.Substring(0, $i)] = $s.Substring($i + 1) }
}
$vName   = $meta['VERSION_NAME']
$vCode   = $meta['VERSION_CODE']
$hapName = $meta['HAP_NAME']
if (-not $vName -or -not $hapName) { throw "version_tool.py returned unexpected output" }
Write-Host "      versionName = $vName"
Write-Host "      versionCode = $vCode"
Write-Host "      channel     = $($meta['CHANNEL'])"
Write-Host "      output      = $hapName"

# ---------------------------------------------------------------- static check
Write-Host "[2/7] static check of index.html ..."
if ($SkipCheck) {
    Write-Host "      SKIPPED (-SkipCheck)"
} else {
    $checker = Join-Path $root 'check_mobile.py'
    if (Test-Path $checker) {
        & $python $checker (Join-Path $root 'index.html')
        if ($LASTEXITCODE -ne 0) {
            throw "Static check failed. Fix the problems above before building."
        }
    } else {
        Write-Host "      check_mobile.py not found, skipping"
    }
}

# ---------------------------------------------------------------- sync web body
Write-Host "[3/7] syncing index.html into harmony rawfile ..."
$src = Join-Path $root 'index.html'
$dst = Join-Path $entry 'src\main\resources\rawfile\index.html'
if (-not (Test-Path $src)) { throw "Missing: $src" }
Copy-Item $src $dst -Force
$kb = [math]::Round((Get-Item $dst).Length / 1KB, 1)
Write-Host "      copied ($kb KB)"

# ---------------------------------------------------------------- stamp version
Write-Host "[4/7] stamping version into app.json5 ..."
& $python $versionTool stamp $Version $Channel | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Failed to stamp version" }

# Everything after this point must restore app.json5, even on failure.
$built = $false
try {
    # ------------------------------------------------------------ build
    Write-Host "[5/7] building (this takes a while) ..."
    $env:NODE_HOME       = 'D:\DevEco Studio\tools\node'
    $env:JAVA_HOME       = 'D:\DevEco Studio\jbr'
    $env:DEVECO_SDK_HOME = 'D:\DevEco Studio\sdk'
    $env:PATH = "$env:NODE_HOME;$env:JAVA_HOME\bin;" +
                "D:\DevEco Studio\tools\ohpm\bin;" +
                "D:\DevEco Studio\tools\hvigor\bin;$env:PATH"

    foreach ($tool in @($env:NODE_HOME, $env:JAVA_HOME, $env:DEVECO_SDK_HOME)) {
        if (-not (Test-Path $tool)) { throw "DevEco component not found: $tool" }
    }

    Push-Location $harmony
    try {
        # hvigorw writes WARN lines to stderr, and under Windows PowerShell 5.1
        # any native stderr output becomes a NativeCommandError -- with
        # ErrorActionPreference = 'Stop' that aborts the script mid-build.
        # It also exits 1 even on success, so neither stderr nor the exit code
        # can be used directly: run it via Start-Process, redirect BOTH streams
        # to files, and decide from the log text.
        $logOut = Join-Path $env:TEMP 'mazi-hvigor.out.log'
        $logErr = Join-Path $env:TEMP 'mazi-hvigor.err.log'
        Remove-Item $logOut, $logErr -Force -ErrorAction SilentlyContinue

        $hvigorw = Get-Command hvigorw -ErrorAction SilentlyContinue
        if (-not $hvigorw) { throw "hvigorw not found in PATH" }

        $hvArgs = @('assembleHap', '--mode', 'module',
                    '-p', 'product=default', '-p', 'buildMode=release', '--no-daemon')
        $proc = Start-Process -FilePath $hvigorw.Source -ArgumentList $hvArgs `
                    -NoNewWindow -Wait -PassThru `
                    -RedirectStandardOutput $logOut -RedirectStandardError $logErr

        $log = @()
        if (Test-Path $logOut) { $log += Get-Content $logOut }
        if (Test-Path $logErr) { $log += Get-Content $logErr }
        $log | ForEach-Object { Write-Host "      $_" }

        $ok = $false
        foreach ($l in $log) {
            if ([string]$l -match 'BUILD SUCCESSFUL') { $ok = $true }
        }
        if (-not $ok) {
            throw "hvigor build did not report BUILD SUCCESSFUL (exit code $($proc.ExitCode))"
        }
    } finally {
        Pop-Location
    }

    # ------------------------------------------------------------ collect
    Write-Host "[6/7] collecting artifact ..."
    $hap = Join-Path $entry 'build\default\outputs\default\entry-default-signed.hap'
    if (-not (Test-Path $hap)) { throw "Signed HAP not produced: $hap" }

    $dist = Join-Path $harmony 'dist'
    New-Item -ItemType Directory -Force -Path $dist | Out-Null
    $out = Join-Path $dist $hapName
    Copy-Item $hap $out -Force

    $sizeKB = [math]::Round((Get-Item $out).Length / 1KB, 1)
    $sha    = (Get-FileHash $out -Algorithm SHA256).Hash
    $built  = $true
    Write-Host "      $out"
    Write-Host "      $sizeKB KB"
    Write-Host "      SHA256 $sha"

    # ------------------------------------------------------------ install
    Write-Host "[7/7] installing to device ..."
    if ($SkipInstall) {
        Write-Host "      SKIPPED (-SkipInstall)"
    } else {
        $hdc = 'D:\DevEco Studio\sdk\default\openharmony\toolchains\hdc.exe'
        if (-not (Test-Path $hdc)) {
            Write-Host "      hdc not found, skipping install"
            Write-Host "      install manually with: hdc install -r `"$out`""
        } else {
            # Capture via a temp file, not 2>&1: hdc writes some lines to stderr,
            # and under PowerShell 5.1 that produces NativeCommandError records
            # which corrupt the captured list.
            $listFile = Join-Path $env:TEMP 'mazi-hdc-targets.txt'
            Remove-Item $listFile -Force -ErrorAction SilentlyContinue
            $hdcArgs = @('list', 'targets')
            Start-Process -FilePath $hdc -ArgumentList $hdcArgs -NoNewWindow -Wait `
                -RedirectStandardOutput $listFile `
                -RedirectStandardError ($listFile + '.err') | Out-Null

            $targets = @()
            if (Test-Path $listFile) {
                $targets = @(Get-Content $listFile |
                    ForEach-Object { ([string]$_).Trim() } |
                    Where-Object { $_ -and $_ -notmatch '^\[Empty\]$' })
            }

            if ($targets.Count -eq 0) {
                Write-Host "      no device connected, skipping install"
                Write-Host "      install later with: hdc install -r `"$out`""
            } else {
                $t = $targets[0]
                Write-Host "      device: $t"

                function Invoke-HdcInstall() {
                    $instFile = Join-Path $env:TEMP 'mazi-hdc-install.txt'
                    Remove-Item $instFile, ($instFile + '.err') -Force -ErrorAction SilentlyContinue
                    Start-Process -FilePath $hdc `
                        -ArgumentList @('-t', $t, 'install', '-r', $out) `
                        -NoNewWindow -Wait -RedirectStandardOutput $instFile `
                        -RedirectStandardError ($instFile + '.err') | Out-Null
                    $lines = @()
                    foreach ($f in @($instFile, $instFile + '.err')) {
                        if (Test-Path $f) { $lines += Get-Content $f }
                    }
                    return $lines
                }

                $instLog = Invoke-HdcInstall
                $instLog | Select-Object -Unique | ForEach-Object { Write-Host "      $_" }

                $joined = ($instLog -join "`n")
                $installed = $joined -match 'successfully'
                $downgrade = $joined -match '9568263|version downgrade'

                if (-not $installed -and $downgrade) {
                    Write-Host ""
                    Write-Host "      The device already has a HIGHER versionCode."
                    Write-Host "      (you are installing a lower version than the current build)"
                    if ($AllowDowngrade) {
                        Write-Host "      -AllowDowngrade given: uninstalling the old build first ..."
                        Write-Host "      WARNING: this deletes the app private directory."
                        # Give the user a chance to export their data in a real run.
                        Start-Process -FilePath $hdc `
                            -ArgumentList @('-t', $t, 'uninstall', 'com.touho.writing') `
                            -NoNewWindow -Wait | Out-Null
                        Start-Sleep -Seconds 2
                        $instLog = Invoke-HdcInstall
                        $instLog | Select-Object -Unique | ForEach-Object { Write-Host "      $_" }
                        $installed = (($instLog -join "`n") -match 'successfully')
                    } else {
                        Write-Host "      To install anyway, re-run with -AllowDowngrade"
                        Write-Host "      (it will UNINSTALL the old build, deleting drafts and repos)."
                        Write-Host "      To keep your data, either:"
                        Write-Host "        - export first (folder panel -> export all / Git -> export repo), or"
                        Write-Host "        - pick a version number higher than the installed one."
                    }
                }

                if ($installed) {
                    Start-Process -FilePath $hdc `
                        -ArgumentList @('-t', $t, 'shell', 'aa force-stop com.touho.writing') `
                        -NoNewWindow -Wait | Out-Null
                    Start-Process -FilePath $hdc `
                        -ArgumentList @('-t', $t, 'shell', 'aa start -a EntryAbility -b com.touho.writing') `
                        -NoNewWindow -Wait | Out-Null
                    Write-Host "      installed and launched"
                } elseif (-not $downgrade) {
                    Write-Host "      install failed (device may need developer mode enabled)"
                }
            }
        }
    }
} finally {
    # Always restore the source tree, even if the build threw.
    & $python $versionTool restore | Out-Null
    Write-Host "      app.json5 restored"
}

Write-Host ""
Write-Host "============================================================"
if ($built) {
    Write-Host " DONE   $hapName"
    Write-Host "        version $vName (code $vCode),  channel $($meta['CHANNEL'])"
    Write-Host ""
    Write-Host " Install on a phone:"
    Write-Host "   hdc install -r `"$out`""
} else {
    Write-Host " FAILED"
}
Write-Host "============================================================"
Write-Host ""
