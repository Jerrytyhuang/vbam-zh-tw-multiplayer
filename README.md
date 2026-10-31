# VBA-M 繁體中文 + 本機多開聯機

給 [VisualBoyAdvance-M](https://github.com/visualboyadvance-m/visualboyadvance-m)（Windows）用的兩樣東西：

1. **繁體中文介面**：官方只提供簡體中文（zh_CN），這裡是轉成繁體並改為台灣用語的翻譯檔。
2. **多開聯機啟動器**：在同一台電腦開 2～4 個 VBA-M 視窗，用本機連線模擬 GBA 連接線（交換、對戰）。

在 VBA-M **2.2.3**（內建 wxWidgets 3.3.2）上測試過。

## 快速安裝（推薦）

1. 到 [Releases](https://github.com/Jerrytyhuang/vbam-zh-tw-multiplayer/releases/latest) 下載 `vbam-zh-tw-multiplayer-v*.zip` 並解壓縮。
2. 關閉所有 VBA-M 視窗，執行 **`安裝.bat`**。

安裝程式會：

- 從 VBA-M 官方 GitHub 下載 v2.2.3 並**驗證數位簽章**（安裝包本身不含模擬器）
- 安裝到 `%LOCALAPPDATA%\Programs\VisualBoyAdvance-M`（不需要系統管理員權限）
- 放入繁體中文翻譯與多開聯機工具
- 把介面語言設為繁體中文（只改語言，其他設定保留，原設定檔備份為 `vbam.ini.before-zh-tw`）
- 在桌面建立「VisualBoyAdvance-M」與「VBA-M 多開聯機」捷徑

若已安裝過 VBA-M，可指定位置沿用：`安裝.bat -InstallDir "D:\VBA-M"`；不要捷徑則加 `-NoShortcut`。

## 手動安裝

1. 從 [VBA-M Releases](https://github.com/visualboyadvance-m/visualboyadvance-m/releases) 下載 `visualboyadvance-m-Win-x86_64.zip`，解壓縮到任一資料夾，並開啟一次讓它產生設定檔。
2. 把這個 repo 的檔案放到 `visualboyadvance-m.exe` 所在的資料夾，結構如下：

   ```
   visualboyadvance-m\
   ├─ visualboyadvance-m.exe
   ├─ zh_TW\LC_MESSAGES\wxvbam.mo     ← 來自 translations\
   └─ multiplayer\
      ├─ Start-Link.ps1
      └─ 多開聯機.bat
   ```

## 啟用繁體中文

關閉 VBA-M，編輯 `%LOCALAPPDATA%\visualboyadvance-m\vbam.ini` 的 `[Language]` 區段：

```ini
[Language]
Locale=138
ExternalTranslations=1
```

- `138` 是 wxWidgets 3.3.2 中 `wxLANGUAGE_CHINESE_TAIWAN` 的值。VBA-M 的「語言」選單沒有繁體中文，若從選單切換成其他語言，需要再改回這兩行。
- `ExternalTranslations=1` 讓 VBA-M 讀取 exe 旁邊的翻譯檔，而不是內建的翻譯。

## 多開聯機

把 ROM 檔**拖曳**到 `multiplayer\多開聯機.bat` 上：

- 拖 1 個 ROM → 開 2 個視窗玩同一個遊戲
- 拖 2～4 個 ROM → 每位玩家各自的遊戲（例如紅寶石 ↔ 藍寶石）

或用 PowerShell：

```powershell
.\Start-Link.ps1 -Rom "game.gba" -Players 4
.\Start-Link.ps1 -Rom "ruby.gba","sapphire.gba" -LinkType Cable
```

| 參數 | 說明 |
|---|---|
| `-Rom` | 1～4 個 ROM 路徑；ROM 數少於人數時，其餘玩家用最後一個 |
| `-Players` | 2～4，預設為 ROM 數量（至少 2） |
| `-LinkType` | `Cable`（預設）、`Wireless`、`GameBoy` |
| `-Scale` | 畫面倍率，預設依螢幕大小自動計算 |

### 啟動器做了什麼

以你的主設定檔為基礎，在 `multiplayer\P1`～`P4` 為每位玩家產生獨立的 `vbam.ini`，並用 `-c` 參數啟動：

- **本機連線**：`LinkProto=1`、`LinkAuto=1`，視窗開啟後自動連上，不經網路、不需設定防火牆
- **存檔分開**：每位玩家的遊戲存檔與即時存檔放在 `P<n>\saves`、`P<n>\states`，不會互相覆蓋
- **背景不暫停**：`pauseWhenInactive=0`，否則失去焦點的視窗會卡住連線
- **操作**：玩家 N 用第 N 支手把（背景視窗也能操作）；鍵盤只控制目前選中的視窗
- **視窗排列**：2 人左右並排、3～4 人 2×2，會依螢幕可用區域與 DPI 縮放調整

你的主設定檔不會被修改。

## 網際網路連線（P2P 配對碼）

和不同地方的朋友連線，不需要開連接埠、不需要架伺服器，雙方輸入**相同的配對碼**即可。

1. **主機**：把 ROM 拖曳到 `multiplayer\網路連線.bat`（或桌面「VBA-M 網路連線」捷徑），選「建立房間」。直接按 Enter 會產生配對碼（已複製到剪貼簿），傳給朋友。
2. **朋友**：同樣拖曳 ROM 到 `網路連線.bat`，選「加入房間」並輸入配對碼，等出現「P2P 已打通」。
3. **主機**在 VBA-M：選項 → 連線 → 開始網路連線 → 選「伺服器」、人數 → 確定。
4. **朋友**在 VBA-M：選項 → 連線 → 開始網路連線 → 選「用戶端」，伺服器 `127.0.0.1` → 確定。

主機最多可讓 3 位朋友加入（共 4 人）。黑色的連線視窗是通道，遊玩期間請不要關閉。
VBA-M 對話框裡的「警告：連線可能無法工作於網際網路以及區域網路」是它的固定提示，不影響使用。

### 原理

```
VBA-M(用戶端) → 127.0.0.1 → [通道] ══ UDP P2P ══ [通道] → 127.0.0.1 → VBA-M(伺服器)
```

- 配對碼經 PBKDF2 推導出信令頻道與金鑰（不分大小寫，忽略空白與 `-`）
- 透過 [ntfy.sh](https://ntfy.sh) 交換彼此位址；內容以 AES-256 + HMAC-SHA256 加密，ntfy 只看得到密文
- 以 STUN 取得公開位址，同時嘗試 IPv6、IPv4 打洞與區域網路，第一個通的就用
- 遊戲資料**只走 P2P**，在 UDP 上實作重傳與排序的可靠傳輸；每個封包都以 HMAC 驗證
- 程式碼在 `multiplayer\P2PLink.cs`，由 PowerShell 即時編譯，不需要安裝任何東西

### 限制

- 若**雙方都**在對稱型 NAT 後面（常見於行動網路、部分電信 CGNAT），打洞會失敗，畫面會提示。此時可改用 Tailscale／ZeroTier 等虛擬區域網路，或由主機在路由器開放 TCP 5738 讓朋友直接連。
- 首次執行時 Windows 防火牆可能詢問是否允許 PowerShell 存取網路，請允許「私人網路」。
- 配對碼太短容易和陌生人撞碼，至少需要 6 個英數字；建議使用自動產生的。

## 重新產生翻譯檔

`tools\mo-s2tw.ps1` 用 Windows 內建的 `LCMapStringEx` 做簡轉繁，再套用一份大陸 → 台灣用語對照表（文件→檔案、默認→預設、聯機→連線…）。VBA-M 更新翻譯後可重新產生：

```powershell
# 從 VBA-M release 的 translations.zip 取出 zh_CN\LC_MESSAGES\wxvbam.mo
.\tools\mo-s2tw.ps1 -InMo zh_CN\LC_MESSAGES\wxvbam.mo -OutMo zh_TW\LC_MESSAGES\wxvbam.mo -DumpTxt dump.txt
```

`-DumpTxt` 會輸出「英文原文 => 繁體翻譯」對照，方便檢查。這是自動轉換，少數詞句仍可能保留大陸用語，歡迎修正對照表。

## 授權

翻譯檔衍生自 VisualBoyAdvance-M 的 zh_CN 翻譯，與本 repo 其他內容一同以 [GPL-2.0](LICENSE) 授權。本 repo 不包含任何 ROM 或 BIOS。
