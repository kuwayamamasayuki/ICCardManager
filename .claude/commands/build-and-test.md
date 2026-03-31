---
description: "ビルドとテストを実行して結果をサマリ表示する"
user-invocable: true
---

# ビルド＆テスト

以下を順番に実行し、結果をサマリ表示してください。

1. **ビルド**: `"/mnt/c/Program Files/dotnet/dotnet.exe" build` を ICCardManager.sln に対して実行
2. **テスト**: ビルド成功時のみ `"/mnt/c/Program Files/dotnet/dotnet.exe" test --no-build` を実行
3. **結果サマリ**: 以下を報告
   - ビルド: 成功/失敗（警告数があれば記載）
   - テスト: パス数/失敗数/スキップ数
   - 失敗テストがあれば、失敗内容を簡潔に報告
