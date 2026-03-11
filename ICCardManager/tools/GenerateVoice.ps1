<#
.SYNOPSIS
    VOICEVOX を使って貸出・返却の音声WAVファイルを生成するスクリプト

.DESCRIPTION
    VOICEVOX Engine の REST API (localhost:50021) を使用して、
    貸出時「いってらっしゃい！」と返却時「おかえりなさい！」の
    男性・女性音声WAVファイルを生成します。

    事前に VOICEVOX を起動しておく必要があります。
    https://voicevox.hiroshiba.jp/

.PARAMETER FemaleSpeakerId
    女性ボイスのスピーカーID（デフォルト: 2 = 四国めたん ノーマル）

.PARAMETER MaleSpeakerId
    男性ボイスのスピーカーID（デフォルト: 11 = 玄野武宏 ノーマル）

.PARAMETER Port
    VOICEVOX Engine のポート番号（デフォルト: 50021）

.PARAMETER OutputDir
    WAV ファイルの出力先ディレクトリ
    デフォルト: src/ICCardManager/Resources/Sounds/

.PARAMETER SpeedScale
    話速（デフォルト: 1.15 … やや速めで元気な印象に）
    1.0 = 標準速度

.PARAMETER PitchScale
    ピッチ（デフォルト: 0.06 … やや高めで明るい印象に）
    0.0 = 標準ピッチ、正の値で高く、負の値で低く

.PARAMETER IntonationScale
    抑揚（デフォルト: 1.6 … 大きく抑揚をつけて元気よく）
    1.0 = 標準抑揚、大きいほど表情豊か

.PARAMETER VolumeScale
    音量（デフォルト: 1.0 … 標準音量）
    1.0 = 標準音量

.PARAMETER PrePhonemeLength
    音声前の無音時間（秒）（デフォルト: 0.0 … 無音なしで即座に発声）
    VOICEVOXのデフォルトは 0.15

.PARAMETER PostPhonemeLength
    音声後の無音時間（秒）（デフォルト: 0.0 … 無音なしで即座に終了）
    VOICEVOXのデフォルトは 0.15

.PARAMETER ListSpeakers
    利用可能なスピーカー一覧を表示して終了します

.EXAMPLE
    .\GenerateVoice.ps1
    デフォルト設定（元気よい声）で4つのWAVファイルを生成します。

.EXAMPLE
    .\GenerateVoice.ps1 -ListSpeakers
    利用可能なスピーカー一覧を表示します。

.EXAMPLE
    .\GenerateVoice.ps1 -FemaleSpeakerId 2 -MaleSpeakerId 11
    スピーカーIDを指定して生成します。

.EXAMPLE
    .\GenerateVoice.ps1 -SpeedScale 1.0 -PitchScale 0.0 -IntonationScale 1.0
    すべてのパラメータを標準値にリセットして生成します。

.NOTES
    作成日: 2026-02-23
    前提: VOICEVOX が起動していること（REST API が localhost:50021 で応答すること）

    VOICEVOX クレジット表記が必要です。
    例: 「VOICEVOX:四国めたん」「VOICEVOX:玄野武宏」
    詳細: https://voicevox.hiroshiba.jp/term/
#>

param(
    [int]$FemaleSpeakerId = 2,
    [int]$MaleSpeakerId = 11,
    [int]$Port = 50021,
    [string]$OutputDir = "",
    [double]$SpeedScale = 1.15,
    [double]$PitchScale = 0.06,
    [double]$IntonationScale = 1.6,
    [double]$VolumeScale = 1.0,
    [double]$PrePhonemeLength = 0.0,
    [double]$PostPhonemeLength = 0.0,
    [switch]$ListSpeakers
)

$ErrorActionPreference = "Stop"
$BaseUrl = "http://localhost:$Port"

# デフォルト出力先の設定
if (-not $OutputDir) {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputDir = Join-Path (Split-Path -Parent $ScriptDir) "src\ICCardManager\Resources\Sounds"
}

