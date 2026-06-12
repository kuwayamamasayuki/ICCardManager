# ============================================================
# check-mapped-drives.ps1
#
# Issue #1584 / PR #1589 関連:
# インストーラーがマップトドライブを検出できない問題を
# ユーザー環境で診断するスクリプト。
#
# 通常ユーザー権限で実行されることを前提とする
# （= ExecAsOriginalUser が再現したい条件と同じ）。
# 必ず check-mapped-drives.bat 経由で起動すること。
# ============================================================

$OutputPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'mapped_drives_diag.txt'
$sb = [System.Text.StringBuilder]::new()

function Add-Line { param([string]$text = '') [void]$sb.AppendLine($text) }
function Add-Section { param([string]$title) Add-Line ''; Add-Line "=== $title ===" }

Add-Line '=== マップトドライブ検出診断 ==='
Add-Line ("実行日時:       " + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
Add-Line ("実行ユーザー:   " + $env:USERNAME)
Add-Line ("コンピュータ名: " + $env:COMPUTERNAME)
Add-Line ("ドメイン:       " + $env:USERDOMAIN)
try {
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    Add-Line ("OS:             " + $os.Caption + " (" + $os.Version + ", build " + $os.BuildNumber + ")")
} catch {
    Add-Line ("OS:             " + [System.Environment]::OSVersion.VersionString)
}
Add-Line ("PowerShell:     " + $PSVersionTable.PSVersion)

# ─────────────────────────────────────────────
# [1] 管理者権限の有無
# ─────────────────────────────────────────────
Add-Section '[1] 管理者権限の有無'
$identity   = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin    = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Add-Line '★ 管理者として実行中（昇格セッション）'
    Add-Line ''
    Add-Line '   インストーラーの ExecAsOriginalUser は「通常セッション」を再現します。'
    Add-Line '   このスクリプトは必ず管理者権限「なし」で実行してください。'
    Add-Line '   再度 check-mapped-drives.bat を「普通にダブルクリック」して下さい。'
} else {
    Add-Line '○ 一般ユーザーとして実行中（通常セッション）'
    Add-Line '   インストーラー Method 2 (ExecAsOriginalUser + PowerShell) と同じ条件です。'
}

# ─────────────────────────────────────────────
# [2] net use
# ─────────────────────────────────────────────
Add-Section '[2] net use の結果（ユーザーから見えるネットワーク接続一覧）'
try {
    $netUse = (& net use 2>&1 | Out-String).TrimEnd()
    Add-Line $netUse
} catch {
    Add-Line ("net use 実行エラー: " + $_)
}

# ─────────────────────────────────────────────
# [3] HKCU\Network レジストリ
#     インストーラー Method 1 (DetectMappedDrives) のソース
# ─────────────────────────────────────────────
Add-Section '[3] HKCU\Network レジストリ（インストーラー Method 1 のソース）'
if (Test-Path 'HKCU:\Network') {
    $subkeys = Get-ChildItem -Path 'HKCU:\Network' -ErrorAction SilentlyContinue
    if (-not $subkeys -or @($subkeys).Count -eq 0) {
        Add-Line '(エントリなし → Method 1 はスキップされ Method 2 にフォールバック)'
    } else {
        foreach ($k in $subkeys) {
            $prop = Get-ItemProperty -Path $k.PSPath -ErrorAction SilentlyContinue
            $remotePath = if ($prop) { $prop.RemotePath } else { '(RemotePath 不明)' }
            Add-Line ("  Drive " + $k.PSChildName + ": RemotePath = " + $remotePath)
        }
    }
} else {
    Add-Line 'HKCU\Network が存在しません → Method 2 にフォールバックします。'
}

# ─────────────────────────────────────────────
# [4] Win32_MappedLogicalDisk
#     インストーラー Method 2 が叩く WMI クラス
# ─────────────────────────────────────────────
Add-Section '[4] Get-CimInstance Win32_MappedLogicalDisk（★インストーラー Method 2 と同一）'
try {
    $mapped = @(Get-CimInstance Win32_MappedLogicalDisk -ErrorAction Stop)
    if ($mapped.Count -eq 0) {
        Add-Line '★ 0 件 → ここが 0 件だとインストーラーは検出に失敗します。'
        Add-Line '   GPO 配信ドライブ、SMB1 経由、Workstation サービス停止 等を疑ってください。'
    } else {
        foreach ($m in $mapped) {
            Add-Line ("  DeviceID: " + $m.DeviceID + "  ProviderName: " + $m.ProviderName + "  SessionID: " + $m.SessionID)
        }
    }
} catch {
    Add-Line ("クエリエラー: " + $_)
}

