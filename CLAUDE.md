# 交通系ICカード管理システム：ピッすい - 開発ガイド

## プロジェクト概要

複数の交通系ICカード（はやかけん、nimoca、SUGOCA等）を複数職員でシェア利用する際の出納記録を管理するWindowsデスクトップアプリケーション。

## 技術スタック

- **言語**: C# 10 / .NET Framework 4.8 + WPF（MVVM） — .NET Core / .NET 5+ ではない
- **ICカードリーダー**: PaSoRi（felicalib 経由。NuGet ではなくネイティブ DLL 依存）

## ディレクトリ構成の注意点

ツリー構成は `ls` で確認できるが、**同名ディレクトリが2組あり混同しやすい**ので注意:

- `docs/`（ルート直下） = superpowers ワークフロー成果物（プラグインキャッシュ・plans）。**本プロジェクトの設計書は `ICCardManager/docs/` 配下**。設計 spec は `ICCardManager/docs/superpowers/specs/` に集約済み
- `tools/`（ルート直下） = 補助スクリプト群。`ICCardManager/tools/`（開発支援ツール）とは別物

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

「読込」列が **常時** のファイルは毎セッション自動でロードされる。**条件付き** のファイルは frontmatter の `paths:` に一致するファイルを扱うときだけロードされるため、**該当作業に入る前に自分で読むこと**。

| ファイル | 読込 | 内容 |
|---------|------|------|
| `development-conventions.md` | 常時 | 環境制約、DB設計原則、UI/UX原則、ICカード関連、論理削除の方針 |
| `business-logic.md` | 常時 | 状態遷移、貸出/返却フロー、バス判別、摘要生成、共有モード、残高不足処理、月次帳票 |
| `git-workflow.md` | 常時 | ブランチルール、ステージング規約 |
| `error-messages.md` | 常時 | エラーメッセージ品質（「何が/なぜ/どうすれば」3要素、禁止パターン、Issue #1275） |
| `testing.md` | 条件付き（`tests/**`、07_テスト設計書） | テスト品質、ハードコーディング禁止、テスト実装原則 |
| `async-configureawait.md` | 条件付き（`Services/**`、`Data/**`、`Infrastructure/**`、`Common/**`） | async/ConfigureAwait(false) 規約（Service 層のみ付与、ViewModels/Views/tests は付けない、CA2007、Issue #1287） |
| `migrations.md` | 条件付き（`Data/Migrations/**`、02_DB設計書） | マイグレーション作成規約（冪等性必須、AddColumnIfNotExists 引数検証、新規マイグレーション追加手順） |
| `domain-boundaries.md` | 条件付き（`Services/**`、`Models/**`、`Infrastructure/**`、05_クラス設計書） | 交通系固有ロジックの境界（3リング、新しいロジックの置き場所の決定木、SummaryGenerator の汎用/固有の同居、Issue #1695） |

## 参照ドキュメント

- `ICCardManager/CHANGELOG.md` — **バージョン履歴・変更内容の Single Source of Truth**
- `ICCardManager/docs/design/` — 設計書一式（用語集 00・00a と 01〜08）
- `ICCardManager/docs/manual/` — マニュアル（ユーザー・管理者・開発者）
- `ICCardManager/src/ICCardManager/Resources/Templates/` — 月次帳票テンプレート（`物品出納簿テンプレート（企業会計部局）.xlsx`、`物品出納簿テンプレート（市長事務部局）.xlsx` の 2 ファイル）
- `ICCardManager/docs/線区駅順コード/StationCode.csv` — 駅コード→駅名マスター（[出典](https://produ.irelang.jp/blog/2017/08/305/)、[新駅参照](https://ja.ysrl.org/atc/station-code.html)）
- `BUG-AUDIT-2026-08-07.md` / `BUG-AUDIT-2026-08-07.data.json`（ルート直下） — リポジトリ全体バグ監査の報告書と生データ。Issue #1723〜#1750 の出典。未検証 54 件の精査結果は `ICCardManager/docs/superpowers/specs/2026-08-14-issue-1750-audit-triage.md`
