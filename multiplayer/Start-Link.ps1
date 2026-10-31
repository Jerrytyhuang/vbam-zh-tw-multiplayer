<#
.SYNOPSIS
  在同一台電腦多開 VisualBoyAdvance-M 並以本機連線（模擬 GBA 連接線）互連。

.EXAMPLE
  .\Start-Link.ps1 -Rom "D:\ROMs\game.gba"                       # 2 人，同一個 ROM
  .\Start-Link.ps1 -Rom "D:\ROMs\ruby.gba","D:\ROMs\sapphire.gba" # 每位玩家不同 ROM
  .\Start-Link.ps1 -Rom "D:\ROMs\game.gba" -Players 4 -LinkType Wireless
#>
param(
    [Parameter(Mandatory = $true)][string[]]$Rom,
    [ValidateRange(2, 4)][int]$Players = 0,
    [ValidateSet('Cable', 'Wireless', 'GameBoy')][string]$LinkType = 'Cable',
    [int]$Scale = 0
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path (Split-Path -Parent $here) 'visualboyadvance-m.exe'
$baseIni = Join-Path $env:LOCALAPPDATA 'visualboyadvance-m\vbam.ini'

if ($Players -eq 0) { $Players = [Math]::Max(2, $Rom.Count) }
foreach ($r in $Rom) { if (-not (Test-Path -LiteralPath $r)) { throw "找不到 ROM：$r" } }
if (-not (Test-Path $baseIni)) { throw "找不到主設定檔：$baseIni（請先正常開啟一次 VBA-M）" }

# 2 人左右並排，3~4 人 2x2；依螢幕可用區域（已含 DPI 縮放）算出放得下的最大整數倍率
Add-Type -AssemblyName System.Windows.Forms
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$rows = if ($Players -le 2) { 1 } else { 2 }
$cellW = [Math]::Floor($area.Width / 2); $cellH = [Math]::Floor($area.Height / $rows)
if ($Scale -eq 0) { $Scale = [Math]::Max(1, [Math]::Floor([Math]::Min(($cellW - 20) / 240, ($cellH - 100) / 160))) }
$linkTypeValue = @{ Cable = 1; Wireless = 2; GameBoy = 4 }[$LinkType]

Add-Type -Namespace VbamLink -Name Win -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr h, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
public struct RECT { public int L, T, R, B; }
"@

# Section-aware INI setter: updates key in [section], or appends it to that section
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

function Get-IniValue($lines, [string]$section, [string]$key) {
    $inSec = $false
    foreach ($l in $lines) {
        if ($l -match '^\[(.+)\]$') { $inSec = ($Matches[1] -eq $section); continue }
        if ($inSec -and $l.StartsWith("$key=")) { return $l.Substring($key.Length + 1) }
    }
    return ''
}

$base = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($baseIni))
$buttons = 'Up', 'Down', 'Left', 'Right', 'A', 'B', 'L', 'R', 'Select', 'Start', 'Speed'

$procs = @()
for ($n = 1; $n -le $Players; $n++) {
    $dir = Join-Path $here "P$n"
    New-Item -ItemType Directory -Force (Join-Path $dir 'saves'), (Join-Path $dir 'states') | Out-Null
    $ini = [System.Collections.Generic.List[string]]::new($base)

    # 本機連線
    Set-IniValue $ini 'GBA' 'LinkType' $linkTypeValue
    Set-IniValue $ini 'GBA' 'LinkProto' '1'
    Set-IniValue $ini 'GBA' 'LinkAuto' '1'
    Set-IniValue $ini 'preferences' 'LinkNumPlayers' $Players
    # 背景視窗不能暫停，否則連線會卡住
    Set-IniValue $ini 'preferences' 'pauseWhenInactive' '0'
    Set-IniValue $ini 'ui' 'allowJoystickBackgroundInput' '1'
    Set-IniValue $ini 'ui' 'allowKeyboardBackgroundInput' '0'

    # 每位玩家獨立的存檔與即時存檔
    Set-IniValue $ini 'General' 'BatteryDir' (Join-Path $dir 'saves')
    Set-IniValue $ini 'General' 'StateDir' (Join-Path $dir 'states')
    Set-IniValue $ini 'General' 'AutoLoadLastState' '0'

    # 玩家 N 使用 Joypad/N：第 N 支手把 + 與玩家 1 相同的鍵盤按鍵（只對目前焦點視窗有效）
    Set-IniValue $ini 'Joypad' 'Default' $n
    if ($n -gt 1) {
        foreach ($b in $buttons) {
            $keys = (Get-IniValue $base 'Joypad/1' $b) -split ',' | Where-Object { $_ -and $_ -notmatch '^Joy\d' }
            $pad = (Get-IniValue $base "Joypad/$n" $b) -split ',' | Where-Object { $_ }
            Set-IniValue $ini "Joypad/$n" $b ((@($keys) + @($pad)) -join ',')
        }
    }

    Set-IniValue $ini 'Display' 'Scale' $Scale
    Set-IniValue $ini 'geometry' 'fullScreen' '0'
    Set-IniValue $ini 'geometry' 'isMaximized' '0'

    $cfg = Join-Path $dir 'vbam.ini'
    [IO.File]::WriteAllLines($cfg, $ini, (New-Object System.Text.UTF8Encoding $false))

    $romPath = (Resolve-Path -LiteralPath $Rom[[Math]::Min($n - 1, $Rom.Count - 1)]).Path
    Write-Host "啟動玩家 $n：$([IO.Path]::GetFileName($romPath))"
    $procs += Start-Process -FilePath $exe -ArgumentList @('-c', "`"$cfg`"", "`"$romPath`"") -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    # 讓玩家 1 先建立共享記憶體（主機），其餘再加入
    Start-Sleep -Seconds $(if ($n -eq 1) { 3 } else { 1 })
}

# 視窗排列：依螢幕可用區域等分，畫面會自動延展填滿（Stretch=1）
for ($i = 0; $i -lt $procs.Count; $i++) {
    $p = $procs[$i]
    # 等 ROM 載入完成（標題出現「 - 」）再排列，否則模擬器載入後會再自行調整視窗大小
    for ($w = 0; $w -lt 75 -and ($p.MainWindowHandle -eq [IntPtr]::Zero -or $p.MainWindowTitle -notlike '* - *'); $w++) { Start-Sleep -Milliseconds 200; $p.Refresh() }
    Start-Sleep -Milliseconds 500
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { continue }
    # 視窗比例保持接近 GBA 3:2（加上選單列與狀態列高度）
    $w = $cellW; $h = [Math]::Min($cellH, [Math]::Floor($w * 2 / 3) + 90)
    $x = $area.X + ($i % 2) * $cellW; $y = $area.Y + [Math]::Floor($i / 2) * $cellH
    [void][VbamLink.Win]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, $x, $y, $w, $h, 0x0014)  # NOZORDER|NOACTIVATE
}

Write-Host "`n已啟動 $Players 個視窗（連線類型：$LinkType）。存檔位於 $here\P<n>\saves"
