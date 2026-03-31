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

## Gotchas
- **PowerShell バージョン**: `pwsh.exe`（7系）を使用。`powershell.exe`（5.1）では構文エラーになる
- **WSL2 パス**: スクリプト呼び出しは `./tools/release.ps1` 形式で。bare path だと Windows 側で解決できない
- **タグ重複**: 失敗リトライ時、タグ `vX.Y.Z` が既に存在する場合は `-SkipTag` で既存タグをスキップ
- **ISCC.exe パス**: `settings.local.json` の許可パスと実際のインストール先が一致していること
- **CHANGELOG.md**: リリーススクリプトが自動生成するため、手動で先に編集すると内容が重複する可能性がある
