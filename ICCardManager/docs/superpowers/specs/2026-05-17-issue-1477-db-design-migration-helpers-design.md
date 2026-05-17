# Issue #1477 — DB 設計書 02 にマイグレーション冪等化ヘルパー（MigrationHelpers）の存在を記載する

- 起票: 2026-05-08（リポジトリ全体レビュー、ドキュメント観点エージェント、Docs M6）
- 修正対象: `ICCardManager/docs/design/02_DB設計書.md` / `ICCardManager/CHANGELOG.md`
- 種別: documentation
- 関連 Issue: #1285（v2.8.0 で `MigrationHelpers.AddColumnIfNotExists` 導入）

## 1. 背景

v2.8.0（2026-05-03）で `MigrationHelpers.AddColumnIfNotExists` および `HasColumn` を導入し、非冪等だった 5 つの `ALTER TABLE ADD COLUMN` 型マイグレーション（#002 / #003 / #005 / #006 / #009）を冪等化した（Issue #1285）。これは共有モードで複数 PC が初回起動時にマイグレーション競合した場合や、`schema_migrations` テーブル部分破損時の再適用エラーを防ぐための重要な変更である。

しかし、DB 設計書 02 の §4 マイグレーション履歴は 001〜009 の表とチェックリストが現状と一致しているにもかかわらず、`MigrationHelpers` の存在と冪等性の方針自体には一切言及がない。冪等性の方針は `.claude/rules/migrations.md` のみで述べられているため、設計書だけ読む読者（DB スキーマ進化を追う Claude エージェントを含む）には伝わらない。

加えて、§4 末尾の「新規マイグレーションを追加した際のチェックリスト」は (1) 本表に追加、(2) テーブル定義のカラム説明に追記、(3) `MigrationRunner` のテストを追加、の 3 項目に留まっており、**`Up()` を冪等に書く**という運用上必須の要件が抜けている。新規マイグレーション追加者がこのチェックリストだけを根拠に実装した場合、非冪等な `ALTER TABLE` を再導入してしまうリスクがある。

## 2. 修正方針

### 2.1 `ICCardManager/docs/design/02_DB設計書.md` への追記（2 箇所）

#### (a) 既存「新規マイグレーションを追加した際のチェックリスト」blockquote に 4 項目目を追加

現状（§4 末尾、`---` 直前）:

```markdown
> **新規マイグレーションを追加した際のチェックリスト**:
> 1. 本表に1行追加する（`DEFAULT値` 列も忘れずに）
> 2. 該当テーブル定義(§3)のカラム説明に「(マイグレーション番号 / Issue番号)」を追記する
> 3. `MigrationRunner` のテストを追加する(`07_テスト設計書.md` 参照)
```

追加項目（末尾に 4 項目目）:

```markdown
> 4. `Up()` は冪等パターン（`CREATE TABLE IF NOT EXISTS` / `CREATE INDEX IF NOT EXISTS` /
>    `MigrationHelpers.AddColumnIfNotExists` / `INSERT OR IGNORE` 等）で書く。
>    詳細は `.claude/rules/migrations.md` 参照
```

#### (b) チェックリスト blockquote の直後・`---` 直前に冪等性ヘルパー blockquote を追加

```markdown
> **冪等性ヘルパー**: `src/ICCardManager/Data/Migrations/MigrationHelpers.cs` に
> `HasColumn` / `AddColumnIfNotExists` を実装（Issue #1285、v2.8.0）。
> SQLite の `ALTER TABLE ADD COLUMN` は二重実行で "duplicate column" エラーになるため、
> `PRAGMA table_info()` で事前に列の有無を確認する方式で冪等化している。
> 共有モードで複数 PC が初回起動時にマイグレーション競合した場合や、
> `schema_migrations` テーブル部分破損時の再適用に備える。
> 詳細は `.claude/rules/migrations.md` 参照。
```

### 2.2 `ICCardManager/CHANGELOG.md` への追記（1 箇所）

`Unreleased` の「ドキュメント整理」セクション末尾に次のエントリを追加:

