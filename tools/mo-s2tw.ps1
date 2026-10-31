param([string]$InMo, [string]$OutMo, [string]$DumpTxt)

Add-Type -Namespace W -Name K -MemberDefinition @'
[DllImport("kernel32.dll", CharSet=CharSet.Unicode)]
public static extern int LCMapStringEx(string loc, uint flags, string src, int srcLen, System.Text.StringBuilder dst, int dstLen, System.IntPtr v, System.IntPtr r, System.IntPtr s);
'@
$LCMAP_TRADITIONAL_CHINESE = 0x04000000

function To-Trad([string]$s) {
    if ($s.Length -eq 0) { return $s }
    $sb = New-Object System.Text.StringBuilder ($s.Length + 16)
    $n = [W.K]::LCMapStringEx("zh-TW", $LCMAP_TRADITIONAL_CHINESE, $s, $s.Length, $sb, $sb.Capacity, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
    if ($n -le 0) { throw "LCMapStringEx failed" }
    return $sb.ToString(0, $n)
}

# Mainland -> Taiwan vocabulary, applied after character conversion (longest first)
$vocab = [ordered]@{
    '文件夾'='資料夾'; '目錄'='資料夾'; '文件'='檔案'; '保存'='儲存'; '另存為'='另存新檔'; '設置'='設定'; '配置'='設定'
    '默認'='預設'; '缺省'='預設'; '網絡'='網路'; '屏幕'='螢幕'; '全屏'='全螢幕'; '菜單'='選單'; '信息'='資訊'
    '程序'='程式'; '窗口'='視窗'; '加載'='載入'; '插件'='外掛程式'; '線程'='執行緒'; '光標'='游標'; '鼠標'='滑鼠'
    '用戶'='使用者'; '服務器'='伺服器'; '客戶端'='用戶端'; '端口'='連接埠'; '地址'='位址'; '遊戲手柄'='遊戲控制器'
    '手柄'='控制器'; '數據'='資料'; '支持'='支援'; '質量'='品質'; '分辨率'='解析度'; '調試'='偵錯'; '內存'='記憶體'
    '緩存'='快取'; '硬件'='硬體'; '軟件'='軟體'; '禁用'='停用'; '視頻'='視訊'; '音頻'='音訊'; '錄制'='錄製'
    '字體'='字型'; '圖標'='圖示'; '快捷鍵'='快速鍵'; '刷新'='重新整理'; '兼容'='相容'; '接口'='介面'; '界面'='介面'
    '打印機'='印表機'; '打印'='列印'; '模塊'='模組'; '寬帶'='寬頻'; '在線'='線上'; '鏈接'='連結'; '連接'='連線'
    '回放'='重播'; '幀'='影格'; '濾鏡'='濾鏡'; '采樣'='取樣'; '采用'='採用'; '帶寬'='頻寬'; '隊列'='佇列'
    '打開'='開啟'; '導入'='匯入'; '導出'='匯出'; '日志'='日誌'; '關于'='關於'; '反饋'='意見回饋'; '查看器'='檢視器'
    '非活動狀態'='非使用中'; '補丁'='修補檔'; '印度尼西亞'='印尼'; '意大利'='義大利'; '中文(中國)'='中文(中國)'
    '聯機連線'='連線'; '聯機'='連線'; '線纜'='連接線'; '內置'='內建'; '實時'='即時'; '超時'='逾時'; '幫助'='說明'
    '并'='並'; '徑里'='徑裡'; '當前'='目前'; '搜索'='搜尋'; '應用'='套用'; '本地'='本機'; '精靈塊'='精靈'
    '列表'='清單'; '圖像'='影像'; '退出'='結束'; '覆蓋'='覆寫'; '激活'='啟用'; '運行'='執行'; '創建'='建立'
'撤銷'='復原'; '選項卡'='索引標籤'; '複選框'='核取方塊'; '對話框'='對話方塊'
}

function To-TW([string]$s) {
    $t = To-Trad $s
    foreach ($k in $vocab.Keys) { $t = $t.Replace((To-Trad $k), $vocab[$k]).Replace($k, $vocab[$k]) }
    # undo over-eager replacements
    $t = $t.Replace('來來源','來源').Replace('資料來源','資料來源').Replace('連線埠','連接埠').Replace('開來源','開源')
    return $t
}

$bytes = [IO.File]::ReadAllBytes($InMo)
if ([BitConverter]::ToUInt32($bytes, 0) -ne [uint32]2500072158) { throw "not a little-endian .mo" }
$n    = [BitConverter]::ToInt32($bytes, 8)
$oOff = [BitConverter]::ToInt32($bytes, 12)
$tOff = [BitConverter]::ToInt32($bytes, 16)
$utf8 = New-Object System.Text.UTF8Encoding $false

$ids = New-Object 'System.Collections.Generic.List[byte[]]'
$strs = New-Object 'System.Collections.Generic.List[byte[]]'
$dump = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt $n; $i++) {
    $ol = [BitConverter]::ToInt32($bytes, $oOff + 8*$i); $op = [BitConverter]::ToInt32($bytes, $oOff + 8*$i + 4)
    $tl = [BitConverter]::ToInt32($bytes, $tOff + 8*$i); $tp = [BitConverter]::ToInt32($bytes, $tOff + 8*$i + 4)
    $id = New-Object byte[] $ol; [Array]::Copy($bytes, $op, $id, 0, $ol)
    $tr = $utf8.GetString($bytes, $tp, $tl)
    if ($ol -eq 0) {
        $tr = $tr -replace 'Language: zh_CN', 'Language: zh_TW' -replace 'Language-Team: [^\n]*', 'Language-Team: Chinese (Taiwan), converted from zh_CN'
    } else {
        # plural forms are NUL-separated; convert each part
        $tr = ($tr -split "`0" | ForEach-Object { To-TW $_ }) -join "`0"
        [void]$dump.AppendLine(($utf8.GetString($id) -replace "`0",' | ') + "`t=> " + ($tr -replace "`0",' | '))
    }
    $ids.Add($id); $strs.Add($utf8.GetBytes($tr))
}

# Rebuild .mo (no hash table; original ordering is already sorted by msgid)
$hdr = 28; $origTab = $hdr; $transTab = $origTab + 8*$n; $data = $transTab + 8*$n
$ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
$bw.Write([uint32]2500072158); $bw.Write([int]0); $bw.Write([int]$n); $bw.Write([int]$origTab); $bw.Write([int]$transTab); $bw.Write([int]0); $bw.Write([int]$data)
$pos = $data; $oEnt = @(); $tEnt = @()
foreach ($b in $ids)  { $oEnt += ,@($b.Length, $pos); $pos += $b.Length + 1 }
foreach ($b in $strs) { $tEnt += ,@($b.Length, $pos); $pos += $b.Length + 1 }
foreach ($e in $oEnt) { $bw.Write([int]$e[0]); $bw.Write([int]$e[1]) }
foreach ($e in $tEnt) { $bw.Write([int]$e[0]); $bw.Write([int]$e[1]) }
foreach ($b in $ids)  { $bw.Write($b); $bw.Write([byte]0) }
foreach ($b in $strs) { $bw.Write($b); $bw.Write([byte]0) }
$bw.Flush()
New-Item -ItemType Directory -Force (Split-Path $OutMo) | Out-Null
[IO.File]::WriteAllBytes($OutMo, $ms.ToArray())
if ($DumpTxt) { [IO.File]::WriteAllText($DumpTxt, $dump.ToString(), $utf8) }
"Converted $n entries -> $OutMo"