function Test-VoicevoxConnection {
    try {
        $response = Invoke-RestMethod -Uri "$BaseUrl/version" -Method Get -TimeoutSec 5
        Write-Host "VOICEVOX Engine v$response に接続しました" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "エラー: VOICEVOX Engine に接続できません (localhost:$Port)" -ForegroundColor Red
        Write-Host ""
        Write-Host "以下を確認してください:" -ForegroundColor Yellow
        Write-Host "  1. VOICEVOX がインストールされていること"
        Write-Host "     ダウンロード: https://voicevox.hiroshiba.jp/"
        Write-Host "  2. VOICEVOX が起動していること"
        Write-Host "  3. ポート $Port で REST API が応答すること"
        Write-Host ""
        return $false
    }
}

function Show-Speakers {
    Write-Host "利用可能なスピーカー一覧:" -ForegroundColor Cyan
    Write-Host ""

    try {
        $speakers = Invoke-RestMethod -Uri "$BaseUrl/speakers" -Method Get
        foreach ($speaker in $speakers) {
            Write-Host "  $($speaker.name)" -ForegroundColor White
            foreach ($style in $speaker.styles) {
                Write-Host "    ID: $($style.id) - $($style.name)" -ForegroundColor Gray
            }
        }
    }
    catch {
        Write-Host "スピーカー一覧の取得に失敗しました: $_" -ForegroundColor Red
    }
}

function Get-SpeakerName {
    param([int]$SpeakerId)

    try {
        $speakers = Invoke-RestMethod -Uri "$BaseUrl/speakers" -Method Get
        foreach ($speaker in $speakers) {
            foreach ($style in $speaker.styles) {
                if ($style.id -eq $SpeakerId) {
                    return $speaker.name
                }
            }
        }
    }
    catch {
        # 取得失敗時は空文字を返す
    }
    return "不明 (ID: $SpeakerId)"
}

function New-VoiceWav {
    param(
        [string]$Text,
        [int]$SpeakerId,
        [string]$OutputPath
    )

    $fileName = Split-Path -Leaf $OutputPath

    # 1. 音声合成用のクエリを作成
    Write-Host "  [$fileName] クエリ生成中..." -NoNewline
    $encodedText = [System.Uri]::EscapeDataString($Text)
    $queryUrl = "$BaseUrl/audio_query?text=$encodedText&speaker=$SpeakerId"
    $audioQuery = Invoke-RestMethod -Uri $queryUrl -Method Post -ContentType "application/json"
    Write-Host " OK" -ForegroundColor Green

    # 2. 音声パラメータを調整（元気よい声に）
    Write-Host "  [$fileName] パラメータ調整中..." -NoNewline
    $audioQuery.speedScale = $SpeedScale
    $audioQuery.pitchScale = $PitchScale
    $audioQuery.intonationScale = $IntonationScale
    $audioQuery.volumeScale = $VolumeScale
    $audioQuery.prePhonemeLength = $PrePhonemeLength
    $audioQuery.postPhonemeLength = $PostPhonemeLength
    Write-Host " OK (速度:$SpeedScale ピッチ:$PitchScale 抑揚:$IntonationScale 音量:$VolumeScale)" -ForegroundColor Green

    # 3. 音声合成を実行してWAVデータを取得
    Write-Host "  [$fileName] 音声合成中..." -NoNewline
    $synthesisUrl = "$BaseUrl/synthesis?speaker=$SpeakerId"
    $queryJson = $audioQuery | ConvertTo-Json -Depth 10
    $wavData = Invoke-WebRequest -Uri $synthesisUrl -Method Post -ContentType "application/json" -Body $queryJson
    Write-Host " OK" -ForegroundColor Green

    # 4. WAVファイルとして保存
    [System.IO.File]::WriteAllBytes($OutputPath, $wavData.Content)
    $fileSize = (Get-Item $OutputPath).Length
    Write-Host "  [$fileName] 保存完了 ($fileSize bytes)" -ForegroundColor Green
}

