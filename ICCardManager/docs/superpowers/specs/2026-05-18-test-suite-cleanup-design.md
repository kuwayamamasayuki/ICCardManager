# テストスイート整理（不要・重複テストの削除）設計書

**作成日**: 2026-05-18
**対象**: `ICCardManager/tests/ICCardManager.Tests/`（3,267件）・`ICCardManager.UITests/`（26件）
**ブランチ**: `chore/test-cleanup-unnecessary-duplicate-2026-05-18`

## 背景

過去数か月で回帰防止テスト追加を重ね、単体テストが 1,800 件 → 3,267 件まで増加。`.claude/rules/testing.md` の品質基準に反するテスト、重複カバレッジ、Skip 状態で放置されたテストが混入している可能性があり、テスト実行時間とメンテナンスコストの両面で整理が必要。

## ゴール

`.claude/rules/testing.md` の以下原則に違反するテストを発見・削除する：

- 「テストは必ず実際の機能を検証すること」（→ 意味のないアサーション禁止）
- 「テストケース名は何をテストしているか明確に記述すること」（→ テスト名重複は意図不明）
- 「カバレッジだけでなく実際の品質を重視」（→ コードパス重複は品質に寄与しない）

## 非ゴール

- **テスト追加**：本作業は削除専用
- **テスト書き直し**：「不適切だが価値はある」テストは温存し、書き直しは別 Issue
- **CI 仕組みの整備**：自動検出パイプライン化は将来検討（今回は手動スキャン）

## 対象カテゴリ（4種、処理順）

| 順 | カテゴリ | 検出方法 | リスク |
|----|---------|----------|--------|
| 1 | **Skip 属性付き** | `\[Fact(Skip` / `\[Theory(Skip` grep | **最低**：実行されていない |
| 2 | **意味のないアサーション** | `true.Should().BeTrue()` 等の grep + 単一行 Assert | 低 |
| 3 | **テスト名・意図重複** | 名前の共通プレフィックス + 末尾差分が trivial | 中 |
| 4 | **同一コードパス重複** | Act 行が完全一致する同クラス内テスト群 | 高 |

## 処理フロー（カテゴリごとに繰り返し）

```
(1) 静的スキャン (grep/正規表現) → 候補一覧
(2) markdown レポート提示 (file:line + 抜粋 + 削除理由)
(3) ユーザー一括承認 (全件 / 除外指定 / カテゴリスキップ)
(4) git rm / Edit でバッチ削除
(5) dotnet build → dotnet test 全件実行
    - GREEN: コミット作成・次カテゴリへ
    - RED: 削除取り消し (git reset --hard HEAD) → 該当テストを温存対象に追加
(6) 07_テスト設計書.md §1.1a / §8.1 件数同期 + CHANGELOG エントリ追加
```

## 安全機構

- **コミット粒度**: カテゴリごとに 1 コミット。問題発生時 `git revert` で復元可能
- **ビルド検証**: 削除後必ず `dotnet build` 0警告・`dotnet test` GREEN を確認
- **CHANGELOG**: カテゴリ完了ごとに「整理」セクションへエントリ追加
- **テスト設計書同期**: §1.1a（規模）と §8.1（完了基準）の件数を実測値で更新
- **承認の不可逆性回避**: 各カテゴリ承認時に「除外したいテスト」を指定できる窓口を必ず提示

## 期待される効果

- テスト総数の削減（推定 50〜200 件）
- `dotnet test` 実行時間の短縮
- テスト設計書の整合性向上
- 将来の保守者が「何をテストしているか」を読み取りやすくなる

## 関連ドキュメント

- `.claude/rules/testing.md`（品質基準）
- `ICCardManager/docs/design/07_テスト設計書.md`（§1.1a 件数表 / §8.1 必須基準）

## 関連メモリ

- `feedback_test_modification_approval.md`（既存テスト修正は事前承認必須）
- `feedback_test_design_doc.md`（テスト変更時は設計書も同期）
- `feedback_test_count_snapshot_sync.md`（§8.1 件数を `dotnet test --list-tests` 実測値で更新）

---

## 実施結果（2026-05-18）

