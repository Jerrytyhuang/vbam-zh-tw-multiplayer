<#
.SYNOPSIS
  VBA-M 網際網路 P2P 連線：雙方輸入相同配對碼即可連線，不需要開連接埠或架伺服器。

.EXAMPLE
  .\Start-Online.ps1                                   # 互動式詢問
  .\Start-Online.ps1 -Mode Host -Rom game.gba          # 建立房間（自動產生配對碼）
  .\Start-Online.ps1 -Mode Join -Code ABCDE-12345 -Rom game.gba
#>
param(
    [string]$Rom,
    [ValidateSet('Host', 'Join')][string]$Mode,
    [string]$Code,
    [ValidateRange(2, 4)][int]$Players = 2,
    [int]$Port = 5738,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path (Split-Path -Parent $here) 'visualboyadvance-m.exe'
$baseIni = Join-Path $env:LOCALAPPDATA 'visualboyadvance-m\vbam.ini'

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

function New-PairCode {
    # 去掉容易混淆的 0/O/1/I/L
    $chars = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'.ToCharArray()
    $bytes = New-Object byte[] 10
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $s = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
    return $s.Substring(0, 5) + '-' + $s.Substring(5)
}

Write-Host '=== VBA-M 網際網路連線（P2P）===' -ForegroundColor Cyan
if (-not $Mode) {
    $ans = Read-Host '1) 建立房間（主機）  2) 加入房間    請選擇 [1/2]'
    $Mode = if ($ans -eq '2') { 'Join' } else { 'Host' }
}
if ($Mode -eq 'Host' -and -not $PSBoundParameters.ContainsKey('Players') -and -not $Code) {
    $ans = Read-Host '玩家人數（2~4）[2]'
    if ($ans -match '^[2-4]$') { $Players = [int]$ans }
}
if (-not $Code) {
    if ($Mode -eq 'Host') {
        $ans = Read-Host '配對碼（直接按 Enter 自動產生）'
        $Code = if ($ans.Trim()) { $ans.Trim() } else { New-PairCode }
    } else {
        $Code = (Read-Host '輸入主機提供的配對碼').Trim()
    }
}
$normalized = ($Code.ToUpperInvariant().ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) }) -join ''
if ($normalized.Length -lt 6) { throw '配對碼至少需要 6 個英數字（太短容易和陌生人撞碼）。' }

# 連線專用設定檔：以主設定為基礎，存檔位置不變（沿用平常的存檔）
if ((-not $NoLaunch) -and (Test-Path $baseIni)) {
    $dir = Join-Path $here 'online'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $ini = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($baseIni))
    Set-IniValue $ini 'GBA' 'LinkType' '1'
    Set-IniValue $ini 'GBA' 'LinkProto' '0'
    Set-IniValue $ini 'GBA' 'LinkAuto' '0'
    Set-IniValue $ini 'GBA' 'LinkHost' '127.0.0.1'
    # 主機的 VBA-M 只聽本機，外部連線一律經過 P2P 通道
    Set-IniValue $ini 'GBA' 'ServerIP' '127.0.0.1'
    Set-IniValue $ini 'GBA' 'LinkPort' $Port
    Set-IniValue $ini 'GBA' 'LinkTimeout' '1000'
    Set-IniValue $ini 'preferences' 'LinkNumPlayers' $Players
    Set-IniValue $ini 'preferences' 'pauseWhenInactive' '0'
    $cfg = Join-Path $dir 'vbam.ini'
    [IO.File]::WriteAllLines($cfg, $ini, (New-Object System.Text.UTF8Encoding $false))

    $vbamArgs = @('-c', "`"$cfg`"")
    if ($Rom) { $vbamArgs += "`"$((Resolve-Path -LiteralPath $Rom).Path)`"" }
    Start-Process -FilePath $exe -ArgumentList $vbamArgs -WorkingDirectory (Split-Path -Parent $exe) | Out-Null
}

Write-Host ''
if ($Mode -eq 'Host') {
    try { Set-Clipboard -Value $Code } catch { }
    Write-Host "配對碼：$Code" -ForegroundColor Yellow -NoNewline
    Write-Host '（已複製到剪貼簿，傳給朋友）'
    Write-Host ''
    Write-Host '主機步驟：'
    Write-Host '  1. 在 VBA-M 載入遊戲，進到遊戲內的連線／交換畫面前'
    Write-Host "  2. 選項 → 連線 → 開始網路連線 → 選「伺服器」、玩家人數 $Players → 開始"
    Write-Host '  3. 等朋友連進來（下方會顯示「遊戲連線已建立」）'
} else {
    Write-Host '加入步驟：'
    Write-Host '  1. 等下方出現「P2P 已打通」，並確認主機已在 VBA-M 按下開始'
    Write-Host '  2. 選項 → 連線 → 開始網路連線 → 選「用戶端」，主機位址 127.0.0.1 → 連線'
}
Write-Host ''
Write-Host '這個視窗是連線通道，遊玩期間請不要關閉。按 Ctrl+C 結束。' -ForegroundColor DarkGray
Write-Host ''

Add-Type -Path (Join-Path $here 'P2PLink.cs') -ReferencedAssemblies System.Web.Extensions
if ($Mode -eq 'Host') {
    [VbamP2P.Runner]::RunHost($Code, $Port, $Players - 1)
} else {
    [VbamP2P.Runner]::RunGuest($Code, $Port)
}
