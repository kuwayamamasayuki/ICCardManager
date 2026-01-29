# アプリケーションアイコン生成スクリプト
# ICカードをイメージしたシンプルなアイコンを生成します
# 使用方法: .\generate-icon.ps1

param(
    [string]$OutputPath = "..\src\ICCardManager\Resources\app.ico"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

Write-Host "======================================" -ForegroundColor Cyan
Write-Host " アプリケーションアイコン生成" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$FullOutputPath = Join-Path $ScriptDir $OutputPath

# 出力ディレクトリを作成
$OutputDir = Split-Path -Parent $FullOutputPath
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

Write-Host "[1/2] アイコンを生成中..." -ForegroundColor Yellow

# 複数サイズのアイコンを生成
$sizes = @(16, 32, 48, 256)
$bitmaps = @()

foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # 背景色（交通系ICカードをイメージした青）
    $bgBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 120, 200))
    $graphics.FillRectangle($bgBrush, 0, 0, $size, $size)

    # 角丸の効果（小さいサイズでは省略）
    if ($size -ge 32) {
        $cornerRadius = [int]($size * 0.15)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
        $path.AddArc($rect.X, $rect.Y, $cornerRadius * 2, $cornerRadius * 2, 180, 90)
        $path.AddArc($rect.Right - $cornerRadius * 2, $rect.Y, $cornerRadius * 2, $cornerRadius * 2, 270, 90)
        $path.AddArc($rect.Right - $cornerRadius * 2, $rect.Bottom - $cornerRadius * 2, $cornerRadius * 2, $cornerRadius * 2, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $cornerRadius * 2, $cornerRadius * 2, $cornerRadius * 2, 90, 90)
        $path.CloseFigure()
        $graphics.SetClip($path)
        $graphics.Clear([System.Drawing.Color]::FromArgb(0, 120, 200))
    }

    # ICカードのシンボル（白い四角形＝ICチップ）
    $whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $chipSize = [int]($size * 0.35)
    $chipX = [int]($size * 0.15)
    $chipY = [int]($size * 0.25)
    $graphics.FillRectangle($whiteBrush, $chipX, $chipY, $chipSize, $chipSize)

    # ICチップの回路パターン（細い線）
    if ($size -ge 32) {
        $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0, 120, 200), [Math]::Max(1, $size / 32))
        $lineY = $chipY + [int]($chipSize * 0.3)
        $graphics.DrawLine($linePen, $chipX + 2, $lineY, $chipX + $chipSize - 2, $lineY)
        $lineY = $chipY + [int]($chipSize * 0.5)
        $graphics.DrawLine($linePen, $chipX + 2, $lineY, $chipX + $chipSize - 2, $lineY)
        $lineY = $chipY + [int]($chipSize * 0.7)
        $graphics.DrawLine($linePen, $chipX + 2, $lineY, $chipX + $chipSize - 2, $lineY)
        $linePen.Dispose()
    }

    # 電波マーク（右側に配置）
    $waveX = [int]($size * 0.55)
    $waveY = [int]($size * 0.55)
    $wavePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1, $size / 16))

    if ($size -ge 32) {
        # 複数の弧を描く
        for ($i = 1; $i -le 3; $i++) {
            $arcSize = [int]($size * 0.12 * $i)
            $arcRect = New-Object System.Drawing.Rectangle(($waveX - $arcSize), ($waveY - $arcSize), ($arcSize * 2), ($arcSize * 2))
            $graphics.DrawArc($wavePen, $arcRect, -45, -90)
        }
    } else {
        # 小さいサイズでは簡略化
        $arcSize = [int]($size * 0.2)
        $arcRect = New-Object System.Drawing.Rectangle(($waveX - $arcSize), ($waveY - $arcSize), ($arcSize * 2), ($arcSize * 2))
        $graphics.DrawArc($wavePen, $arcRect, -45, -90)
    }

    $wavePen.Dispose()
    $whiteBrush.Dispose()
    $bgBrush.Dispose()
    $graphics.Dispose()

    $bitmaps += $bitmap
}

Write-Host "[2/2] ICOファイルに変換中..." -ForegroundColor Yellow

# ICOファイルを手動で構築
$iconStream = New-Object System.IO.MemoryStream

# ICONDIRヘッダー
$iconDir = [byte[]]@(0, 0, 1, 0, [byte]$bitmaps.Count, 0)
$iconStream.Write($iconDir, 0, 6)

$imageDataOffset = 6 + (16 * $bitmaps.Count)  # ヘッダー + エントリ
$imageDataList = @()

foreach ($bmp in $bitmaps) {
    $bmpStream = New-Object System.IO.MemoryStream
    $bmp.Save($bmpStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $imageData = $bmpStream.ToArray()
    $bmpStream.Dispose()

    # ICONDIRENTRYエントリ
    $width = if ($bmp.Width -eq 256) { 0 } else { [byte]$bmp.Width }
    $height = if ($bmp.Height -eq 256) { 0 } else { [byte]$bmp.Height }

    $entry = New-Object byte[] 16
    $entry[0] = $width
    $entry[1] = $height
    $entry[2] = 0  # Color palette
    $entry[3] = 0  # Reserved
    [BitConverter]::GetBytes([int16]1).CopyTo($entry, 4)  # Color planes
    [BitConverter]::GetBytes([int16]32).CopyTo($entry, 6)  # Bits per pixel
    [BitConverter]::GetBytes([int32]$imageData.Length).CopyTo($entry, 8)  # Image size
    [BitConverter]::GetBytes([int32]$imageDataOffset).CopyTo($entry, 12)  # Offset

    $iconStream.Write($entry, 0, 16)
    $imageDataOffset += $imageData.Length
    $imageDataList += ,$imageData
}

# 画像データを書き込み
foreach ($imgData in $imageDataList) {
    $iconStream.Write($imgData, 0, $imgData.Length)
}

# ファイルに保存
[System.IO.File]::WriteAllBytes($FullOutputPath, $iconStream.ToArray())
$iconStream.Dispose()

# ビットマップを解放
foreach ($bmp in $bitmaps) {
    $bmp.Dispose()
}

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host " 生成完了!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "出力ファイル: $FullOutputPath" -ForegroundColor Green

# インストーラー用にもコピー
$installerIconPath = Join-Path $ScriptDir "app.ico"
Copy-Item $FullOutputPath $installerIconPath -Force
Write-Host "インストーラー用: $installerIconPath" -ForegroundColor Green
Write-Host ""
