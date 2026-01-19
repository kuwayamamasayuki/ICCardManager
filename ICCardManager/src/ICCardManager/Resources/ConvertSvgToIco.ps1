<#
.SYNOPSIS
    SVGファイルをICOファイルに変換するPowerShellスクリプト
.DESCRIPTION
    app-icon.svg を複数サイズのPNGに変換し、ICOファイルを生成します。
    Inkscapeがインストールされている必要があります。
.NOTES
    Issue #269: かっこいいアイコンを作成する
#>

param(
    [string]$SvgPath = "$PSScriptRoot\app-icon.svg",
    [string]$OutputPath = "$PSScriptRoot\app.ico"
)

# Inkscapeのパスを検索
$inkscapePaths = @(
    "C:\Program Files\Inkscape\bin\inkscape.exe",
    "C:\Program Files (x86)\Inkscape\bin\inkscape.exe",
    "$env:LOCALAPPDATA\Programs\Inkscape\bin\inkscape.exe"
)

$inkscape = $null
foreach ($path in $inkscapePaths) {
    if (Test-Path $path) {
        $inkscape = $path
        break
    }
}

if (-not $inkscape) {
    # Inkscapeがない場合は、ImageMagickを試す
    $magick = Get-Command magick -ErrorAction SilentlyContinue
    if ($magick) {
        Write-Host "ImageMagickを使用してICOを生成します..."
        & magick convert -background none $SvgPath -define icon:auto-resize=256,48,32,16 $OutputPath
        Write-Host "完了: $OutputPath"
        exit 0
    }

    Write-Error @"
Inkscape または ImageMagick が見つかりません。
以下のいずれかをインストールしてください:
- Inkscape: https://inkscape.org/
- ImageMagick: https://imagemagick.org/

または、オンラインツールを使用してください:
- https://convertio.co/svg-ico/
- https://cloudconvert.com/svg-to-ico
"@
    exit 1
}

Write-Host "Inkscapeを使用してICOを生成します..."
Write-Host "SVG: $SvgPath"
Write-Host "出力: $OutputPath"

# 一時ディレクトリを作成
$tempDir = Join-Path $env:TEMP "ico_convert_$(Get-Random)"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    # 各サイズのPNGを生成
    $sizes = @(16, 32, 48, 256)
    $pngFiles = @()

    foreach ($size in $sizes) {
        $pngPath = Join-Path $tempDir "icon_$size.png"
        Write-Host "  生成中: ${size}x${size}..."

        & $inkscape $SvgPath `
            --export-type=png `
            --export-filename=$pngPath `
            --export-width=$size `
            --export-height=$size `
            --export-background-opacity=0 2>&1 | Out-Null

        if (Test-Path $pngPath) {
            $pngFiles += $pngPath
        } else {
            Write-Warning "  ${size}x${size} の生成に失敗しました"
        }
    }

    if ($pngFiles.Count -eq 0) {
        Write-Error "PNGファイルの生成に失敗しました"
        exit 1
    }

    # ImageMagickがあればそれでICOを作成、なければ手動で作成
    $magick = Get-Command magick -ErrorAction SilentlyContinue
    if ($magick) {
        & magick convert $pngFiles $OutputPath
    } else {
        # .NETを使用してICOを作成
        Add-Type -AssemblyName System.Drawing

        # 最初のPNGからアイコンを作成（簡易版）
        $bitmap = [System.Drawing.Bitmap]::FromFile($pngFiles[0])
        $icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())

        $stream = [System.IO.File]::Create($OutputPath)
        $icon.Save($stream)
        $stream.Close()

        $bitmap.Dispose()
        $icon.Dispose()

        Write-Warning "簡易版ICO（単一サイズ）が作成されました。複数サイズのICOにはImageMagickが必要です。"
    }

    Write-Host ""
    Write-Host "完了: $OutputPath" -ForegroundColor Green

} finally {
    # 一時ファイルを削除
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
