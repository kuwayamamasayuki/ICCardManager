# マニュアル変換の対象一覧（Issue #2241）
#
# convert-to-docx.ps1 と convert-to-pdf.ps1 が dot-source して使う。
# 各マニュアルの .md / .docx / .pdf のファイル名は Name から導出するので、
# 「docx 側が書き出すファイル」と「PDF 側が読み込むファイル」は必ず一致する。
#
# 一覧を 2 つのスクリプトにそれぞれ持っていた頃は、概要版の docx を自動生成へ切り替えた際（Issue #1489）に
# PDF 側の入力だけが手作業時代のファイル名（「ユーザーマニュアル概要版（修正版）.docx」）のまま残り、
# 概要版の PDF が作られないまま古い PDF がインストーラーに同梱されていた。
#
# マニュアルを追加・削除するときは、ここと各スクリプトの -Target の ValidateSet
# （PowerShell の制約で定数しか書けない）を併せて直す。両者の一致は ManualConversionScriptTests が検査する。
# 本ファイルは日本語を含むため BOM 付き UTF-8 で保存する（Windows PowerShell 5.1 は BOM の無い .ps1 を
# システムの既定コードページで読み、構文解析に失敗する）。

function Get-ManualTargets {
    $Definitions = @(
        @{
            Key = "intro"
            Name = "はじめに"
            Title = "交通系ICカード管理システム：ピッすい はじめに"
            VersionTracked = $false  # 固定バージョン（1.0）
        },
        @{
            Key = "user"
            Name = "ユーザーマニュアル"
            Title = "交通系ICカード管理システム：ピッすい ユーザーマニュアル"
            VersionTracked = $true   # アプリバージョンに追従
        },
        @{
            Key = "user-summary"
            Name = "ユーザーマニュアル概要版"
            Title = "交通系ICカード管理システム：ピッすい 操作ガイド（概要版）"
            VersionTracked = $false  # Markdown にバージョン行がないため注入不要
            ReferenceDoc = "reference-summary.docx"  # 概要版専用（縦向き・ヘッダーフッターなし）
            TableCellVAlign = $true  # テーブルセルの上下中央揃え（後処理）
        },
        @{
            Key = "admin"
            Name = "管理者マニュアル"
            Title = "交通系ICカード管理システム：ピッすい 管理者マニュアル"
            VersionTracked = $true   # アプリバージョンに追従
        },
        @{
            Key = "quickstart"
            Name = "かんたん導入ガイド"
            Title = "交通系ICカード管理システム：ピッすい かんたん導入ガイド"
            VersionTracked = $true   # アプリバージョンに追従（管理者マニュアルから「別紙」として案内される 2 ページの手順書）
            ReferenceDoc = "reference-summary.docx"  # 概要版と同じ縦向き・ヘッダーフッターなし
            TableCellVAlign = $true
        },
        @{
            Key = "it"
            Name = "IT担当者ガイド"
            Title = "交通系ICカード管理システム：ピッすい IT担当者ガイド"
            VersionTracked = $true   # アプリバージョンに追従
        },
        @{
            Key = "dev"
            Name = "開発者ガイド"
            Title = "交通系ICカード管理システム：ピッすい 開発者ガイド"
            VersionTracked = $false  # 独自バージョン体系（1.1）
        }
    )

    foreach ($Definition in $Definitions) {
        [PSCustomObject]@{
            Key = $Definition.Key
            Name = $Definition.Name
            Markdown = "$($Definition.Name).md"
            Docx = "$($Definition.Name).docx"
            Pdf = "$($Definition.Name).pdf"
            Title = $Definition.Title
            VersionTracked = [bool]$Definition.VersionTracked
            ReferenceDoc = $Definition.ReferenceDoc
            TableCellVAlign = [bool]$Definition.TableCellVAlign
        }
    }
}
