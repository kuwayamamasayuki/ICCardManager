---
description: "ICCardManagerのリリース手順を実行する。バージョンバンプ、ビルド、インストーラー作成、GitHub Release公開を含む。使い方: /release X.Y.Z"
user-invocable: true
---

# リリース手順

## 引数
- `/release X.Y.Z` のようにバージョン番号が渡された場合、そのバージョンで手順を実行する
- バージョン番号が省略された場合、ユーザーに確認してから実行する

## 前提条件
- リリース対象のPRがすべて `main` にマージ済み
- 作業ディレクトリ: `/mnt/d/OneDrive/交通系/src/ICCardManager`
- **PowerShell 7 (`pwsh.exe`) を使用すること**（`powershell.exe` は5.1なので不可）
- WSL2から呼ぶ際、`-File` の引数は必ず `./` 付きで指定すること（例: `./tools/release.ps1`）

## リリース前のセキュリティチェック（Issue #1272）

Phase 1 を開始する前に、依存パッケージの既知 CVE を必ず確認する。

```bash
# 作業ディレクトリ: ICCardManager
"/mnt/c/Program Files/dotnet/dotnet.exe" restore
"/mnt/c/Program Files/dotnet/dotnet.exe" list package --vulnerable --include-transitive
```

### 判定基準
| 結果 | 対応 |
|------|------|
| `No vulnerable packages` | リリース続行 |
| Critical / High 検出 | **リリース保留**。修正版にアップグレードしてから再開（[開発者ガイド §5.7](../../ICCardManager/docs/manual/開発者ガイド.md) 参照） |
| Moderate / Low 検出 | リリースノートに記載、計画的対応 |

CVE スキャンの詳細プロセスは `docs/manual/開発者ガイド.md` §5.7 を参照。

## 自動リリース（推奨）

### 1コマンドリリース
```bash
pwsh.exe -File ./tools/release.ps1 -Version X.Y.Z
```
- Phase 1: バージョンバンプ → PR作成 → CIチェック待ち → squashマージ → main最新化
- Phase 2: タグ → インストーラービルド → GitHub Release更新（リリースノート+exe）
- `-DryRun` でCHANGELOG生成と差分プレビューのみ
- `-Force` でタグ作成時の確認プロンプトをスキップ

### 個別実行（リカバリ時）
```bash
# Phase 1 だけ（-NoMerge で手動マージにもできる）
pwsh.exe -File ./tools/bump-version.ps1 -NewVersion X.Y.Z

# Phase 2 だけ（-SkipTag / -SkipBuild で途中再開可能）
pwsh.exe -File ./tools/publish-release.ps1 -Version X.Y.Z [-SkipTag] [-SkipBuild]
```

### リカバリ例
```bash
# タグは済み、ビルドからやり直し
pwsh.exe -File ./tools/publish-release.ps1 -Version X.Y.Z -SkipTag

# ビルドも済み、リリースアップロードだけ
pwsh.exe -File ./tools/publish-release.ps1 -Version X.Y.Z -SkipTag -SkipBuild
```

## 手動リリース（参考）

### 1. Version Bump（ブランチ上で実施、mainへの直接コミット禁止）
```bash
git checkout main && git pull
git checkout -b chore/bump-version-X.Y.Z
```

更新対象ファイル:
- `src/ICCardManager/ICCardManager.csproj` — `<Version>`, `<FileVersion>`, `<AssemblyVersion>`
- `README.md` — 最新バージョン行
- `CHANGELOG.md` — 新セクション追加
- `docs/manual/ユーザーマニュアル.md` — Version string
- `docs/manual/管理者マニュアル.md` — Version string
- `docs/manual/開発者ガイド.md` — **構造変更が含まれる場合**は §2.5「アーキテクチャの発展」を更新（Issue #1472 対策、下記）

コミット → プッシュ → PR作成 → マージ。

### 2. Tag & Build
```bash
git checkout main && git pull
git tag vX.Y.Z
git push origin vX.Y.Z
```

インストーラービルド:
```bash
pwsh.exe -ExecutionPolicy Bypass -File ./installer/build-installer.ps1 -Version X.Y.Z
```

### 3. GitHub Release
```bash
gh release edit vX.Y.Z --notes "Release notes from CHANGELOG"
gh release upload vX.Y.Z "installer/output/ICCardManager_Setup_X.Y.Z.exe" --clobber
```

