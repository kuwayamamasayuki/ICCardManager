# Issue #1466: MigrationHelpers の引数検証強化（設計書）

- 日付: 2026-05-22
- 関連 Issue: #1466
- 関連ルール: `.claude/rules/migrations.md`
- 影響範囲: `ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs`

## 1. 背景

`MigrationHelpers.AddColumnIfNotExists` および `HasColumn` は、SQL の識別子部分をパラメータ化できないため文字列補間で構成している。現状の防御は `table` のみで、`column` と `typeAndConstraints` は無検証のまま SQL に補間される。

```csharp
// 現状（MigrationHelpers.cs:73）
command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeAndConstraints}";
```

引数はすべて開発者制御で外部入力ではないため、攻撃面は実質ゼロである。しかし、新規 Migration を書く開発者が誤って外部入力相当の値を渡した場合の保険として、ヘルパー側で識別子・型句のホワイトリスト検証を追加する。

## 2. 目的

1. `column` を識別子の正規表現で検証する（`HasColumn` / `AddColumnIfNotExists` の両方）。
2. `typeAndConstraints` を型・制約のホワイトリスト regex で検証する（`AddColumnIfNotExists`）。
3. 既存呼び出し 7 callsite および既存テスト 7 件は無修正で動作する。
4. 不正値を渡した場合は `ArgumentException` を即座に投げ、SQL 実行に到達させない。

## 3. 方針

### 3.1 シグネチャ不変・regex 追加方式

既存呼び出しの実値はすべてシンプルなパターン（`"INTEGER"` / `"INTEGER DEFAULT 0"` / `"INTEGER DEFAULT 1"` / `"TEXT"`）に収まるため、列挙体ベースのシグネチャ変更は不要。`Regex` での弾き出しで十分な防御層になる。

採用する検証:

| 引数 | 検証方法 | 受理例 | 拒否例 |
|---|---|---|---|
| `column` | `^[A-Za-z_][A-Za-z0-9_]*$` | `is_point_redemption`, `_temp`, `col1` | `x; DROP`, `1col`, `col-name`, `col name`, 空文字 |
| `typeAndConstraints` | `^(INTEGER\|TEXT\|REAL\|BLOB\|NUMERIC)(\s+(NOT\s+NULL\|DEFAULT\s+(-?\d+(\.\d+)?\|'[^';]*'\|NULL)\|REFERENCES\s+[A-Za-z_][A-Za-z0-9_]*\([A-Za-z_][A-Za-z0-9_]*\)))*\s*$` | `INTEGER`, `INTEGER DEFAULT 0`, `TEXT`, `REAL NOT NULL`, `INTEGER REFERENCES ic_card(idm)` | `VARCHAR(100)`, `INTEGER; DROP TABLE t`, `TEXT DEFAULT 'O''Reilly'`（クォート二重化を一旦拒否）, 空文字 |

`typeAndConstraints` の DEFAULT 句で `'literal'` を許容する場合、内部のシングルクォートは禁止する（`;` 挿入や SQL インジェクションの足掛かりを避けるため）。これは Migration 用途では妥当な制約。

### 3.2 `table` 検証の一貫化

`HasColumn` の `table` 検証も同じ識別子 regex に統一する。現状の `IndexOfAny(new[] { '\'', '"', ';', ' ' })` は OK だが、`column` と同じパターンに揃えることで「予約語ではない素朴な SQLite 識別子」という意図を明確にする。これは仕様強化に当たるが、既存 callsite で使われているテーブル名（`ledger_detail`, `ic_card`, `ledger`, `operation_log` 等）はすべてこの regex を通過する。

### 3.3 例外設計

検証失敗時は `ArgumentException` を投げる（既存 `table` 検証と同じ流儀）。メッセージは `.claude/rules/error-messages.md` の 3 要素を満たす:

- 何が: 引数名と実際の値
- なぜ: 「識別子として不正」「許可された型/制約の構文に一致しない」
- どうすれば: 「`[A-Za-z_][A-Za-z0-9_]*` の識別子を渡してください」「`INTEGER` `TEXT` 等の SQLite 型に `NOT NULL` / `DEFAULT <値>` を組み合わせてください」

例:

```csharp
throw new ArgumentException(
    $"column '{column}' is not a valid SQLite identifier. " +
    "Use `[A-Za-z_][A-Za-z0-9_]*` (英字または '_' で始まり、英数字または '_' のみ).",
    nameof(column));
```

## 4. 実装詳細

### 4.1 `MigrationHelpers.cs` の変更点

```csharp
private static readonly Regex IdentifierPattern =
    new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

private static readonly Regex TypeAndConstraintsPattern =
    new Regex(
        @"^(INTEGER|TEXT|REAL|BLOB|NUMERIC)" +
        @"(\s+(NOT\s+NULL" +
        @"|DEFAULT\s+(-?\d+(\.\d+)?|'[^';]*'|NULL)" +
        @"|REFERENCES\s+[A-Za-z_][A-Za-z0-9_]*\([A-Za-z_][A-Za-z0-9_]*\)" +
        @"))*\s*$",
        RegexOptions.Compiled);

private static void EnsureValidIdentifier(string value, string paramName) { ... }
private static void EnsureValidTypeAndConstraints(string value) { ... }
```

