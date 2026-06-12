# ============================================================
# check-mapped-drives-v2.ps1
#
# Issue #1584 / PR #1595 後も解消しない問題の追加診断。
# v1 ([1]-[10]) に加え、以下の仮説を検証する [11]-[15] を追加。
#
#   仮説 A: Inno Setup の LoadStringsFromFile が UTF-8 BOM を
#           自動検出しない可能性
#   仮説 B: net use 自体が失敗している可能性 (認証/GPO 等)
#   仮説 C: SHBrowseForFolder が net use 後のドライブを反映しない
#   仮説 D: EnableLinkedConnections レジストリ前提との不一致
#
# 通常ユーザー権限で実行されることを前提とする。
# 必ず check-mapped-drives-v2.bat 経由で起動すること。
# ============================================================

$OutputPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'mapped_drives_diag_v2.txt'
$sb = [System.Text.StringBuilder]::new()

function Add-Line { param([string]$text = '') [void]$sb.AppendLine($text) }
function Add-Section { param([string]$title) Add-Line ''; Add-Line "=== $title ===" }

Add-Line '=== マップトドライブ検出診断 v2 ==='
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
    Add-Line '   再度 check-mapped-drives-v2.bat を「普通にダブルクリック」して下さい。'
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
# ─────────────────────────────────────────────
Add-Section '[4] Get-CimInstance Win32_MappedLogicalDisk（★インストーラー Method 2 と同一）'
$mapped = @()
try {
    $mapped = @(Get-CimInstance Win32_MappedLogicalDisk -ErrorAction Stop)
    if ($mapped.Count -eq 0) {
        Add-Line '★ 0 件 → ここが 0 件だとインストーラーは検出に失敗します。'
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
#     ※ rc1 では UTF8 に変更済みだが、v1 比較用に残す
# ─────────────────────────────────────────────
Add-Section '[5] Out-File -Encoding ASCII（v1 比較用、PR #1595 前の挙動）'
Add-Line 'PR #1595 前のコマンド。ProviderName の非 ASCII が ? に化けることを再確認。'
$tmpAscii = Join-Path $env:TEMP 'mapped_drives_ascii.txt'
if (Test-Path $tmpAscii) { Remove-Item $tmpAscii -Force -ErrorAction SilentlyContinue }
try {
    Get-CimInstance Win32_MappedLogicalDisk | ForEach-Object {
        'DeviceID=' + $_.DeviceID
        'ProviderName=' + $_.ProviderName
        ''
    } | Out-File -FilePath $tmpAscii -Encoding ASCII
    if (Test-Path $tmpAscii) {
        $content = Get-Content $tmpAscii -Raw
        if ([string]::IsNullOrWhiteSpace($content)) {
            Add-Line '(空ファイル)'
        } else {
            Add-Line $content.TrimEnd()
        }
        Remove-Item $tmpAscii -Force -ErrorAction SilentlyContinue
    }
} catch {
    Add-Line ('エラー: ' + $_)
}

# ─────────────────────────────────────────────
# [6] Win32_LogicalDisk DriveType=4
# ─────────────────────────────────────────────
Add-Section '[6] Get-CimInstance Win32_LogicalDisk DriveType=4（代替検出ロジック）'
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
# [8] Get-PSDrive
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
# [9] wmic（参考、Win11 で廃止）
# ─────────────────────────────────────────────
Add-Section '[9] wmic netuse list brief（参考）'
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
# [10] PowerShell 実行ファイル
# ─────────────────────────────────────────────
Add-Section '[10] PowerShell 実行ファイル'
$psPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
Add-Line ("インストーラーが起動するパス: " + $psPath)
if (Test-Path $psPath) {
    Add-Line '  → 存在します'
} else {
    Add-Line '  ★ 存在しません！'
}

# ═════════════════════════════════════════════
# ★ v2 追加項目: [11]-[15]
# ═════════════════════════════════════════════

# ─────────────────────────────────────────────
# [11] EnableLinkedConnections レジストリ確認（仮説 D）
# ─────────────────────────────────────────────
Add-Section '[11] EnableLinkedConnections レジストリ（仮説 D: UAC セッション分離の前提）'
Add-Line 'HKLM\SYSTEM\CurrentControlSet\Control\Lsa\EnableLinkedConnections'
Add-Line '  = 1 : UAC 昇格セッションでもユーザーマッピングが見える設定（本来は再マッピング不要）'
Add-Line '  = 0 / 未設定 : デフォルト挙動（Issue #1584 の問題が発生し得る）'
$elcKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Lsa'
try {
    $elc = Get-ItemProperty -Path $elcKey -Name EnableLinkedConnections -ErrorAction Stop
    Add-Line ('  値: ' + $elc.EnableLinkedConnections)
} catch {
    Add-Line '  値: 未設定（デフォルト挙動）'
}

# ─────────────────────────────────────────────
# [12] -Encoding UTF8 のバイナリ確認（仮説 A）
# ─────────────────────────────────────────────
Add-Section '[12] Out-File -Encoding UTF8 のバイナリ確認（仮説 A: BOM 自動検出の検証）'
Add-Line 'PR #1595 (rc1) で -Encoding ASCII → -Encoding UTF8 に変更。'
Add-Line 'PowerShell 5.1 は BOM 付き UTF-8 を書き出すはず。先頭バイトを確認する。'
$tmpUtf8 = Join-Path $env:TEMP 'mapped_drives_utf8.txt'
if (Test-Path $tmpUtf8) { Remove-Item $tmpUtf8 -Force -ErrorAction SilentlyContinue }
try {
    Get-CimInstance Win32_MappedLogicalDisk | ForEach-Object {
        'DeviceID=' + $_.DeviceID
        'ProviderName=' + $_.ProviderName
        ''
    } | Out-File -FilePath $tmpUtf8 -Encoding UTF8
    if (Test-Path $tmpUtf8) {
        $bytes = [System.IO.File]::ReadAllBytes($tmpUtf8)
        Add-Line ('  ファイルサイズ: ' + $bytes.Length + ' bytes')
        if ($bytes.Length -ge 3) {
            $bomHex = '{0:X2} {1:X2} {2:X2}' -f $bytes[0], $bytes[1], $bytes[2]
            $isBom = ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
            Add-Line ('  先頭 3 バイト: ' + $bomHex + ' → BOM ' + $(if ($isBom) {'あり ★期待通り'} else {'なし ★想定外!'}))
        }
        Add-Line '  先頭 128 バイトの hex dump:'
        $hex = ($bytes | Select-Object -First 128 | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
        # 16 バイトごとに改行
        $hexLines = @()
        $tokens = $hex -split ' '
        for ($i = 0; $i -lt $tokens.Length; $i += 16) {
            $hexLines += '    ' + (($tokens[$i..([Math]::Min($i+15, $tokens.Length-1))]) -join ' ')
        }
        $hexLines | ForEach-Object { Add-Line $_ }
        Add-Line ''
        Add-Line '  UTF-8 としてデコードした内容:'
        $content = [System.IO.File]::ReadAllText($tmpUtf8, [System.Text.Encoding]::UTF8)
        ($content.TrimEnd() -split "`r?`n") | ForEach-Object { Add-Line ('    ' + $_) }
        Remove-Item $tmpUtf8 -Force -ErrorAction SilentlyContinue
    }
} catch {
    Add-Line ('エラー: ' + $_)
}

# ─────────────────────────────────────────────
# [13] -Encoding Unicode (UTF-16 LE) のバイナリ確認（仮説 A の代案）
# ─────────────────────────────────────────────
Add-Section '[13] Out-File -Encoding Unicode (UTF-16 LE) のバイナリ確認（rc2 候補）'
Add-Line 'UTF-8 BOM が Inno Setup で効かない場合、UTF-16 LE BOM 付きを試す。'
Add-Line 'TStringList.LoadFromFile は UTF-16 LE BOM 付きを確実にサポートする。'
$tmpUtf16 = Join-Path $env:TEMP 'mapped_drives_utf16.txt'
if (Test-Path $tmpUtf16) { Remove-Item $tmpUtf16 -Force -ErrorAction SilentlyContinue }
try {
    Get-CimInstance Win32_MappedLogicalDisk | ForEach-Object {
        'DeviceID=' + $_.DeviceID
        'ProviderName=' + $_.ProviderName
        ''
    } | Out-File -FilePath $tmpUtf16 -Encoding Unicode
    if (Test-Path $tmpUtf16) {
        $bytes = [System.IO.File]::ReadAllBytes($tmpUtf16)
        Add-Line ('  ファイルサイズ: ' + $bytes.Length + ' bytes')
        if ($bytes.Length -ge 2) {
            $bomHex = '{0:X2} {1:X2}' -f $bytes[0], $bytes[1]
            $isBom = ($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE)
            Add-Line ('  先頭 2 バイト: ' + $bomHex + ' → UTF-16 LE BOM ' + $(if ($isBom) {'あり ★期待通り'} else {'なし ★想定外!'}))
        }
        Add-Line '  先頭 128 バイトの hex dump:'
        $hex = ($bytes | Select-Object -First 128 | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
        $hexLines = @()
        $tokens = $hex -split ' '
        for ($i = 0; $i -lt $tokens.Length; $i += 16) {
            $hexLines += '    ' + (($tokens[$i..([Math]::Min($i+15, $tokens.Length-1))]) -join ' ')
        }
        $hexLines | ForEach-Object { Add-Line $_ }
        Add-Line ''
        Add-Line '  UTF-16 LE としてデコードした内容:'
        $content = [System.IO.File]::ReadAllText($tmpUtf16, [System.Text.Encoding]::Unicode)
        ($content.TrimEnd() -split "`r?`n") | ForEach-Object { Add-Line ('    ' + $_) }
        Remove-Item $tmpUtf16 -Force -ErrorAction SilentlyContinue
    }
} catch {
    Add-Line ('エラー: ' + $_)
}

# ─────────────────────────────────────────────
# [14] 試験的 net use（仮説 B）
# ─────────────────────────────────────────────
Add-Section '[14] 試験的 net use の動作確認（仮説 B: net use 自体が失敗している可能性）'
Add-Line '通常権限セッションで net use が実行できるか試験的に確認します。'
Add-Line '※ このスクリプトは通常権限なので、昇格セッションでの挙動は別途確認が必要 ([15] 参照)。'
if (-not $mapped -or $mapped.Count -eq 0) {
    Add-Line '対象なし → [4] で 0 件のためスキップ'
} else {
    # 空きドライブ文字探索: Get-PSDrive と Win32_MappedLogicalDisk の両方から
    # 使用中の文字を集めて除外する（Disconnected 状態のドライブも漏らさないため）
    $usedFromPsDrive = @((Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue).Name | ForEach-Object { $_.ToUpper() })
    $usedFromMapped  = @($mapped | ForEach-Object { ($_.DeviceID -replace ':', '').ToUpper() })
    $reserved        = @('A','B','C')   # フロッピー予約 (A,B) とシステムドライブ (C)
    $allUsed         = ($usedFromPsDrive + $usedFromMapped + $reserved) | Select-Object -Unique
    # Z から逆順に探索（Windows の慣例: ネットワークドライブは末尾アルファベットから割当）
    $candidates = 'Z','Y','X','W','V','U','T','S','R','Q','P','O','N','M','L','K','J','I','H','G','F','E','D'
    $testLetter = $null
    foreach ($c in $candidates) {
        if ($allUsed -notcontains $c) { $testLetter = $c; break }
    }
    $usedSummary = (($usedFromPsDrive + $usedFromMapped) | Select-Object -Unique | Sort-Object) -join ', '
    Add-Line ('検出済み使用中ドライブ: ' + $usedSummary)
    if (-not $testLetter) {
        Add-Line '★ D: 〜 Z: すべてが使用中。空きを作らないと試験できません。'
        Add-Line '   （手動で不要なドライブを切断するか、Disconnected 状態のドライブを削除してください）'
    } else {
        Add-Line ('採用した空きドライブ文字: ' + $testLetter + ':')
        $first = $mapped[0]
        $remotePath = $first.ProviderName
        Add-Line ('試験コマンド: net use ' + $testLetter + ': "' + $remotePath + '" /persistent:no')
        try {
            $out = & net use ($testLetter + ':') $remotePath /persistent:no 2>&1
            $exitCode = $LASTEXITCODE
            Add-Line ('  ExitCode: ' + $exitCode + ' ' + $(if ($exitCode -eq 0) {'(成功)'} else {'(失敗)'}))
            Add-Line '  出力:'
            ($out | Out-String).TrimEnd().Split("`n") | ForEach-Object { Add-Line ('    ' + $_.TrimEnd()) }
            $rootPath = $testLetter + ':\'
            if (Test-Path $rootPath) {
                Add-Line ('  → ' + $rootPath + ' へアクセス可能を確認')
            } else {
                Add-Line ('  → ' + $rootPath + ' にアクセスできない')
            }
        } catch {
            Add-Line ('  net use 実行で例外: ' + $_)
        }
        # クリーンアップ
        try {
            $delOut = & net use ($testLetter + ':') /delete 2>&1
            Add-Line ('  クリーンアップ: net use ' + $testLetter + ': /delete (ExitCode: ' + $LASTEXITCODE + ')')
        } catch {
            Add-Line ('  クリーンアップ失敗（手動で net use ' + $testLetter + ': /delete を実行してください）')
        }
    }
}

# ─────────────────────────────────────────────
# [15] 次の手順（手動: インストーラーログ取得）
# ─────────────────────────────────────────────
# 検出済みドライブの値を例コマンドに埋め込む（コピー&ペースト用）
$exampleProvider = '\\<サーバ>\<共有名>'
$exampleSourceLabel = ''
if ($mapped -and $mapped.Count -gt 0) {
    $exampleProvider = $mapped[0].ProviderName
    $exampleSourceLabel = ' （上記は [4] で ' + $mapped[0].DeviceID + ' として検出された共有のパスです）'
}
# [14] で算出した空きドライブ文字を再利用。全部埋まっていればプレースホルダ + 案内文を出す
if ($testLetter) {
    $exampleTestLetter = $testLetter
    $exampleTestLetterNote = ''
} else {
    $exampleTestLetter = '<空きドライブ>'
    $exampleTestLetterNote = '  ※ あなたの環境では D: 〜 Z: すべてのドライブ文字が使用中のため、上記の <空きドライブ>'
}

Add-Section '[15] 追加でお願いしたい確認手順'
Add-Line '本スクリプトの結果送付に加え、以下の 2 件をお願いします。'
Add-Line ''
Add-Line '【手順 A】インストーラー本体のログを取得'
Add-Line '  1. スタートメニュー（画面左下の Windows ロゴ）を開き、検索ボックスに'
Add-Line '     「cmd」と入力。出てきた「コマンド プロンプト」を【普通に】クリックして開く。'
Add-Line '     （「管理者として実行」は不要です）'
Add-Line '  2. 開いた黒い画面で、インストーラー .exe があるフォルダへ移動。'
Add-Line '     例) cd %USERPROFILE%\Downloads'
Add-Line '  3. 次のコマンドを 1 行で入力して Enter キーを押す:'
Add-Line ''
Add-Line '     ICCardManager_Setup_2.9.3-rc1.exe /LOG=%USERPROFILE%\Desktop\setup_rc1.log'
Add-Line ''
Add-Line '  4. 「このアプリがデバイスに変更を加えることを許可しますか？」という'
Add-Line '     青いウィンドウが表示されたら「はい」をクリックします。'
Add-Line '     （Windows が管理者権限の確認をしている画面です）'
Add-Line '  5. インストールウィザードが起動したら「データベースの保存先」ページまで'
Add-Line '     進み、「参照...」ボタンを押します。'
# [4] で検出された実際のドライブ文字を例示（被影響ユーザーが認識しやすい文言を動的生成）
$expectedDrivesText = if ($mapped -and $mapped.Count -gt 0) {
    $devIds = @($mapped | Select-Object -First 3 -ExpandProperty DeviceID)
    if ($devIds.Count -ge 2) {
        '     ' + ($devIds -join ', ') + ' などが見えなければ'
    } else {
        '     ' + $devIds[0] + ' ドライブが見えなければ'
    }
} else {
    '     マップトドライブが見えなければ'
}
Add-Line $expectedDrivesText
Add-Line '     そのまま「キャンセル」で構いません。'
Add-Line '  6. デスクトップに setup_rc1.log というファイルが生成されます。'
Add-Line ''
Add-Line '【手順 B】管理者権限のコマンド プロンプトで net use を試す'
Add-Line '  1. スタートメニュー（画面左下の Windows ロゴ）を開き、検索ボックスに'
Add-Line '     「cmd」と入力。'
Add-Line '  2. 出てきた「コマンド プロンプト」を【右クリック】して、表示される'
Add-Line '     メニューから「管理者として実行」を選びます。'
Add-Line '  3. 「このアプリがデバイスに変更を加えることを許可しますか？」という'
Add-Line '     青いウィンドウが表示されたら「はい」をクリックします。'
Add-Line '  4. 開いた黒い画面（コマンド プロンプト）に、以下のコマンドを 1 行で'
Add-Line '     入力して Enter キーを押します:'
Add-Line ''
Add-Line ('     net use ' + $exampleTestLetter + ': "' + $exampleProvider + '" /persistent:no')
if ($exampleSourceLabel) {
    Add-Line ('    ' + $exampleSourceLabel)
}
if ($exampleTestLetterNote) {
    Add-Line $exampleTestLetterNote
    Add-Line '     部分を別の文字（例: 既存ドライブを切断後の文字）に置き換えてください。'
}
Add-Line ''
Add-Line '  5. 表示された結果（「コマンドは正常に終了しました」または'
Add-Line '     エラーメッセージ・エラー番号）をメモするか、画面のスクリーンショットを'
Add-Line '     取ってください。'
Add-Line '  6. 最後に以下のコマンドでクリーンアップ:'
Add-Line ''
Add-Line ('     net use ' + $exampleTestLetter + ': /delete')
Add-Line ''
Add-Line '【送付物まとめ】'
Add-Line '  ・mapped_drives_diag_v2.txt（本ファイル）'
Add-Line '  ・setup_rc1.log（手順 A で生成）'
Add-Line '  ・手順 B の結果メモまたはスクリーンショット'

Add-Line ''
Add-Line '=== 診断完了 ==='

# ─────────────────────────────────────────────
# 出力（UTF-8 BOM 付き）
# ─────────────────────────────────────────────
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($OutputPath, $sb.ToString(), $utf8Bom)

Write-Host ''
Write-Host ('診断結果を保存しました: ' + $OutputPath)
Write-Host 'メモ帳で開きます…'
Start-Process notepad.exe -ArgumentList ('"' + $OutputPath + '"')
