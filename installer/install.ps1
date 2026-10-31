<#
.SYNOPSIS
  安裝 VisualBoyAdvance-M（官方版）+ 繁體中文介面 + 本機多開聯機工具。

.EXAMPLE
  .\install.ps1
  .\install.ps1 -InstallDir "D:\Emulators\VBA-M" -NoShortcut
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\VisualBoyAdvance-M'),
    [string]$VbamVersion = '2.2.3',
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$pkg = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $InstallDir 'visualboyadvance-m.exe'
# wxLANGUAGE_CHINESE_TAIWAN in wxWidgets 3.3.x (VBA-M 2.2.x)
$localeTaiwan = 138

function Set-IniValue([System.Collections.Generic.List[string]]$lines, [string]$section, [string]$key, [string]$value) {
    $inSec = $false; $secEnd = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -match '^\[(.+)\]$') {
            if ($inSec) { $secEnd = $i; break }
            $inSec = ($Matches[1] -eq $section)
            continue
        }
        if ($inSec -and $l.StartsWith("$key=")) { $lines[$i] = "$key=$value"; return }
    }
    if ($inSec -and $secEnd -lt 0) { $lines.Add("$key=$value"); return }
    if ($secEnd -ge 0) { $lines.Insert($secEnd, "$key=$value"); return }
    $lines.Add("[$section]"); $lines.Add("$key=$value")
}

if (Get-Process visualboyadvance-m -ErrorAction SilentlyContinue) {
    throw '請先關閉所有 VisualBoyAdvance-M 視窗再安裝。'
}

Write-Host "安裝位置：$InstallDir"
New-Item -ItemType Directory -Force $InstallDir | Out-Null

# 1. 下載官方 VBA-M 並驗證簽章
if (Test-Path $exe) {
    Write-Host "[1/4] 已有 VBA-M $((Get-Item $exe).VersionInfo.ProductVersion)，略過下載"
} else {
    Write-Host "[1/4] 下載官方 VBA-M v$VbamVersion ..."
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("vbam-" + [guid]::NewGuid())
    New-Item -ItemType Directory $tmp | Out-Null
    try {
        $zip = Join-Path $tmp 'vbam.zip'
        Invoke-WebRequest -UseBasicParsing -OutFile $zip `
            -Uri "https://github.com/visualboyadvance-m/visualboyadvance-m/releases/download/v$VbamVersion/visualboyadvance-m-Win-x86_64.zip"
        Expand-Archive $zip -DestinationPath (Join-Path $tmp 'x')
        $newExe = Join-Path $tmp 'x\visualboyadvance-m.exe'
        $sig = Get-AuthenticodeSignature $newExe
        if ($sig.Status -ne 'Valid') { throw "VBA-M 簽章驗證失敗：$($sig.Status)" }
        Write-Host "      簽章有效：$($sig.SignerCertificate.GetNameInfo('SimpleName', $false))"
        Copy-Item $newExe $exe
    } finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

# 2. 繁體中文翻譯與多開聯機工具
Write-Host '[2/4] 安裝繁體中文翻譯與多開聯機工具'
$moDir = Join-Path $InstallDir 'zh_TW\LC_MESSAGES'
New-Item -ItemType Directory -Force $moDir | Out-Null
Copy-Item (Join-Path $pkg 'zh_TW\LC_MESSAGES\wxvbam.mo') $moDir -Force
$mpDir = Join-Path $InstallDir 'multiplayer'
New-Item -ItemType Directory -Force $mpDir | Out-Null
Copy-Item (Join-Path $pkg 'multiplayer\*') $mpDir -Force

# 3. 設定介面語言（保留既有設定，只改語言）
Write-Host '[3/4] 設定介面語言為繁體中文'
$cfgDir = Join-Path $env:LOCALAPPDATA 'visualboyadvance-m'
$cfg = Join-Path $cfgDir 'vbam.ini'
New-Item -ItemType Directory -Force $cfgDir | Out-Null
$lines = New-Object 'System.Collections.Generic.List[string]'
if (Test-Path $cfg) {
    Copy-Item $cfg "$cfg.before-zh-tw" -Force
    $lines.AddRange([IO.File]::ReadAllLines($cfg))
}
Set-IniValue $lines 'Language' 'Locale' $localeTaiwan
Set-IniValue $lines 'Language' 'ExternalTranslations' '1'
[IO.File]::WriteAllLines($cfg, $lines, (New-Object System.Text.UTF8Encoding $false))

# 4. 桌面捷徑
if ($NoShortcut) {
    Write-Host '[4/4] 略過建立捷徑'
} else {
    Write-Host '[4/4] 建立桌面捷徑'
    $desktop = [Environment]::GetFolderPath('Desktop')
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut((Join-Path $desktop 'VisualBoyAdvance-M.lnk'))
    $lnk.TargetPath = $exe; $lnk.WorkingDirectory = $InstallDir; $lnk.Save()
    $lnk = $ws.CreateShortcut((Join-Path $desktop 'VBA-M 多開聯機.lnk'))
    $lnk.TargetPath = Join-Path $mpDir '多開聯機.bat'; $lnk.WorkingDirectory = $mpDir
    $lnk.IconLocation = "$exe,0"; $lnk.Save()
}

Write-Host "`n安裝完成！"
Write-Host "  開啟模擬器：$exe"
Write-Host "  多開聯機：把 ROM 拖曳到桌面的「VBA-M 多開聯機」捷徑（或 $mpDir\多開聯機.bat）"
