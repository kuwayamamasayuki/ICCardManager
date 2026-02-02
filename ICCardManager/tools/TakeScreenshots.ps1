<#
.SYNOPSIS
    ICCardManager スクリーンショット取得支援スクリプト

.DESCRIPTION
    マニュアル用のスクリーンショットを対話的に取得します。
    各画面で操作を行い、準備ができたらEnterキーを押すとスクリーンショットを保存します。
    ICCardManagerのウィンドウを自動検索するため、PowerShellウィンドウではなく
    アプリのウィンドウが撮影されます。

.PARAMETER RequiredOnly
    必須画面（6枚）のみを取得します。

.PARAMETER All
    オプション画面も含むすべての画面（10枚）を取得します。

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

# Win32 API定義（シンプル版）
if (-not ([System.Management.Automation.PSTypeName]'Win32Api').Type) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class Win32Api {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
"@
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
        Name = "lend.png"
        Title = "貸出完了画面"
        Instructions = "職員証をタッチし、交通系ICカードをタッチして貸出完了画面が表示されたら"
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
    },
    @{
        Name = "staff.png"
        Title = "職員管理画面"
        Instructions = "F2キーを押して職員管理画面が表示されたら"
    }
)

$optionalScreens = @(
    @{
        Name = "report.png"
        Title = "帳票出力画面"
        Instructions = "F1キーを押して帳票出力画面が表示されたら"
    },
    @{
        Name = "settings.png"
        Title = "設定画面"
        Instructions = "F5キーを押して設定画面が表示されたら"
    },
    @{
        Name = "system.png"
        Title = "システム管理画面"
        Instructions = "F6キーを押してシステム管理画面が表示されたら"
    },
    @{
        Name = "export.png"
        Title = "データ入出力画面"
        Instructions = "F4キーを押してデータ入出力画面が表示されたら"
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
        [string]$OutputPath
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
    [Win32Api]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 500

    # ウィンドウの位置とサイズを取得
    $rect = New-Object Win32Api+RECT
    if (-not [Win32Api]::GetWindowRect($hwnd, [ref]$rect)) {
        Write-Host "    ! ウィンドウの位置を取得できませんでした" -ForegroundColor Red
        return $false
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

        if (Take-Screenshot -OutputPath $outputPath) {
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