`HasColumn` 冒頭に `EnsureValidIdentifier(table, nameof(table))` と `EnsureValidIdentifier(column, nameof(column))` を入れる（既存の `IndexOfAny` チェックは regex に置き換え）。

`AddColumnIfNotExists` 冒頭に `EnsureValidTypeAndConstraints(typeAndConstraints)` を入れる。`table` / `column` は `HasColumn` 経由で検証されるため二重検証は不要。

### 4.2 テスト追加

`MigrationHelpersTests.cs` に以下を追加（7 件 → 推定 14 件）:

| # | テスト名 | 目的 |
|---|---|---|
| T1 | `HasColumn_InvalidColumnName_Throws`（`[Theory]`） | `"x; DROP"`, `"1col"`, `"col-name"`, `"col name"`, 空文字 |
| T2 | `AddColumnIfNotExists_InvalidColumnName_Throws`（`[Theory]`） | 同上 |
| T3 | `AddColumnIfNotExists_InvalidTypeAndConstraints_Throws`（`[Theory]`） | `"VARCHAR(100)"`, `"INTEGER; DROP TABLE t"`, `"TEXT DEFAULT 'a;b'"`, 空文字 |
| T4 | `AddColumnIfNotExists_ValidTypeAndConstraints_Accepts`（`[Theory]`） | `"INTEGER"`, `"INTEGER DEFAULT 0"`, `"TEXT"`, `"REAL NOT NULL"`, `"INTEGER REFERENCES ic_card(idm)"` |
| T5 | `HasColumn_InvalidTableName_RegexBased_Throws` | regex 統一の回帰テスト |
| T6 | `AddColumnIfNotExists_NumericTypeWithDecimalDefault_Accepts` | `"REAL DEFAULT 1.5"`（境界値） |
| T7 | `AddColumnIfNotExists_NullDefault_Accepts` | `"TEXT DEFAULT NULL"` |

エラーメッセージ品質（最低 20 文字、3 要素）も `Throw().WithMessage(...)` で軽く検証する。

### 4.3 既存テストへの影響

既存 7 件のテストはすべて regex を通過する値（`"t"`, `"name"`, `"extra"`, `"INTEGER DEFAULT 0"`, `"TEXT"` 等）を使っているため、無修正で通過する。

### 4.4 既存 callsite への影響

7 callsite（Migration_002, 003, 005, 006 ×2, 009 ×2）の実引数はすべて regex を通過する。書き換え不要。

## 5. ドキュメント同期

| ファイル | 更新内容 |
|---|---|
| `.claude/rules/migrations.md` | 「冪等パターン」表の `MigrationHelpers.AddColumnIfNotExists` 行に「列名・型句は識別子/ホワイトリスト regex で検証される」旨を追記 |
| `ICCardManager/CHANGELOG.md` | `### Unreleased` の「セキュリティ」または「保守性」サブセクションに 1 行追記 |
| `ICCardManager/docs/design/07_テスト設計書.md` | §1.1a 単体テスト件数を `3,306` → `3,313` 程度に更新（追加 7 件想定。実測値で確定） |

設計書 02（DB 設計書）はスキーマに影響なしのため更新不要。

## 6. リスクと回避策

- **regex のエッジケース**: ホワイトリスト方針なので「false positive で既存パターンを誤って弾く」リスクが主。`AddColumnIfNotExists_ValidTypeAndConstraints_Accepts` の `[Theory]` に既存 4 パターン全てを含めて回帰防止する。
- **将来の型追加**: 新規 Migration で `BOOLEAN` 等を使いたくなった場合、regex の更新が必要。`migrations.md` にこの注意を明記する。
- **trim 不要**: 末尾空白を含む文字列を許容するため regex 末尾は `\s*$`。先頭空白は禁止のままにする（明示的に通したい場合は事前に `.Trim()` してから渡す方針）。

## 7. ロールアウト

- ブランチ: `fix/issue-1466-migration-helpers-validation`
- PR 単位: 1 PR（本変更 + テスト + 設計書 + CHANGELOG + ルール更新）
- リリース: Unreleased 扱い（次回バージョンに同梱）

## 8. 受け入れ基準

- [ ] `MigrationHelpers.HasColumn` / `AddColumnIfNotExists` に検証 regex 追加
- [ ] 新規テスト 7 件以上が追加され、すべてパス
- [ ] 既存 7 callsite の動作が回帰しない（既存テスト 7 件 + マイグレーション冪等テストがパス）
- [ ] `.claude/rules/migrations.md` に検証方針が明記される
- [ ] CHANGELOG `### Unreleased` に追記される
- [ ] テスト件数の §1.1a が CI test-count-sync をパスする実測値で更新される