### スキャン結果サマリ

| Category | 検出数 | 真の削除候補 | 状態 |
|---------|-------|-------------|------|
| 1. Skip 属性付き (`[Fact(Skip=...)]`) | **0** | 0 | 完全にクリーン（過去にも蓄積なし） |
| 2. 意味のないアサーション (`true.Should().BeTrue()` 等) | **0** | 0 | 完全にクリーン |
| 3. テスト名・意図重複 (構造類似 ratio≥0.85) | 475 ペア | **0** | 全て境界値テスト・同値分割テスト・偽陽性 |
| 4. 同一コードパス重複 (同クラス本体完全一致) | 11 グループ | **1** | 10 グループは Theory 化候補（リファクタリング） |

**3,267 件中、真の削除候補は 1 件のみ**。本リポジトリは既に高品質な状態。

### 削除した重複テスト

`tests/ICCardManager.Tests/Data/DbContextConnectionLeaseTests.cs:132-148`

- 削除対象: `LeaseConnection_リエントラント呼び出しがデッドロックしないこと`
- 重複ペア: 同ファイル L362 `LeaseConnection_同期版でリエントラントが動作すること`
- 重複の本質: 両方とも同期 `dbContext.LeaseConnection()` を呼び、State Open + 同一 Connection を検証
- 削除判断: L134 が `#region LeaseConnectionAsync` 内に**配置不整合**で置かれていた一方、L362 は `#region LeaseConnection（同期版）` 内で正しく配置されていたため L134 を削除
- 影響: 単体テスト 3,267 → 3,266 件、`DbContextConnectionLease` 関連は 11 → 10 件、全件 GREEN

### 検出ロジックの限界

スキャン途中で明らかになった偽陽性パターン：

1. **マルチクラス・パー・ファイル**: `IntToVisibilityConverterTests` と `BoolToVisibilityConverterTests` のように 1 ファイル内に複数 class を持つ場合、最初のクラスしか認識しないと別クラス間の同名メソッドを「同クラス内重複」と誤検出する → 修正済み（クラス位置トラッキングを追加）
2. **過剰な正規化**: 数値・文字列リテラルを placeholder 化すると `_WithZero` / `_WithValue` / `_WithLargeValue` のような **入力違いの境界値テスト**が同一視され、真の重複と区別不能になる → 修正済み（リテラルを温存して比較）
3. **ヘルパー隠蔽されたアサーション**: `.Should()` も `Assert.*` も直接呼ばない `AssertIdentifierIsInsideDebugBlock(path, ...)` のようなヘルパー経由検証は静的 grep では「アサーションなし」と誤判定 → 構造的に対処不能（テスト実行時の mutation testing が必要）

### Theory 化候補（別 Issue で対応）

Category 4 スキャンで検出された 10 グループは、**削除不可だが `[Theory] + [InlineData]` への統合でコード行数を削減できる**：

| クラス | グループ | 件数 |
|--------|---------|------|
| `FileSizeConverterTests` | バイト/KB 単位 | 2 |
| `PathValidatorTests` | 無効パスパターン | 2+ |
| `FormulaInjectionSanitizerTests` | Null/Empty 判定 | 2 |
| `SharedModeMonitorTests` | 経過秒数表示 | 2 |
| `StationMasterServiceTests` | 路線×カード種別 | 9 |

これらは別 Issue「refactor: 同構造 Fact テスト群を Theory + InlineData に統合」として起票し、本作業のスコープ外とする。

### 学びとフォローアップ

- **テスト品質はランタイム値で測れない**: 「3,000 件以上」は数の多さで「不要・重複が潜む」と懸念したが、実態は規律ある回帰防止の累積で、削除候補はほぼ存在しない
- **真のテスト整理は構造ではなく意味から**: 静的 grep では意味的重複（同じ意図を別表現で書いた 2 テスト）は検出不可。本格的にやるなら mutation testing が必要
- **将来の運用**: 新規テスト追加時は `.claude/rules/testing.md` の品質基準を遵守し、特に「`true.Should().BeTrue()` のような意味のないアサーションは絶対に書かない」を継続徹底すれば、本作業のような大規模整理は不要
