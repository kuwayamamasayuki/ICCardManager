---
description: "現在のブランチがPR作成可能な状態か確認する"
user-invocable: true
---

# PR Ready チェック

現在のブランチについて以下をチェックし、結果を一覧で報告してください。

1. **ブランチ名**: `fix/issue-XXX-*`、`feat/issue-XXX-*`、`docs/issue-XXX-*`、`chore/*` のいずれかに合致するか
2. **mainとの差分**: `git log main..HEAD --oneline` でコミット一覧を確認
3. **未コミットの変更**: `git status` で未ステージ・未追跡ファイルがないか
4. **ビルド**: `"/mnt/c/Program Files/dotnet/dotnet.exe" build` が成功するか
5. **テスト**: `"/mnt/c/Program Files/dotnet/dotnet.exe" test --no-build` が全パスするか
6. **結果サマリ**: 各項目を OK/NG で一覧表示し、NGがあれば対処方法を提案
