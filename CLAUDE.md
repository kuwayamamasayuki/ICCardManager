# 交通系ICカード管理システム：ピッすい - 開発ガイド

## プロジェクト概要

複数の交通系ICカード（はやかけん、nimoca、SUGOCA等）を複数職員でシェア利用する際の出納記録を管理するWindowsデスクトップアプリケーション。

## 技術スタック

- **言語**: C# 10 / .NET Framework 4.8 + WPF（MVVM）
- **DB**: SQLite3
- **ICカードリーダー**: PaSoRi（felicalib 経由）
- **帳票出力**: ClosedXML
- **テスト**: xUnit + FluentAssertions + Moq

## ディレクトリ構成

```
ICCardManager/
├── src/ICCardManager/      # 本体（Views, ViewModels, Models, Services, Data, Infrastructure, Common）
├── tests/                  # ユニットテスト・UIテスト
├── tools/                  # 開発支援ツール・スクリプト
├── installer/              # InnoSetupインストーラー
├── docs/design/            # 設計書（01〜08）
└── docs/manual/            # ユーザー・管理者・開発者マニュアル
```

## よく使うコマンド

> **注**: WSL2環境では `dotnet` の代わりに `"/mnt/c/Program Files/dotnet/dotnet.exe"` を使用すること。

```bash
dotnet build                                # ビルド
dotnet test                                 # テスト実行
dotnet run --project src/ICCardManager      # 実行
dotnet publish -c Release                   # リリースビルド
```

## 最重要ルール

<important if="editing UI text, dialogs, or user-facing strings">
交通系ICカードを指す場合は必ず「交通系ICカード」と記載し、単に「ICカード」とは書かないこと。
「ICカード」だけでは職員証と区別がつかずユーザーが混乱する。ただし「ICカードリーダー」等のハードウェア名称はそのまま。
</important>

<important if="modifying deletion logic, cleanup, or database maintenance">
論理削除の方針はテーブルごとに異なる（staff/ic_card=論理削除、ledger/operation_log=6年後物理削除）。
詳細は .claude/rules/development-conventions.md を参照。
</important>

<important if="running dotnet, build, or test commands in WSL2">
WSL2では "/mnt/c/Program Files/dotnet/dotnet.exe" を使用すること。
</important>

## 詳細ルール（`.claude/rules/` に一元化）

| ファイル | 内容 |
|---------|------|
| `development-conventions.md` | 環境制約、DB設計原則、UI/UX原則、ICカード関連、論理削除の方針 |
| `business-logic.md` | 状態遷移、貸出/返却フロー、バス判別、摘要生成、共有モード、残高不足処理、月次帳票 |
| `git-workflow.md` | ブランチルール、ステージング規約 |
| `testing.md` | テスト品質、ハードコーディング禁止、テスト実装原則 |
| `error-messages.md` | エラーメッセージ品質（「何が/なぜ/どうすれば」3要素、禁止パターン、Issue #1275） |

## 参照ドキュメント

- `ICCardManager/CHANGELOG.md` — **バージョン履歴・変更内容の Single Source of Truth**（TODO.md より優先）
- `docs/design/` — 設計書一式（01〜08）
- `docs/manual/` — マニュアル（ユーザー・管理者・開発者）
- `Resources/Templates/物品出納簿テンプレート.xlsx` — 月次帳票テンプレート
- `docs/線区駅順コード/StationCode.csv` — 駅コード→駅名マスター（[出典](https://produ.irelang.jp/blog/2017/08/305/)、[新駅参照](https://ja.ysrl.org/atc/station-code.html)）

## 非推奨ドキュメント

- `TODO.md`（リポジトリ直下） — v2.2.0 時点の初期実装タスクアーカイブ。**deprecated**（Issue #1249）。以降の進捗・要望管理は GitHub Issues と `CHANGELOG.md` を使用する。新規作業の優先度判断には使用しないこと。
