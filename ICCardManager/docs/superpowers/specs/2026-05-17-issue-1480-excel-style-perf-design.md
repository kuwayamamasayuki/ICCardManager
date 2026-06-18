# Issue #1480: ClosedXML 月次帳票スタイル設定の最適化

## 概要

`ICCardManager/src/ICCardManager/Services/ExcelStyleFormatter.cs` は月次帳票の各行に対し罫線・フォント・配置などのスタイルを個別適用している。月次帳票の生成では 1 ヶ月あたり 30〜60 行、12 ヶ月一括出力では数百〜数千回のスタイル変更が発生する。ClosedXML はスタイル変更が比較的重いため、無駄な操作を削減して帳票生成体感を改善する。

## 現状分析

### 計測対象メソッド

| メソッド | 呼出元 | 連続性 | 重複箇所 |
|---------|--------|--------|---------|
| `ApplyDataRowBorder` | `ReportService` から各データ行 | 他行種別とインターリーブ | `worksheet.Range(row,1,row,12)` を 2 回生成（line 45/76） |
| `ApplySummaryRowBorder` | 月計・累計行 | 単発 | 同上（line 107/137） |
| `ApplyEmptyRowBorder` | `FillEmptyRowsWithBorders` で**連続行**にループ呼出 | **連続が保証される** | ループ全体が per-row 適用 |

### ClosedXML のスタイル適用コスト

- `IXLRange.Style` への代入や `Style.Border.X = ...` などのプロパティ設定は、内部で対象セルすべてに style hash の更新を波及させる
- 同一の Range を複数回取得しても各取得は新しい `IXLRange` インスタンス生成を伴う（小コストだが累積する）
- 連続する複数行に対して 1 つの Range で `Style.Border.InsideBorder = Thin` を 1 回だけ呼ぶ方が、各行で個別に呼ぶより内部の bookkeeping が少ない

## 設計（Option A: 後方互換最適化）

### 方針

1. **既存 per-row API は維持**: `ApplyDataRowBorder`, `ApplySummaryRowBorder`, `ApplyEmptyRowBorder` のシグネチャ・挙動を変更しない（既存テスト 30+ 件と `ReportService` 呼出箇所への影響をゼロにする）。
2. **連続空白行用の一括版を新設**: `ApplyEmptyRowBordersToRange(IXLWorksheet, int firstRow, int lastRow)` を追加。`FillEmptyRowsWithBorders` で連続する空白行を 1 度の範囲適用で塗る。
3. **per-row メソッドの内部最適化**: `worksheet.Range(row,1,row,12)` の重複生成を 1 回にまとめる。per-cell 操作（`worksheet.Cell(row, 8)`）はそのまま残す（個別書式が必要なため）。

### 新規 API

```csharp
/// <summary>
/// 連続する空白行に罫線を一括適用します（性能最適化版）。
/// </summary>
/// <remarks>
/// <para>Issue #1480: ループから抜け出して 1 つの Range に対して罫線・行高さを一括適用。</para>
/// <para>per-row 版 <see cref="ApplyEmptyRowBorder"/> と等価な視覚結果になることを保証。</para>
/// <para>firstRow == lastRow の場合は単一行に等価。firstRow &gt; lastRow の場合は no-op。</para>
/// </remarks>
internal static void ApplyEmptyRowBordersToRange(IXLWorksheet worksheet, int firstRow, int lastRow);
```

### per-row メソッドの最適化

- `ApplyDataRowBorder`: 同一 Range の重複取得を統合
  - `fullRange` (line 45) と `range` (line 76) は同じ範囲なので 1 変数化
- `ApplySummaryRowBorder`: 同上 (line 107 / 137)
- `ApplyEmptyRowBorder`: 既に `fullRange` と `range` の 2 重取得があるので統合

### ReportService の変更

`FillEmptyRowsWithBorders` をループから一括版呼出に置換:

```csharp
// Before
for (int i = 0; i < emptyRowsCount; i++)
{
    ApplyEmptyRowBorder(worksheet, currentRow + i);
}

// After
ExcelStyleFormatter.ApplyEmptyRowBordersToRange(worksheet, currentRow, currentRow + emptyRowsCount - 1);
```

ただし注意点として、`ApplyEmptyRowBorder` 内では `Range(row, 2, row, 4).Merge()` と `Range(row, 9, row, 12).Merge()` の**行単位セル結合**を行うため、一括版でもこの結合は**行ごとに**実行する必要がある（連続行をまとめて結合すると 1 つの結合セルになってしまう）。

## テスト計画

### 等価性テスト（新規）

`ExcelStyleFormatterTests` に以下を追加:

1. `ApplyEmptyRowBordersToRange_SingleRow_EquivalentToPerRow`
   - 1 行範囲で適用 → per-row 版と同じスタイル結果（罫線・高さ・結合）
2. `ApplyEmptyRowBordersToRange_MultipleRows_AllRowsHaveBorders`
   - 5 行範囲で適用 → 各行の上下左右罫線・行高さ・B-D / I-L 結合が正しい
3. `ApplyEmptyRowBordersToRange_EmptyRange_DoesNothing`
   - `firstRow > lastRow` で no-op
4. `ApplyEmptyRowBordersToRange_BordersAtEdges_AreMedium`
   - 範囲の左端 A 列と右端 L 列が太線

### 既存テストの維持

- `ApplyDataRowBorder` / `ApplySummaryRowBorder` / `ApplyEmptyRowBorder` の per-row 版が既存テストにパスする（30+ 件）

### ベンチマークテスト（新規）

`ExcelStyleFormatterPerfTests` を新設し、1 シートに 12 ヶ月相当（約 360 行）のスタイル適用を行って所要時間を計測。**回帰検知**目的（CI で時間ベースのアサーションは flaky になるため、`Skip` 付きで結果ログ出力のみ）。

## 影響範囲

| 影響対象 | 内容 |
|---------|------|
| `ExcelStyleFormatter.cs` | 新規メソッド追加 + per-row 内部最適化 |
| `ReportService.cs` | `FillEmptyRowsWithBorders` のループを置換 |
| `ExcelStyleFormatterTests.cs` | 新規テスト 4 件追加 |
| `ExcelStyleFormatterPerfTests.cs`（新規） | ベンチマーク 1 件追加 |
| `docs/design/07_テスト設計書.md` | テストケース追加 |
| `ICCardManager/CHANGELOG.md` | 性能改善エントリ追加 |

## ロールバック計画

API 互換のため、`ApplyEmptyRowBordersToRange` の呼出箇所のみ revert すれば全体の挙動は per-row ループに戻る。

## 出典

- Issue #1480: https://github.com/kuwayamamasayuki/ICCardManager/issues/1480
- 2026-05-08 リポジトリ全体レビュー（性能観点エージェント、Perf M3）
- ClosedXML Performance Best Practices: https://github.com/ClosedXML/ClosedXML/wiki/Performance
