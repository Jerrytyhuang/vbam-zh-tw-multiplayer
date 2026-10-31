<#
.SYNOPSIS
  打包發佈用的安裝包 zip（輸出到 dist\）。

.EXAMPLE
  .\tools\build-release.ps1 -Version 1.0.0
#>
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$name = "vbam-zh-tw-multiplayer-v$Version"
$dist = Join-Path $root 'dist'
$zipPath = Join-Path $dist "$name.zip"

# 封包內路徑 => repo 內來源
$files = [ordered]@{
    '安裝.bat'                         = 'installer\安裝.bat'
    'install.ps1'                      = 'installer\install.ps1'
    'zh_TW/LC_MESSAGES/wxvbam.mo'      = 'translations\zh_TW\LC_MESSAGES\wxvbam.mo'
    'multiplayer/Start-Link.ps1'       = 'multiplayer\Start-Link.ps1'
    'multiplayer/多開聯機.bat'         = 'multiplayer\多開聯機.bat'
    'multiplayer/Start-Online.ps1'     = 'multiplayer\Start-Online.ps1'
    'multiplayer/P2PLink.cs'           = 'multiplayer\P2PLink.cs'
    'multiplayer/網路連線.bat'         = 'multiplayer\網路連線.bat'
    'README.md'                        = 'README.md'
    'LICENSE'                          = 'LICENSE'
}

New-Item -ItemType Directory -Force $dist | Out-Null
Remove-Item $zipPath -ErrorAction SilentlyContinue

# Compress-Archive (PS 5.1) mangles non-ASCII names and uses '\' separators; write entries directly as UTF-8
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create', [Text.Encoding]::UTF8)
try {
    foreach ($entry in $files.Keys) {
        $src = Join-Path $root $files[$entry]
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $src, "$name/$entry", 'Optimal')
    }
} finally {
    $zip.Dispose()
}
Get-Item $zipPath