## 補足
- `build-installer.ps1` はクリーンビルド、dotnet publish、マニュアル変換(-NoMermaid)、Inno Setupを一括実行
- `release.yml` GitHub Action は `v*` タグpushでRelease + ZIPを自動作成
- 順序: Version bump PR → マージ → Tag → Build → GitHub Release

## 開発者ガイド §2.5「アーキテクチャの発展」の更新（Issue #1472 対策）

過去、リリース毎に追加された構造変更が `docs/manual/開発者ガイド.md` §2.5 に反映されず、章タイトルだけが古いバージョン上限で残る不具合があった（v2.7.0 までしか書かれていない状態で v2.8.0 がリリースされ、Issue #1472 で発覚）。再発防止のため、リリース時に以下を必ず確認する。

### チェック手順

1. 今回のリリースに **構造変更** が含まれるかを判定する。「構造変更」とは以下のいずれか:
   - クラス・サービスの分割／統合（例: `LendingService` のヘルパー抽出 #1283、`CsvImportService` の再分割 #1284）
   - 新しい横断規約の導入（例: `ConfigureAwait(false)` 規約 #1287）
   - 新しいヘルパー基盤の追加（例: `MigrationHelpers` #1285）
   - 新規 Value Object・インターフェース階層の追加
   - **バグ修正・UI 微調整・テスト追加のみであれば §2.5 更新は不要**

2. 構造変更が含まれる場合、`docs/manual/開発者ガイド.md` の以下 2 箇所を更新する:
   - **§2.2 末尾の「refactor 履歴」blockquote**: 上限バージョンを今回のリリース版に伸長し、代表的な Issue 番号を 1〜2 件追記
   - **§2.5 章タイトル**: 「アーキテクチャの発展（v2.5.0〜vX.Y.Z）」の上限バージョンを更新し、本文導入も同様に伸長
   - **§2.5 配下のサブセクション**: 構造変更ごとに `#### 2.5.N <タイトル>（#Issue、vX.Y.Z）` を追加

3. 文書を `bump-version.ps1` の `## 更新対象ファイル一覧` （手動リリースなら本 SKILL §1）に従って同一バージョン PR でコミットする。

### 文面の参考スタイル

```markdown
#### 2.5.N <短い見出し>（#1234、vX.Y.Z）

<1〜2 文の概要>。

| <表 or 箇条書きで詳細> |
```

末尾に「`public` API は不変」「既存テスト全件 pass」「新規テスト N 件追加」のいずれかを明記すると、読者がリスク評価しやすい。

## Gotchas
- **PowerShell バージョン**: `pwsh.exe`（7系）を使用。`powershell.exe`（5.1）では構文エラーになる
- **WSL2 パス**: スクリプト呼び出しは `./tools/release.ps1` 形式で。bare path だと Windows 側で解決できない
- **タグ重複**: 失敗リトライ時、タグ `vX.Y.Z` が既に存在する場合は `-SkipTag` で既存タグをスキップ
- **ISCC.exe パス**: `settings.local.json` の許可パスと実際のインストール先が一致していること
- **CHANGELOG.md の `### Unreleased`**: `bump-version.ps1` は既存 `### Unreleased` セクションを検出すると、見出しを `### vX.Y.Z (date)` にリネームし、Unreleased 本文（手動キュレーションされたエントリ）を保持したまま、コミットメッセージから自動生成したエントリを末尾に追記する。**重複エントリが発生する場合があるため、PR レビュー時に手動で整理すること**。Unreleased が存在しない場合は「# 更新履歴」直後に新規セクションを挿入する従来挙動。
  - 過去の不具合: v2.8.0 / v2.8.1 リリース時に既存 Unreleased が孤児化し、新バージョンと前バージョンの間に挟まる構造になっていた（PR #1451, #1511 で事後修正）。本ロジック追加により自動統合される。

## CHANGELOG.md `### Unreleased` 運用ルール

リリース後に追加する PR が CHANGELOG エントリを記載する場合、必ず **`# 更新履歴` 直下** に `### Unreleased` セクションを作成（または既存セクションに追記）すること。
最新リリース版セクション（`### vX.Y.Z`）の**下**に `### Unreleased` を置くと、構造が壊れる（次回 `bump-version.ps1` の自動検出にもヒットしない）。

```markdown
# 更新履歴

### Unreleased            ← 必ずここ（先頭）
**バグ修正**
- ...

### v2.8.1 (2026-05-11)   ← 既存リリース版はこの下
...
```
