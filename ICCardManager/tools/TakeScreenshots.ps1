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
    オプション画面も含むすべての画面（17枚）を取得します。

.PARAMETER OutputDir
    出力先ディレクトリを指定します。デフォルトは docs/screenshots/ です。

.EXAMPLE
    .\TakeScreenshots.ps1
    必須画面のみを取得します。

.EXAMPLE
    .\TakeScreenshots.ps1 -All
    すべての画面を取得します。

.NOTES
    作成日: 2026-02-02
    Issue: #427, #435
#>

param(
    [switch]$RequiredOnly,
    [switch]$All,
    [string]$OutputDir
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
        Instructions = "履歴照会画面で行をダブルクリックして詳細ダイアログが表示されたら"
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
        Name = "installer_options.png"
        Title = "インストーラーオプション選択画面"
        Instructions = "インストーラーを実行し、オプション選択画面が表示されたら（※手動でPrtScで撮影してください）"
    },
    @{
        Name = "card_registration_mode.png"
        Title = "カード登録方法の選択画面"
        Instructions = "カード管理で新規登録後、カード登録方法の選択ダイアログが表示されたら"
        ForegroundOnly = $true
    }
)

# 取得する画面リストを決定
if ($All) {
    $screens = $requiredScreens + $optionalScreens
} else {
    $screens = $requiredScreens
}

function Take-Screenshot {
    param(
        [string]$OutputPath,
        [bool]$ForegroundOnly = $false
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
    [Win32ApiDpi3]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 500

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

        $isForegroundOnly = $screen.ContainsKey("ForegroundOnly") -and $screen.ForegroundOnly
        if (Take-Screenshot -OutputPath $outputPath -ForegroundOnly $isForegroundOnly) {
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