# ─────────────────────────────────────────────
# [5] インストーラー完全同一のパイプライン (Out-File -Encoding ASCII)
# ─────────────────────────────────────────────
Add-Section '[5] インストーラー完全同一コマンドの出力 (Encoding ASCII)'
Add-Line 'インストーラー本体と同じコマンドラインで一時ファイルに書き出します。'
Add-Line 'ProviderName に日本語等の非 ASCII 文字があると ? に化けるため要注意。'
$tmpFile = Join-Path $env:TEMP 'mapped_drives_installer_query.txt'
if (Test-Path $tmpFile) { Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue }
try {
    Get-CimInstance Win32_MappedLogicalDisk | ForEach-Object {
        'DeviceID=' + $_.DeviceID
        'ProviderName=' + $_.ProviderName
        ''
    } | Out-File -FilePath $tmpFile -Encoding ASCII
    if (Test-Path $tmpFile) {
        $content = Get-Content $tmpFile -Raw
        Add-Line ("----- 出力ファイル (" + $tmpFile + ") -----")
        if ([string]::IsNullOrWhiteSpace($content)) {
            Add-Line '(空ファイル → インストーラー側で MappedDriveCount = 0 となります)'
        } else {
            Add-Line $content.TrimEnd()
        }
        Add-Line '----- ここまで -----'
        Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue
    } else {
        Add-Line '(出力ファイルが作成されませんでした → 重大な異常)'
    }
} catch {
    Add-Line ("Out-File 段階でエラー: " + $_)
}

# ─────────────────────────────────────────────
# [6] 代替: Win32_LogicalDisk DriveType=4
# ─────────────────────────────────────────────
Add-Section '[6] Get-CimInstance Win32_LogicalDisk DriveType=4（代替検出ロジック）'
Add-Line '[4] と件数が異なる場合、検出ロジックを Win32_LogicalDisk 側へ切替える余地あり。'
try {
    $logical = @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=4' -ErrorAction Stop)
    if ($logical.Count -eq 0) {
        Add-Line '(0 件)'
    } else {
        foreach ($l in $logical) {
            Add-Line ("  DeviceID: " + $l.DeviceID + "  ProviderName: " + $l.ProviderName + "  VolumeName: " + $l.VolumeName)
        }
    }
} catch {
    Add-Line ("クエリエラー: " + $_)
}

# ─────────────────────────────────────────────
# [7] Win32_NetworkConnection
# ─────────────────────────────────────────────
Add-Section '[7] Get-CimInstance Win32_NetworkConnection（代替検出ロジック）'
try {
    $conn = @(Get-CimInstance Win32_NetworkConnection -ErrorAction Stop)
    if ($conn.Count -eq 0) {
        Add-Line '(0 件)'
    } else {
        foreach ($c in $conn) {
            Add-Line ("  LocalName: " + $c.LocalName + "  RemoteName: " + $c.RemoteName + "  Persistent: " + $c.Persistent + "  ConnectionState: " + $c.ConnectionState)
        }
    }
} catch {
    Add-Line ("クエリエラー: " + $_)
}

# ─────────────────────────────────────────────
# [8] PowerShell から見える PSDrive
# ─────────────────────────────────────────────
Add-Section '[8] Get-PSDrive -PSProvider FileSystem'
try {
    $psd = Get-PSDrive -PSProvider FileSystem -ErrorAction Stop
    foreach ($d in $psd) {
        Add-Line ("  Name: " + $d.Name + "  Root: " + $d.Root + "  DisplayRoot: " + $d.DisplayRoot)
    }
} catch {
    Add-Line ("Get-PSDrive エラー: " + $_)
}

# ─────────────────────────────────────────────
# [9] wmic（旧来コマンド、参考）
# ─────────────────────────────────────────────
Add-Section '[9] wmic netuse list brief（旧コマンド／PR #1589 で廃止した経路）'
if (Get-Command wmic -ErrorAction SilentlyContinue) {
    try {
        $wmicOut = (& wmic netuse list brief 2>&1 | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($wmicOut)) {
            Add-Line '(空出力)'
        } else {
            Add-Line $wmicOut
        }
    } catch {
        Add-Line ("wmic 実行エラー: " + $_)
    }
} else {
    Add-Line 'wmic.exe が見つかりません（Win11 22H2 以降は既定で無効）。'
}

# ─────────────────────────────────────────────
# [10] PowerShell 実行ファイルの確認
# ─────────────────────────────────────────────
Add-Section '[10] PowerShell 実行ファイル'
$psPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
Add-Line ("インストーラーが起動するパス: " + $psPath)
if (Test-Path $psPath) {
    Add-Line '  → 存在します'
} else {
    Add-Line '  ★ 存在しません！ インストーラーの Method 2 が起動できません。'
}

Add-Line ''
Add-Line '=== 診断完了 ==='
Add-Line ''
Add-Line 'このファイルをそのまま開発者へ送付してください。'

# ─────────────────────────────────────────────
# 出力（UTF-8 BOM 付きでメモ帳が文字化けしない）
# ─────────────────────────────────────────────
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($OutputPath, $sb.ToString(), $utf8Bom)

Write-Host ''
Write-Host ('診断結果を保存しました: ' + $OutputPath)
Write-Host 'メモ帳で開きます…'
Start-Process notepad.exe -ArgumentList ('"' + $OutputPath + '"')