```markdown
- `docs/design/02_DB設計書.md` §4 マイグレーション履歴の末尾に、v2.8.0 で導入された
  冪等化ヘルパー `MigrationHelpers.AddColumnIfNotExists`（Issue #1285）の存在と参照先を追記。
  従来 `.claude/rules/migrations.md` のみで言及されていたため、設計書だけ読む読者には
  ヘルパーの存在が伝わらなかった。併せて「新規マイグレーションを追加した際のチェックリスト」
  に「`Up()` は冪等パターンで書く」項目を追加し、運用上の必須要件として明文化する。
  プログラム変更を伴わないドキュメント校正のため単体テストの追加・既存テストの修正なし（#1477）
```

## 3. 設計上の判断

### 3.1 「節末尾」の位置選定

§4 マイグレーション履歴の構造は (i) 概要文、(ii) 001〜009 のマイグレーション表、(iii) DEFAULT 値の読み方 blockquote、(iv) 新規マイグレーションを追加した際のチェックリスト blockquote、(v) `---` 区切り、(vi) §5 インデックス設計、という順序である。

冪等性ヘルパーの説明は (iv) の直後・(v) の直前に配置する。理由:

- 「新規マイグレーション追加時の運用知識」というテーマで (iv) と隣接させた方が、関連情報が一塊で読める
- (ii) のマイグレーション表に列を追加して「冪等化済み」フラグを置くことも検討したが、表が横長になり可読性が落ちる + 全マイグレーションが既に冪等化されているため列の情報量が乏しい

### 3.2 チェックリストへの追加位置

既存 3 項目の後に「4.」として追加する。`Up()` 実装は (1)〜(3) の前段階（DB に手を入れる本体作業）であるため論理的には (0) または冒頭に置くことも考えられるが、既存番号を維持する方が diff が最小になり、PR レビュー時の見通しも良い。

### 3.3 文言の出典

冪等性ヘルパーの説明文は `MigrationHelpers.cs` のクラスコメント（"SQLite の `ALTER TABLE ADD COLUMN` は二重実行時に "duplicate column" エラーを出すため、`PRAGMA table_info()` で事前に列の有無を確認する方式で冪等化する。"）と CHANGELOG v2.8.0 の #1285 エントリ（"共有モードで複数 PC が初回起動時にマイグレーション競合した場合や、`schema_migrations` テーブル部分破損時の再適用エラーを防止"）を Single Source として要約する。設計書内で新事実を作らない。

## 4. 検証方法

ドキュメントのみの修正で、ビルド・テスト対象のコードは変更しない。代わりに以下の検証を行う:

1. **参照先存在チェック**: 追記内で言及するパスが現リポジトリ上に存在することを目視確認する
   - `src/ICCardManager/Data/Migrations/MigrationHelpers.cs` の存在
   - `.claude/rules/migrations.md` の存在
2. **diff レビュー**: `git diff main -- ICCardManager/docs/design/02_DB設計書.md ICCardManager/CHANGELOG.md` で意図通りの差分のみであることを確認する
3. **整合性チェック**: `.claude/rules/migrations.md` の冪等パターン記述（`CREATE TABLE IF NOT EXISTS` / `MigrationHelpers.AddColumnIfNotExists` / `INSERT OR IGNORE` 等）と、追加するチェックリスト項目 4. の文言が一致していること

## 5. テストの取り扱い

`02_DB設計書.md` および `CHANGELOG.md` はビルド対象ではないため単体テストは作成しない。代わりに本 PR の Description に「検証方法」（§4）の結果を箇条書きで記録する。

## 6. ロールバック

`git revert <commit>` で復元可能。リスクは極小（ソースコード・スキーマ・ビルド構成への影響なし）。

## 7. スコープ外

- `MigrationHelpers` の機能追加・改修（例: `DropColumnIfExists` の新設等）
- `.claude/rules/migrations.md` の更新（既に最新で、本 Issue は設計書側の欠落を埋めるもの）
- 既存マイグレーション 001〜009 の冪等性再検証（v2.8.0 で完了済み）
- マイグレーション表（001〜009）の構造変更や列追加

## 8. 参考

- Issue #1477
- Issue #1285（`MigrationHelpers` 導入元）
- 関連既存ファイル:
  - `ICCardManager/docs/design/02_DB設計書.md` §4（修正対象）
  - `ICCardManager/src/ICCardManager/Data/Migrations/MigrationHelpers.cs`（参照先）
  - `.claude/rules/migrations.md`（冪等性方針の本体）
  - `ICCardManager/CHANGELOG.md` v2.8.0 §セキュリティ修正の #1285 エントリ
