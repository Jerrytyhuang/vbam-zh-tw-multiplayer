# VBA-M 繁體中文 + 本機多開聯機

給 [VisualBoyAdvance-M](https://github.com/visualboyadvance-m/visualboyadvance-m)（Windows）用的兩樣東西：

1. **繁體中文介面**：官方只提供簡體中文（zh_CN），這裡是轉成繁體並改為台灣用語的翻譯檔。
2. **多開聯機啟動器**：在同一台電腦開 2～4 個 VBA-M 視窗，用本機連線模擬 GBA 連接線（交換、對戰）。

在 VBA-M **2.2.3**（內建 wxWidgets 3.3.2）上測試過。

## 安裝

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

## 重新產生翻譯檔

`tools\mo-s2tw.ps1` 用 Windows 內建的 `LCMapStringEx` 做簡轉繁，再套用一份大陸 → 台灣用語對照表（文件→檔案、默認→預設、聯機→連線…）。VBA-M 更新翻譯後可重新產生：

```powershell
# 從 VBA-M release 的 translations.zip 取出 zh_CN\LC_MESSAGES\wxvbam.mo
.\tools\mo-s2tw.ps1 -InMo zh_CN\LC_MESSAGES\wxvbam.mo -OutMo zh_TW\LC_MESSAGES\wxvbam.mo -DumpTxt dump.txt
```

`-DumpTxt` 會輸出「英文原文 => 繁體翻譯」對照，方便檢查。這是自動轉換，少數詞句仍可能保留大陸用語，歡迎修正對照表。

## 授權

翻譯檔衍生自 VisualBoyAdvance-M 的 zh_CN 翻譯，與本 repo 其他內容一同以 [GPL-2.0](LICENSE) 授權。本 repo 不包含任何 ROM 或 BIOS。