# =============================================
# メイン処理
# =============================================

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  VOICEVOX 音声WAVファイル生成スクリプト" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# VOICEVOX 接続チェック
if (-not (Test-VoicevoxConnection)) {
    exit 1
}

# スピーカー一覧モード
if ($ListSpeakers) {
    Write-Host ""
    Show-Speakers
    exit 0
}

# 出力先の確認
if (-not (Test-Path $OutputDir)) {
    Write-Host "エラー: 出力先ディレクトリが存在しません: $OutputDir" -ForegroundColor Red
    exit 1
}

# スピーカー名を取得
$femaleName = Get-SpeakerName -SpeakerId $FemaleSpeakerId
$maleName = Get-SpeakerName -SpeakerId $MaleSpeakerId

Write-Host "設定:" -ForegroundColor Yellow
Write-Host "  女性ボイス: $femaleName (ID: $FemaleSpeakerId)"
Write-Host "  男性ボイス: $maleName (ID: $MaleSpeakerId)"
Write-Host "  出力先:     $OutputDir"
Write-Host ""
Write-Host "音声パラメータ:" -ForegroundColor Yellow
Write-Host "  話速:       $SpeedScale (標準: 1.0)"
Write-Host "  ピッチ:     $PitchScale (標準: 0.0)"
Write-Host "  抑揚:       $IntonationScale (標準: 1.0)"
Write-Host "  音量:       $VolumeScale (標準: 1.0)"
Write-Host "  前無音:     $PrePhonemeLength 秒 (VOICEVOX既定: 0.15)"
Write-Host "  後無音:     $PostPhonemeLength 秒 (VOICEVOX既定: 0.15)"
Write-Host ""

# 生成する音声の定義
$voiceFiles = @(
    @{ Text = "いってらっしゃい！"; SpeakerId = $FemaleSpeakerId; FileName = "lend_female.wav"; Label = "女性" },
    @{ Text = "おかえりなさい！";   SpeakerId = $FemaleSpeakerId; FileName = "return_female.wav"; Label = "女性" },
    @{ Text = "いってらっしゃい！"; SpeakerId = $MaleSpeakerId;   FileName = "lend_male.wav"; Label = "男性" },
    @{ Text = "おかえりなさい！";   SpeakerId = $MaleSpeakerId;   FileName = "return_male.wav"; Label = "男性" }
)

# 確認
Write-Host "以下の4ファイルを生成します:" -ForegroundColor Yellow
foreach ($vf in $voiceFiles) {
    Write-Host "  $($vf.FileName) - $($vf.Label)「$($vf.Text)」"
}
Write-Host ""
$confirm = Read-Host "続行しますか？ (Y/n)"
if ($confirm -eq "n" -or $confirm -eq "N") {
    Write-Host "キャンセルしました。" -ForegroundColor Yellow
    exit 0
}

# WAVファイル生成
Write-Host ""
Write-Host "音声ファイルを生成しています..." -ForegroundColor Cyan
Write-Host ""

$successCount = 0
foreach ($vf in $voiceFiles) {
    $outputPath = Join-Path $OutputDir $vf.FileName
    try {
        New-VoiceWav -Text $vf.Text -SpeakerId $vf.SpeakerId -OutputPath $outputPath
        $successCount++
    }
    catch {
        Write-Host "  [$($vf.FileName)] 生成失敗: $_" -ForegroundColor Red
    }
    Write-Host ""
}

# 結果表示
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  完了: $successCount / $($voiceFiles.Count) ファイル生成" -ForegroundColor $(if ($successCount -eq $voiceFiles.Count) { "Green" } else { "Yellow" })
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

if ($successCount -eq $voiceFiles.Count) {
    Write-Host "クレジット表記（VOICEVOX利用規約に基づき必須）:" -ForegroundColor Yellow
    Write-Host "  VOICEVOX:$femaleName / VOICEVOX:$maleName" -ForegroundColor White
    Write-Host ""
    Write-Host "SettingsDialog.xaml のクレジット表記を上記に合わせて更新してください。" -ForegroundColor Yellow
}
