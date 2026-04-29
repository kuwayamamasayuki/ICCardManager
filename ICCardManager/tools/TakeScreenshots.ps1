<#
.SYNOPSIS
    ICCardManager スクリーンショット取得支援スクリプト

.DESCRIPTION
    マニュアル用のスクリーンショットを対話的に取得します。
    各画面で操作を行い、準備ができたらEnterキーを押すとスクリーンショットを保存します。
    ICCardManagerのウィンドウを自動検索するため、PowerShellウィンドウではなく
    アプリのウィンドウが撮影されます。

.PARAMETER RequiredOnly
    必須画面（7枚）のみを取得します。

.PARAMETER All
    オプション画面も含むすべての画面（49枚）を取得します。
    一部のエントリは `ManualOnly = $true` が指定されており、
    スクリプトでは撮影せず手動でのPrtSc取得を促します
    （別プロセスや特殊起動条件が必要な画面）。
    `DelaySeconds = N` が指定されたエントリは Enter 入力後に N 秒の
    カウントダウンを挟んでから撮影します。カウントダウン中に
    ユーザーがアプリ側へフォーカスを戻してドロップダウン等を
    展開する想定で、撮影直前の SetForegroundWindow をスキップします。

.PARAMETER OutputDir
    出力先ディレクトリを指定します。デフォルトは docs/screenshots/ です。

.PARAMETER Only
    特定の画面だけを再撮影する場合に、対象の Name（拡張子省略可）を
    カンマ区切りまたは配列で指定します。`-RequiredOnly` / `-All` の
    指定有無に関わらず、必須・オプション両方の中から名前で絞り込みます。
    指定可能な Name は `-List` で確認できます。

.PARAMETER List
    撮影可能な画面の一覧（Name とタイトル）を表示して終了します。
    `-Only` に指定する名前を確認するときに使います。

.EXAMPLE
    .\TakeScreenshots.ps1
    必須画面のみを取得します。

.EXAMPLE
    .\TakeScreenshots.ps1 -All
    すべての画面を取得します。

.EXAMPLE
    .\TakeScreenshots.ps1 -List
    撮影可能な画面の Name とタイトル一覧を表示します。

.EXAMPLE
    .\TakeScreenshots.ps1 -Only card_registration_mode
    指定した画面だけを再撮影します（拡張子は省略可）。

.EXAMPLE
    .\TakeScreenshots.ps1 -Only card_registration_mode,error_no_reader
    複数画面をまとめて再撮影します。

.NOTES
    作成日: 2026-02-02
    Issue: #427, #435, #849, #1409-#1418
#>

param(
    [switch]$RequiredOnly,
    [switch]$All,
    [string]$OutputDir,
    [string[]]$Only,
    [switch]$List
)

