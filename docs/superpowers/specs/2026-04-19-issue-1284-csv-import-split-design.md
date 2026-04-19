# Issue #1284: CsvImportService.Ledger.cs / Detail.cs 責務分割 設計書

作成日: 2026-04-19
対象 Issue: [#1284](https://github.com/kuwayamamasayuki/ICCardManager/issues/1284)
対象ファイル:
- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs` (1031 行)
- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs` (1042 行)

## 背景と問題

PR #1223 で CsvImportService は partial class により 5 ファイルに分割済みだが、`Ledger.cs` と `Detail.cs` は依然として大規模で、以下の問題を抱える:

1. **Ledger.cs の重複**: `ImportLedgersAsync` (L60-277) と `PreviewLedgersAsync` (L480-630) に **ほぼ同一の 200 行のバリデーションロジック**が存在。修正時に同期漏れリスクがある。
2. **Detail.cs の責務混在**: `ImportLedgerDetailsInternalAsync` 内に「既存 Ledger 更新」「新規 Ledger 自動作成」「segment 分割」が混在し、~500 行の巨大メソッドになっている。
3. **モックしにくい**: 検証ロジックがインライン化されているため、単体でテストする手段がない。

## スコープ

### 含む

- `LedgerCsvRowParser` の抽出（Import/Preview 共通化）
- `LedgerDetailCsvRowParser` の抽出
- `NewLedgerFromSegmentsBuilder` の抽出（Detail.cs の新規 Ledger 自動作成ロジック）
- `CsvImportService.LedgerValidation.cs` 新規 partial への helper 移設
- 抽出クラスの単体テスト追加
- public API は変更しない

### 含まない

- 動作仕様の変更
- `CsvImportService.Card.cs` / `Staff.cs` の責務分割（これらは 400 行台で既に妥当なサイズ）
- ログメッセージ・エラーメッセージの文言変更

## 抽出対象（4 ファイル）

| 新規ファイル | パス | 役割 | 推定行数 |
|------------|------|------|---------|
| `LedgerCsvRowParser` | `Services/Import/Parsers/LedgerCsvRowParser.cs` | Import/Preview 共通の行パーサ。列数検証 / 日付・金額・カードIDm のパースと検証 | ~250 |
| `CsvImportService.LedgerValidation` | `Services/Import/CsvImportService.LedgerValidation.cs` (partial) | `DetectLedgerChanges` / `ValidateBalanceConsistency` / `ValidateBalanceConsistencyForLedgers` / `GetPreviousBalanceByCardAsync` を移設 | ~250 |
| `LedgerDetailCsvRowParser` | `Services/Import/Parsers/LedgerDetailCsvRowParser.cs` | `ParseLedgerDetailFields` / `ValidateColumnCount` / `ValidateBooleanField` を移設 | ~200 |
| `NewLedgerFromSegmentsBuilder` | `Services/Import/Builders/NewLedgerFromSegmentsBuilder.cs` | 履歴ID空欄→新規 Ledger 自動作成（segment 分割含む） | ~150 |

## 設計判断

### Issue 記載の "private class" → internal static class へ変更

Issue は "private class" を推奨しているが、C# の nested private class はユニットテストできない（外部から参照不能）。本プロジェクトは既に `InternalsVisibleTo` を設定しているため、**新規ファイルに `internal static` クラスとして配置**することで:

- Issue の意図（「責務を分けて単体テスト可能にする」）を達成
- 既存のテスト手段（`InternalsVisibleTo` 経由の internal アクセス）と整合
- 将来的な DI 化にも拡張しやすい

### なぜ新規 partial ファイルと新規クラスを使い分けるか

- **新規 partial (`LedgerValidation.cs`)**: `CsvImportService` のインスタンスフィールド（`_ledgerRepository` 等）を直接使うメソッド群 → partial で同じクラスの一部として残す
- **新規 internal static クラス (`*Parser.cs` / `*Builder.cs`)**: 純粋関数的にパース・構築を行うロジック → 外部依存を引数で受け取る形にして independence を高める

### なぜ Transaction Builder ではなく "NewLedgerFromSegmentsBuilder"

Issue は "LedgerTransactionBuilder" を推奨するが、実コードを見ると `Ledger.cs` には明示的 transaction 使用がない（`InsertAsync` をループ呼び出しするのみ）。一方 `Detail.cs` の "新規 Ledger 自動作成" (L429-529) は `SplitAtChargeBoundaries` を使った segment 分割 + Ledger 作成 + Detail 一括挿入という明確な責務単位。こちらを `NewLedgerFromSegmentsBuilder` として切り出すことで実効性の高い責務分離ができる。

## 期待される効果

| ファイル | Before | After |
|---------|--------|-------|
| `Ledger.cs` | 1031 行 | ~500 行（orchestration のみ） |
| `Detail.cs` | 1042 行 | ~550 行（orchestration のみ） |
| 新規 4 ファイル合計 | - | ~850 行 |

Ledger の重複 200 行がなくなり、責務境界が明示される。

## テスト戦略

### ベースライン保全

既存テスト（`CsvImportServiceTests` 3163 行 + `CsvImportServiceExceptionLoggingTests` 217 行、計 94 件）が全件 pass することをもって振る舞い不変性を担保する。

### 新規テスト

各抽出クラス向けに単体テスト追加:

#### LedgerCsvRowParserTests (~10 件)
- 列数不足 / 不正な日付 / 不正な金額 / 不正な残額 / 不正な ID
- カード未登録 / IDm 空欄 + targetCardIdm 指定 / IDm 空欄 + 未指定
- 正常ケース（全フィールド / ID なし / note 空）

#### LedgerDetailCsvRowParserTests (~8 件)
- 列数不足 / 不正な日付 / 不正な金額 / 不正な Boolean
- 正常ケース（IsCharge / IsPointRedemption / 通常利用）

#### NewLedgerFromSegmentsBuilderTests (~6 件)
- 単一セグメント（通常利用）/ チャージを含む複数セグメント
- ポイント還元を含むセグメント / 空リスト
- Summary 自動生成確認 / 日付 fallback（MinValue → 利用日最古値 → Now）

テストファイル: `ICCardManager/tests/ICCardManager.Tests/Services/Import/` 配下に新設

## 段階実装

| Phase | 内容 |
|-------|-----|
| 1 | `LedgerCsvRowParser` 抽出 → ImportLedgersAsync で使用 → テスト |
| 2 | PreviewLedgersAsync も同パーサへ切替 → 重複削除 → テスト |
| 3 | `LedgerValidation` partial 分離 → テスト |
| 4 | `LedgerDetailCsvRowParser` 抽出 → テスト |
| 5 | `NewLedgerFromSegmentsBuilder` 抽出 → テスト |
| 6 | 単体テスト 24 件追加 → 全体テスト |
| 7 | 設計書 (05_クラス設計書 / 07_テスト設計書) 更新 |
| 8 | CHANGELOG 更新 → PR |

各 Phase 後に `dotnet test --filter "FullyQualifiedName~CsvImport"` を実行して regression を検出する。

## リスクと対策

| リスク | 対策 |
|-------|-----|
| Import/Preview で微妙な挙動差がある箇所の見落とし | 重複を削除する前に、2 つのメソッドを並べて diff し、微差がないか精査。見つけた微差は変更履歴に残す |
| Balance consistency validation の副作用喪失 | `ValidateBalanceConsistency` は `errors` を out で書き換えるため、partial 移設後も同じシグネチャを維持 |
| NewLedgerFromSegmentsBuilder の例外処理がずれる | 既存のトライキャッチ構造（`errors` リストへの追加）を Builder 側にも再現 |

## 非対象（別 Issue 候補）

- Card / Staff の import サービスは現状 400 行台で妥当 → 分割不要
- `CsvImportService.cs` (523 行) — コア処理なのでこのまま