# 必要なアセンブリをロード
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Win32 API定義（DPI対応版・マルチウィンドウ対応）
if (-not ([System.Management.Automation.PSTypeName]'Win32ApiDpi3').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class Win32ApiDpi3 {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // DWM APIを使用してウィンドウの実際の境界を取得（DPI対応）
    public static bool GetWindowRectDpi(IntPtr hWnd, out RECT rect) {
        int result = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(RECT)));
        if (result == 0) {
            return true;
        }
        return GetWindowRect(hWnd, out rect);
    }

    // 指定プロセスの最前面ウィンドウのハンドルを取得（Z-order順）
    public static IntPtr GetTopmostProcessWindow(int processId) {
        IntPtr topWindow = IntPtr.Zero;

        EnumWindows((hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;

            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);

            if (pid == processId) {
                RECT rect;
                if (GetWindowRectDpi(hWnd, out rect)) {
                    if (rect.Right - rect.Left > 0 && rect.Bottom - rect.Top > 0) {
                        // EnumWindowsはZ-order（前面→背面）で列挙するため最初のヒットが最前面
                        if (topWindow == IntPtr.Zero) {
                            topWindow = hWnd;
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        return topWindow;
    }

    // 指定プロセスの全ウィンドウを含む境界を取得
    public static RECT GetProcessWindowsBounds(int processId) {
        RECT bounds = new RECT();
        bounds.Left = int.MaxValue;
        bounds.Top = int.MaxValue;
        bounds.Right = int.MinValue;
        bounds.Bottom = int.MinValue;
        bool found = false;

        EnumWindows((hWnd, lParam) => {
            if (!IsWindowVisible(hWnd)) return true;

            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);

            if (pid == processId) {
                RECT rect;
                if (GetWindowRectDpi(hWnd, out rect)) {
                    if (rect.Right - rect.Left > 0 && rect.Bottom - rect.Top > 0) {
                        if (rect.Left < bounds.Left) bounds.Left = rect.Left;
                        if (rect.Top < bounds.Top) bounds.Top = rect.Top;
                        if (rect.Right > bounds.Right) bounds.Right = rect.Right;
                        if (rect.Bottom > bounds.Bottom) bounds.Bottom = rect.Bottom;
                        found = true;
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        if (!found) {
            bounds.Left = 0;
            bounds.Top = 0;
            bounds.Right = 0;
            bounds.Bottom = 0;
        }

        return bounds;
    }
}
"@
    # プロセスをDPI対応にする
    [Win32ApiDpi3]::SetProcessDPIAware() | Out-Null
}

# スクリプトのディレクトリを取得
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# 出力ディレクトリの設定
if ([string]::IsNullOrEmpty($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "docs\screenshots"
}

# 出力ディレクトリが存在しない場合は作成
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# 画面定義
$requiredScreens = @(
    @{
        Name = "main.png"
        Title = "メイン画面（待機状態）"
        Instructions = "アプリが起動し、待機状態になったら"
    },
    @{
        Name = "staff_recognized.png"
        Title = "職員証認識後の画面"
        Instructions = "職員証をタッチし、「○○さん、交通系ICカードをタッチしてください」のポップアップが表示されたら"
    },
    @{
        Name = "lend.png"
        Title = "貸出完了画面"
        Instructions = "続けて交通系ICカードをタッチして貸出完了画面が表示されたら"
    },
    @{
        Name = "return.png"
        Title = "返却完了画面"
        Instructions = "再度、職員証をタッチし、同じカードをタッチして返却完了画面が表示されたら"
    },
    @{
        Name = "history.png"
        Title = "履歴照会画面"
        Instructions = "メイン画面でカードを選択して履歴が表示されたら"
    },
    @{
        Name = "card.png"
        Title = "カード管理画面"
        Instructions = "F3キーを押してカード管理画面が表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "staff.png"
        Title = "職員管理画面"
        Instructions = "F2キーを押して職員管理画面が表示されたら"
        ForegroundOnly = $true
    }
)

$optionalScreens = @(
    @{
        Name = "report.png"
        Title = "帳票出力画面"
        Instructions = "F1キーを押して帳票出力画面が表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "settings.png"
        Title = "設定画面"
        Instructions = "F5キーを押して設定画面が表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "system.png"
        Title = "システム管理画面"
        Instructions = "F6キーを押してシステム管理画面が表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "export.png"
        Title = "データ入出力画面"
        Instructions = "F4キーを押してデータ入出力画面が表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "busstop.png"
        Title = "バス停名入力ダイアログ"
        Instructions = "返却時にバス利用を検出させ、バス停名入力ダイアログが表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "ledger_detail_merge.png"
        Title = "履歴の統合分割（履歴詳細）"
        Instructions = "履歴一覧で行の「詳細」ボタンをクリックし、利用詳細ダイアログが表示されたら（ダブルクリックでは開きません）"
        ForegroundOnly = $true
    },
    @{
        Name = "history_merge.png"
        Title = "履歴の統合（履歴一覧）"
        Instructions = "履歴照会画面で複数行にチェックを入れた状態にしたら"
    },
    @{
        Name = "print_preview.png"
        Title = "帳票プレビュー画面"
        Instructions = "帳票作成画面で「プレビュー」ボタンをクリックし、プレビューが表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "report_excel.png"
        Title = "物品出納簿（Excel出力例）"
        Instructions = "帳票作成で出力したExcelファイルを開き、表示されたら（※手動でPrtScで撮影してください）"
    },
    @{
        Name = "installer_options.png"
        Title = "インストーラーオプション選択画面"
        Instructions = "インストーラーを実行し、オプション選択画面が表示されたら（※手動でPrtScで撮影してください）"
    },
    @{
        Name = "card_registration_mode.png"
        Title = "カード登録方法の選択画面"
        Instructions = "カード管理画面で「新規登録」をクリック、未登録カードをタッチしてカード種別選択ダイアログで「交通系ICカード」を選択した後、カード登録方法選択ダイアログ（新規購入／繰越）が表示されたら"
        ForegroundOnly = $true
    },
    # Issue #849: マニュアル用スクリーンショット追加
    # 優先度: 高（操作手順の説明があるのに画面イメージがない）
    @{
        Name = "ledger_row_edit.png"
        Title = "履歴行の修正ダイアログ"
        Instructions = "履歴照会画面で行の「変更」ボタンをクリックし、職員証で認証後、修正ダイアログが表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "operation_log.png"
        Title = "操作ログ画面"
        Instructions = "F6キーでシステム管理画面を開き、「操作ログ」ボタンをクリックして操作ログ画面が表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "import_preview.png"
        Title = "インポートプレビュー画面"
        Instructions = "F4キーでデータ入出力画面を開き、CSVファイルを選択して「プレビュー」ボタンをクリックし、プレビュー結果が表示されたら"
        ForegroundOnly = $true
    },
    # 優先度: 中（あると理解が深まる）
    @{
        Name = "incomplete_busstop.png"
        Title = "バス停名未入力一覧ダイアログ"
        Instructions = "メイン画面右側の「バス停名未入力」警告をクリックし、一覧ダイアログが表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "main_with_warnings.png"
        Title = "メイン画面（システム警告表示時）"
        Instructions = "バス停名未入力データがある状態でメイン画面に警告パネルが表示されたら"
    },
    @{
        Name = "card_type_selection.png"
        Title = "カード種別選択ダイアログ"
        Instructions = "未登録カードをICカードリーダーにタッチし、「職員証」か「交通系ICカード」かを選択するダイアログが表示されたら"
        ForegroundOnly = $true
    },
    # Issue #1409: ユーザーマニュアル §7.2 各設定項目のスクリーンショット
    @{
        Name = "settings_voice_dropdown.png"
        Title = "音声設定ドロップダウン展開"
        Instructions = "F5キーで設定画面を開いた状態で Enter。カウントダウン中にアプリへフォーカスを戻し、音声設定のドロップダウン（効果音/男性/女性/無し）を展開してください"
        ForegroundOnly = $true
        DelaySeconds = 5
    },
    @{
        Name = "settings_department_dropdown.png"
        Title = "部署設定ドロップダウン展開"
        Instructions = "設定画面を開いた状態で Enter。カウントダウン中にアプリへフォーカスを戻し、部署設定のドロップダウン（市長事務部局／企業会計部局）を展開してください"
        ForegroundOnly = $true
        DelaySeconds = 5
    },
    @{
        Name = "settings_fontsize_small.png"
        Title = "文字サイズ「小」"
        Instructions = "設定画面で文字サイズを「小」に変更し、変更が反映された表示状態で"
        ForegroundOnly = $true
    },
    @{
        Name = "settings_fontsize_medium.png"
        Title = "文字サイズ「中（標準）」"
        Instructions = "設定画面で文字サイズを「中（標準）」に変更し、変更が反映された表示状態で"
        ForegroundOnly = $true
    },
    @{
        Name = "settings_fontsize_large.png"
        Title = "文字サイズ「大」"
        Instructions = "設定画面で文字サイズを「大」に変更し、変更が反映された表示状態で"
        ForegroundOnly = $true
    },
    @{
        Name = "settings_fontsize_xlarge.png"
        Title = "文字サイズ「特大」"
        Instructions = "設定画面で文字サイズを「特大」に変更し、変更が反映された表示状態で"
        ForegroundOnly = $true
    },
    @{
        Name = "settings_toast_topright.png"
        Title = "トースト通知 表示位置「右上」"
        Instructions = "設定でトースト位置を「右上」に変更後、トースト通知を発生させて表示中の状態で（メイン画面とトーストの位置関係が見える構図）"
    },
    @{
        Name = "settings_toast_topleft.png"
        Title = "トースト通知 表示位置「左上」"
        Instructions = "設定でトースト位置を「左上」に変更後、トースト通知を発生させて表示中の状態で"
    },
    @{
        Name = "settings_toast_bottomright.png"
        Title = "トースト通知 表示位置「右下」"
        Instructions = "設定でトースト位置を「右下」に変更後、トースト通知を発生させて表示中の状態で"
    },
    @{
        Name = "settings_toast_bottomleft.png"
        Title = "トースト通知 表示位置「左下」"
        Instructions = "設定でトースト位置を「左下」に変更後、トースト通知を発生させて表示中の状態で"
    },
    # Issue #1410: ユーザーマニュアル §6.2 帳票作成完了画面
    @{
        Name = "report_completed_status.png"
        Title = "帳票出力完了ステータス表示"
        Instructions = "F1キーで帳票作成画面を開き、複数カードを一括作成。出力完了後にステータスバーに「N件の帳票を作成しました」と表示されたら（ファイル一覧ダイアログは現状未実装）"
        ForegroundOnly = $true
    },
    # Issue #1411: ユーザーマニュアル §9.3 エラーダイアログ
    # 「未登録カードエラー」専用ダイアログは存在せず、card_type_selection.png
    # （CardTypeSelectionDialog）と同一画面のため、マニュアルでは流用する。
    @{
        Name = "error_no_reader.png"
        Title = "カードリーダー未接続ステータス表示"
        Instructions = "PaSoRiを外した状態でアプリを起動し、ステータスバー右下が「リーダー: 切断」と表示されたら（カードリーダー未検出時のエラーダイアログは現状未実装）"
        ForegroundOnly = $true
    },
    @{
        Name = "warning_network_disconnected.png"
        Title = "ネットワーク切断警告（共有モード）"
        Instructions = "共有モードで稼働中にネットワークを切断し、メイン画面に切断警告バナーが表示された状態で"
    },
    # Issue #1412: ユーザーマニュアル §3.3 カード一覧の状態別
    @{
        Name = "card_list_status_mixed.png"
        Title = "カード一覧（状態混在）"
        Instructions = "メイン画面で「利用可（緑）」「貸出中（オレンジ）」「残額警告（赤背景）」のカードが同時に表示されている状態で"
    },
    @{
        Name = "card_list_sort_menu.png"
        Title = "カード一覧 並び替えメニュー"
        Instructions = "メイン画面でカード一覧を表示した状態で Enter。カウントダウン中にアプリへフォーカスを戻し、並び替えメニューを開いてください"
        DelaySeconds = 5
    },
    # Issue #1413: 管理者マニュアル §2.6 アンインストールデータ取り扱い選択
    @{
        Name = "uninstall_data_choice.png"
        Title = "アンインストール時データ取り扱い選択"
        Instructions = "アンインストーラーを実行し、データ取り扱い選択ダイアログ（すべて削除/データのみ残す/何も削除しない）が表示されたら（※InnoSetupの別プロセスのため手動撮影）"
        ManualOnly = $true
    },
    # Issue #1414: 管理者マニュアル §4 職員登録・編集ダイアログ
    @{
        Name = "staff_register_before_touch.png"
        Title = "職員新規登録ダイアログ（職員証タッチ前）"
        Instructions = "F2キーで職員管理画面を開き「新規登録」をクリック、職員証タッチ待ちの状態で"
        ForegroundOnly = $true
    },
    @{
        Name = "staff_register_after_touch.png"
        Title = "職員新規登録ダイアログ（職員証タッチ後）"
        Instructions = "新規登録ダイアログで職員証をタッチし、IDmが取り込まれた状態で（※IDm取込後の氏名欄への自動フォーカス遷移は未実装。手動で氏名欄をクリックしてから撮影）"
        ForegroundOnly = $true
    },
    @{
        Name = "staff_edit_dialog.png"
        Title = "職員情報編集ダイアログ"
        Instructions = "職員管理画面で職員行を選択して「編集」、職員情報編集ダイアログが表示されたら"
        ForegroundOnly = $true
    },
    # Issue #1415: 管理者マニュアル §5.3/§5.5 カード編集・払い戻しダイアログ
    @{
        Name = "card_edit_dialog.png"
        Title = "交通系ICカード情報編集ダイアログ"
        Instructions = "F3キーでカード管理画面を開き、行を選択して「編集」、カード情報編集ダイアログが表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "card_refund_dialog.png"
        Title = "交通系ICカード払い戻し確認ダイアログ"
        Instructions = "カード管理画面で「払い戻し」を実行し、残高表示と論理削除警告が含まれた確認ダイアログが表示されたら"
        ForegroundOnly = $true
    },
    # Issue #1416: CSV インポートプレビューは既存 import_preview.png を流用するため新規エントリなし
    # Issue #1417: 管理者マニュアル §6.1/§6.2 バックアップ完了通知・リストア一覧
    @{
        Name = "backup_completed_status.png"
        Title = "手動バックアップ完了ステータス表示"
        Instructions = "F6キーでシステム管理画面を開き「バックアップを作成」をクリック、ステータスバーに「バックアップを作成しました: <ファイル名>」と表示されたら（完了通知ダイアログは現状未実装）"
        ForegroundOnly = $true
    },
    @{
        Name = "restore_list.png"
        Title = "リストア用バックアップ一覧"
        Instructions = "システム管理画面で「リストア」をクリックし、バックアップファイル一覧（ファイル名・タイムスタンプ・選択状態）が表示されたら"
        ForegroundOnly = $true
    },
    @{
        Name = "restore_file_dialog.png"
        Title = "「ファイルを指定してリストア」選択ダイアログ"
        Instructions = "リストア画面で「ファイルを指定してリストア」を選択し、ファイル選択ダイアログが表示されたら"
        ForegroundOnly = $true
    },
    # Issue #1418: 管理者マニュアル §8.4 felicalib.dll ハッシュ検証失敗エラー
    @{
        Name = "felicalib_verification_failed.png"
        Title = "felicalib.dll ハッシュ検証失敗ダイアログ"
        Instructions = "テスト環境で felicalib.dll を意図的に差し替えてアプリを起動し、ハッシュ検証失敗エラー（期待ハッシュと実際のハッシュ表示）が出たら（※起動失敗するため手動撮影）"
        ManualOnly = $true
    }
)

# 画面情報を 1 行に整形（一覧表示用、-List / エラー時の両方で使用）
function Format-ScreenLine {
    param([hashtable]$Screen)
    $shortName = $Screen.Name -replace '\.png$', ''
    $manualMark = if ($Screen.ContainsKey("ManualOnly") -and $Screen.ManualOnly) { " [手動撮影]" } else { "" }
    return ("  {0,-38} {1}{2}" -f $shortName, $Screen.Title, $manualMark)
}

# -List 指定時は撮影可能な画面の一覧を表示して終了
if ($List) {
    Write-Host "撮影可能な画面一覧（-Only に指定する Name）:" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "[必須画面]" -ForegroundColor Green
    foreach ($screen in $requiredScreens) {
        Write-Host (Format-ScreenLine -Screen $screen)
    }
    Write-Host ""
    Write-Host "[オプション画面]" -ForegroundColor Green
    foreach ($screen in $optionalScreens) {
        Write-Host (Format-ScreenLine -Screen $screen)
    }
    Write-Host ""
    Write-Host "使用例:" -ForegroundColor Yellow
    Write-Host "  .\TakeScreenshots.ps1 -Only card_registration_mode" -ForegroundColor Gray
    Write-Host "  .\TakeScreenshots.ps1 -Only card_registration_mode,error_no_reader" -ForegroundColor Gray
    exit 0
}

# 取得する画面リストを決定
if ($All) {
    $screens = $requiredScreens + $optionalScreens
} else {
    $screens = $requiredScreens
}

# -Only 指定時は名前で絞り込み（拡張子省略可、複数指定可）
if ($Only) {
    $allScreens = $requiredScreens + $optionalScreens
    # 比較用に拡張子を除いた小文字の名前へ正規化
    $onlyNormalized = $Only | ForEach-Object { ($_ -replace '\.png$', '').ToLower() }
    $screens = @($allScreens | Where-Object {
        $name = ($_.Name -replace '\.png$', '').ToLower()
        $onlyNormalized -contains $name
    })
    if ($screens.Count -eq 0) {
        Write-Host "指定された画面が見つかりません: $($Only -join ', ')" -ForegroundColor Red
        Write-Host ""
        Write-Host "利用可能な Name 一覧（-List でも確認可能）:" -ForegroundColor Yellow
        foreach ($screen in $allScreens) {
            Write-Host (Format-ScreenLine -Screen $screen)
        }
        exit 1
    }
}

function Take-Screenshot {
    param(
        [string]$OutputPath,
        [bool]$ForegroundOnly = $false,
        [bool]$SkipForegroundActivation = $false
    )

    # ICCardManagerのプロセスを取得
    $process = Get-Process -Name "ICCardManager" -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($null -eq $process) {
        Write-Host "    ! ICCardManagerのプロセスが見つかりません" -ForegroundColor Red
        Write-Host "      ICCardManager.exe が起動しているか確認してください" -ForegroundColor Yellow
        return $false
    }

    $hwnd = $process.MainWindowHandle

    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Host "    ! ICCardManagerのウィンドウハンドルを取得できません" -ForegroundColor Red
        Write-Host "      アプリのウィンドウが最小化されていないか確認してください" -ForegroundColor Yellow
        return $false
    }

    # ウィンドウをフォアグラウンドに移動
    # DelaySeconds 撮影中はユーザーがアプリ側に保持しているフォーカスを
    # 奪ってドロップダウンを閉じてしまわないよう、SetForegroundWindow をスキップする
    if (-not $SkipForegroundActivation) {
        [Win32ApiDpi3]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 500
    }

    if ($ForegroundOnly) {
        # 前面ウィンドウ（ダイアログ等）のみをキャプチャ
        $targetHwnd = [Win32ApiDpi3]::GetTopmostProcessWindow($process.Id)
        if ($targetHwnd -eq [IntPtr]::Zero) {
            Write-Host "    ! 前面ウィンドウが見つかりません" -ForegroundColor Red
            return $false
        }
        $rect = New-Object Win32ApiDpi3+RECT
        if (-not [Win32ApiDpi3]::GetWindowRectDpi($targetHwnd, [ref]$rect)) {
            Write-Host "    ! ウィンドウ領域を取得できません" -ForegroundColor Red
            return $false
        }
    } else {
        # プロセスの全ウィンドウ（メイン + トースト通知等）を含む境界を取得
        $rect = [Win32ApiDpi3]::GetProcessWindowsBounds($process.Id)
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    if ($width -le 0 -or $height -le 0) {
        Write-Host "    ! ウィンドウサイズが不正です" -ForegroundColor Red
        return $false
    }

    try {
        $bitmap = New-Object System.Drawing.Bitmap($width, $height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)

        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

        $graphics.Dispose()
        $bitmap.Dispose()

        return $true
    }
    catch {
        Write-Host "    ! スクリーンショットの保存に失敗しました: $_" -ForegroundColor Red
        return $false
    }
}

function Show-Header {
    Clear-Host
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  ICCardManager スクリーンショット取得ツール" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "出力先: $OutputDir" -ForegroundColor Gray
    Write-Host "取得枚数: $($screens.Count) 枚" -ForegroundColor Gray
    Write-Host ""
    Write-Host "【注意事項】" -ForegroundColor Yellow
    Write-Host "  - ICCardManagerのウィンドウが他のウィンドウで隠れていないことを確認"
    Write-Host "  - 文字サイズは「中（標準）」設定で撮影してください"
    Write-Host "  - 個人情報（実名やIDm）が映らないようにしてください"
    Write-Host "  - ダイアログ画面は ESC で閉じてから次に進んでください"
    Write-Host ""
}

function Start-ScreenshotSession {
    Show-Header

    # アプリの起動確認
    Write-Host "【準備】" -ForegroundColor Green
    Write-Host "  ICCardManager.exe を起動してください。"
    Write-Host "  アプリが起動したら Enter を押してください..."
    Read-Host | Out-Null

    $count = 0
    $total = $screens.Count

    foreach ($screen in $screens) {
        $count++
        $outputPath = Join-Path $OutputDir $screen.Name

        Write-Host ""
        Write-Host "----------------------------------------" -ForegroundColor DarkGray
        Write-Host "[$count/$total] $($screen.Title) ($($screen.Name))" -ForegroundColor Green
        Write-Host ""
        Write-Host "  $($screen.Instructions)" -ForegroundColor White
        Write-Host "  Enter を押してスクリーンショットを取得します..." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  [Enter] 撮影  [S] スキップ  [Q] 終了" -ForegroundColor Gray

        $key = Read-Host

        if ($key -eq "q" -or $key -eq "Q") {
            Write-Host ""
            Write-Host "中断しました。" -ForegroundColor Yellow
            break
        }

        if ($key -eq "s" -or $key -eq "S") {
            Write-Host "    - スキップしました" -ForegroundColor Yellow
            continue
        }

        # ManualOnly = 別プロセス起動や特殊条件が必要なためスクリプトでは撮影できない画面
        $isManualOnly = $screen.ContainsKey("ManualOnly") -and $screen.ManualOnly
        if ($isManualOnly) {
            Write-Host "    ! このスクリーンショットはスクリプトでは撮影できません" -ForegroundColor Yellow
            Write-Host "      上記の手順で画面を表示し、PrtSc 等で手動撮影して" -ForegroundColor Yellow
            Write-Host "      $outputPath として保存してください" -ForegroundColor Yellow
            continue
        }

        # DelaySeconds = ドロップダウン等、PowerShell へのフォーカス移動で閉じる画面向け。
        # カウントダウン中にユーザーがアプリ側で対象を開く運用とし、
        # 撮影時の SetForegroundWindow をスキップしてフォーカスを維持する。
        $delaySeconds = if ($screen.ContainsKey("DelaySeconds")) { [int]$screen.DelaySeconds } else { 0 }
        if ($delaySeconds -gt 0) {
            Write-Host ""
            Write-Host "    $delaySeconds 秒のカウントダウン後に撮影します。" -ForegroundColor Cyan
            Write-Host "    その間にアプリへフォーカスを戻し、対象（ドロップダウン等）を開いてください。" -ForegroundColor Cyan
            for ($i = $delaySeconds; $i -gt 0; $i--) {
                Write-Host "      残り $i 秒..." -ForegroundColor Yellow
                Start-Sleep -Seconds 1
            }
            Write-Host "    撮影します..." -ForegroundColor Green
        }

        $isForegroundOnly = $screen.ContainsKey("ForegroundOnly") -and $screen.ForegroundOnly
        $skipActivation = $delaySeconds -gt 0
        if (Take-Screenshot -OutputPath $outputPath -ForegroundOnly $isForegroundOnly -SkipForegroundActivation $skipActivation) {
            Write-Host "    OK $($screen.Name) を保存しました" -ForegroundColor Green
        }
    }

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  完了" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""

    # 保存されたファイルを表示
    $savedFiles = Get-ChildItem -Path $OutputDir -Filter "*.png" -ErrorAction SilentlyContinue
    if ($savedFiles) {
        Write-Host "保存されたファイル:" -ForegroundColor Green
        foreach ($file in $savedFiles) {
            Write-Host "  - $($file.Name)" -ForegroundColor White
        }
    }

    Write-Host ""
    Write-Host "出力先: $OutputDir" -ForegroundColor Gray
}

# メイン処理
Start-ScreenshotSession
