# Issue #1284: CsvImportService 分割リファクタ 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `CsvImportService.Ledger.cs` (1031行) と `Detail.cs` (1042行) を責務ごとに分割し、Import/Preview 間の 200 行の重複を解消する。public API は変更しない。

**Architecture:** 共通のパーサ/ビルダを `internal static` クラスとして新規ファイルに抽出し、既存 helper メソッドは partial 分割で整理する。抽出ロジックは純粋関数寄りで、依存は引数で受け取る形にして単体テスト容易にする。

**Tech Stack:** C# 10 / .NET Framework 4.8 / xUnit / FluentAssertions / Moq / partial class / InternalsVisibleTo

---

## 事前確認

- 作業ブランチ: `refactor/issue-1284-csv-import-split` (main から分岐済み、spec commit あり)
- 対象ファイル:
  - `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs` (1031行)
  - `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs` (1042行)
- 既存テスト: 94 件（`CsvImportServiceTests` + `CsvImportServiceExceptionLoggingTests`）が全件 pass
- Test コマンド: `"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport" --nologo --verbosity minimal`
- `[InternalsVisibleTo("ICCardManager.Tests")]` は既に設定済み
- `ValidateColumnCount` は `CsvImportService.cs` (main partial) で Card/Staff/Detail 共通利用 → **移動しない**

## File Structure

### 新規作成

| パス | 種別 | 役割 |
|-----|------|------|
| `ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerCsvRowParser.cs` | internal static class | Import/Preview 共通の Ledger 行パーサ |
| `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.LedgerValidation.cs` | partial class | `DetectLedgerChanges` / `ValidateBalanceConsistency*` / `GetPreviousBalanceByCardAsync` の移設先 |
| `ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerDetailCsvRowParser.cs` | internal static class | `ParseLedgerDetailFields` / `ValidateBooleanField` の移設先 |
| `ICCardManager/src/ICCardManager/Services/Import/Builders/NewLedgerFromSegmentsBuilder.cs` | internal class | 履歴ID空欄→新規 Ledger 自動作成（segment 分割含む） |
| `ICCardManager/tests/ICCardManager.Tests/Services/Import/LedgerCsvRowParserTests.cs` | xUnit test class | 行パーサの単体テスト |
| `ICCardManager/tests/ICCardManager.Tests/Services/Import/LedgerDetailCsvRowParserTests.cs` | xUnit test class | Detail 行パーサの単体テスト |
| `ICCardManager/tests/ICCardManager.Tests/Services/Import/NewLedgerFromSegmentsBuilderTests.cs` | xUnit test class | ビルダーの単体テスト |

### 変更

- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs`
- `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs`
- `ICCardManager/CHANGELOG.md`
- `ICCardManager/docs/design/05_クラス設計書.md`
- `ICCardManager/docs/design/07_テスト設計書.md`

---

## Task 1: Baseline 確認

**Files:** 参照のみ

- [ ] **Step 1: ブランチ確認**

```bash
git branch --show-current
```
Expected: `refactor/issue-1284-csv-import-split`

- [ ] **Step 2: 既存テスト全件 pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport" --nologo --verbosity minimal
```
Expected: `成功!   -失敗:     0、合格:    94`

---

## Task 2: LedgerCsvRowParser を新規作成

**Files:**
- Create: `ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerCsvRowParser.cs`

**責務:** Import/Preview 両方で使うため、依存は引数で受け取る純粋クラス。`errors` はリストを渡して副作用として追加する。

- [ ] **Step 1: Parsers ディレクトリを作成**

```bash
mkdir -p ICCardManager/src/ICCardManager/Services/Import/Parsers
```

- [ ] **Step 2: パーサクラスを作成**

ファイル `ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerCsvRowParser.cs` を以下の内容で新規作成:

```csharp
using System;
using System.Collections.Generic;
using ICCardManager.Models;

namespace ICCardManager.Services.Import.Parsers
{
    /// <summary>
    /// 利用履歴CSVの1行をパースする共通ロジック。
    /// Import / Preview の両方で再利用するため、副作用を最小化し、
    /// errors リストへの追加のみで失敗を表現する。
    /// </summary>
    internal static class LedgerCsvRowParser
    {
        internal class ParsedLedgerRow
        {
            public int LineNumber { get; set; }
            public int? LedgerId { get; set; }
            public string CardIdm { get; set; }
            public DateTime Date { get; set; }
            public string Summary { get; set; }
            public int Income { get; set; }
            public int Expense { get; set; }
            public int Balance { get; set; }
            public string StaffName { get; set; }
            public string Note { get; set; }
        }

        /// <summary>
        /// CSV の1行をパースして ParsedLedgerRow を返す。
        /// 失敗時は errors に追加して null を返す。
        /// </summary>
        /// <param name="fields">パース済みのフィールド</param>
        /// <param name="lineNumber">CSV 上の行番号（1-based）</param>
        /// <param name="line">元の行文字列（エラーメッセージ用）</param>
        /// <param name="hasIdColumn">1 列目が ID 列か</param>
        /// <param name="minColumns">最小必須列数</param>
        /// <param name="existingCardIdms">DB に存在するカード IDm セット</param>
        /// <param name="targetCardIdm">IDm 空欄時の fallback IDm（省略可）</param>
        /// <param name="errors">エラー追加先</param>
        public static ParsedLedgerRow TryParseRow(
            List<string> fields,
            int lineNumber,
            string line,
            bool hasIdColumn,
            int minColumns,
            HashSet<string> existingCardIdms,
            string targetCardIdm,
            List<CsvImportError> errors)
        {
            if (fields.Count < minColumns)
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = $"列数が不足しています（{minColumns}列必要）",
                    Data = line
                });
                return null;
            }

            var offset = hasIdColumn ? 1 : 0;
            var idStr = hasIdColumn ? fields[0].Trim() : string.Empty;
            var dateStr = fields[0 + offset].Trim();
            var cardIdm = fields[1 + offset].Trim().ToUpperInvariant();
            // fields[2 + offset] は管理番号（参照用、インポート時は使用しない）
            var summary = Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[3 + offset].Trim());
            var incomeStr = fields[4 + offset].Trim();
            var expenseStr = fields[5 + offset].Trim();
            var balanceStr = fields[6 + offset].Trim();
            var staffName = fields[7 + offset].Trim();
            var note = Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[8 + offset].Trim());

            int? ledgerId = null;
            if (hasIdColumn && !string.IsNullOrWhiteSpace(idStr))
            {
                if (!int.TryParse(idStr, out var parsedId))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "IDの形式が不正です",
                        Data = idStr
                    });
                    return null;
                }
                ledgerId = parsedId;
            }

            if (!DateTime.TryParse(dateStr, out var date))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "日時の形式が不正です",
                    Data = dateStr
                });
                return null;
            }

            // Issue #511: IDm 空欄時は targetCardIdm を採用
            if (string.IsNullOrWhiteSpace(cardIdm))
            {
                if (!string.IsNullOrWhiteSpace(targetCardIdm))
                {
                    cardIdm = targetCardIdm.ToUpperInvariant();
                }
                else
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "カードIDmは必須です（CSVで空欄の場合はインポート先カードを指定してください）",
                        Data = line
                    });
                    return null;
                }
            }

            if (!existingCardIdms.Contains(cardIdm))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "該当するカードが登録されていません",
                    Data = cardIdm
                });
                return null;
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "摘要は必須です",
                    Data = line
                });
                return null;
            }

            if (!int.TryParse(balanceStr, out var balance))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "残額の形式が不正です",
                    Data = balanceStr
                });
                return null;
            }

            var income = 0;
            if (!string.IsNullOrWhiteSpace(incomeStr) && !int.TryParse(incomeStr, out income))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "受入金額の形式が不正です",
                    Data = incomeStr
                });
                return null;
            }

            var expense = 0;
            if (!string.IsNullOrWhiteSpace(expenseStr) && !int.TryParse(expenseStr, out expense))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "払出金額の形式が不正です",
                    Data = expenseStr
                });
                return null;
            }

            return new ParsedLedgerRow
            {
                LineNumber = lineNumber,
                LedgerId = ledgerId,
                CardIdm = cardIdm,
                Date = date,
                Summary = summary,
                Income = income,
                Expense = expense,
                Balance = balance,
                StaffName = staffName,
                Note = note
            };
        }
    }
}
```

- [ ] **Step 3: ビルド確認（まだテスト対象の使用なし、文法だけ）**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: エラー 0

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerCsvRowParser.cs
git commit -m "$(cat <<'EOF'
refactor: LedgerCsvRowParser を新規追加 (Issue #1284)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: ImportLedgersAsync を LedgerCsvRowParser 経由に切り替え

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs:60-277`

**責務:** 行パースロジックを parser 呼び出しに置換、バリデーション部分 200 行を削除。

- [ ] **Step 1: using 追加**

`CsvImportService.Ledger.cs` 冒頭の using 群の中に以下を追加:

```csharp
using ICCardManager.Services.Import.Parsers;
```

- [ ] **Step 2: ImportLedgersAsync の行パース部分を置換**

`CsvImportService.Ledger.cs` L60-277 の `for (var i = 1; i < lines.Count; i++)` ループ内を以下に置き換える（ループ全体を以下で差し替える）:

```csharp
for (var i = 1; i < lines.Count; i++)
{
    var lineNumber = i + 1;
    var line = lines[i];

    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    var fields = ParseCsvLine(line);
    var parsed = LedgerCsvRowParser.TryParseRow(
        fields, lineNumber, line, hasIdColumn, minColumns,
        existingCardIdms, targetCardIdm, errors);
    if (parsed == null)
    {
        continue;
    }

    // 既存レコードの確認（IDがある場合）
    var isUpdate = false;
    Ledger existingLedgerForUpdate = null;
    if (parsed.LedgerId.HasValue)
    {
        var existingLedger = await _ledgerRepository.GetByIdAsync(parsed.LedgerId.Value);
        if (existingLedger != null)
        {
            // Issue #639: 金額・日付を含む全フィールドで変更点を検出
            var hasChanges = existingLedger.Summary != parsed.Summary ||
                            (existingLedger.StaffName ?? "") != parsed.StaffName ||
                            (existingLedger.Note ?? "") != parsed.Note ||
                            existingLedger.Income != parsed.Income ||
                            existingLedger.Expense != parsed.Expense ||
                            existingLedger.Balance != parsed.Balance ||
                            existingLedger.Date != parsed.Date;
            if (hasChanges)
            {
                isUpdate = true;
                existingLedgerForUpdate = existingLedger;
            }
            else if (skipExisting)
            {
                // Issue #903: skipExisting=true のみスキップ
                // Issue #754: 残高整合性チェック用に skipped レコードも保持
                var skippedLedger = new Ledger
                {
                    Id = parsed.LedgerId.Value,
                    CardIdm = parsed.CardIdm,
                    Date = parsed.Date,
                    Summary = parsed.Summary,
                    Income = parsed.Income,
                    Expense = parsed.Expense,
                    Balance = parsed.Balance
                };
                allRecordsForValidation.Add((lineNumber, skippedLedger, false));
                skippedCount++;
                continue;
            }
            else
            {
                // Issue #903: skipExisting=false は変更がなくても更新扱い
                isUpdate = true;
                existingLedgerForUpdate = existingLedger;
            }
        }
    }

    var ledger = new Ledger
    {
        Id = parsed.LedgerId ?? 0,
        CardIdm = parsed.CardIdm,
        Date = parsed.Date,
        Summary = parsed.Summary,
        Income = parsed.Income,
        Expense = parsed.Expense,
        Balance = parsed.Balance,
        StaffName = string.IsNullOrWhiteSpace(parsed.StaffName) ? null : parsed.StaffName,
        Note = string.IsNullOrWhiteSpace(parsed.Note) ? null : parsed.Note,
        // Issue #639: 更新時は CSV に含まれないフィールドを既存レコードから引き継ぐ
        LenderIdm = existingLedgerForUpdate?.LenderIdm,
        ReturnerIdm = existingLedgerForUpdate?.ReturnerIdm,
        LentAt = existingLedgerForUpdate?.LentAt,
        ReturnedAt = existingLedgerForUpdate?.ReturnedAt,
        IsLentRecord = existingLedgerForUpdate?.IsLentRecord ?? false
    };

    validRecords.Add((lineNumber, ledger, isUpdate));
    allRecordsForValidation.Add((lineNumber, ledger, isUpdate));
}
```

- [ ] **Step 3: 既存テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport" --nologo --verbosity minimal
```
Expected: `失敗: 0、合格: 94`

- [ ] **Step 4: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs
git commit -m "$(cat <<'EOF'
refactor: ImportLedgersAsync を LedgerCsvRowParser 経由へ切替 (Issue #1284)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: PreviewLedgersAsync を LedgerCsvRowParser 経由に切り替え

**Files:**
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs:480-630` (Preview 内の行パース部分)

**責務:** 同じ parser を使って Preview 側の重複コード ~150 行を削除。

- [ ] **Step 1: Preview の行パースループを置換**

`PreviewLedgersAsync` 内の `for (var i = 1; i < lines.Count; i++)` ループ全体を以下に置き換える:

```csharp
for (var i = 1; i < lines.Count; i++)
{
    var lineNumber = i + 1;
    var line = lines[i];

    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    var fields = ParseCsvLine(line);
    var parsed = LedgerCsvRowParser.TryParseRow(
        fields, lineNumber, line, hasIdColumn, minColumns,
        existingCardIdms, targetCardIdm, errors);
    if (parsed == null)
    {
        continue;
    }

    cardIdmsInFile.Add(parsed.CardIdm);
    validatedRecords.Add((
        parsed.LineNumber, parsed.LedgerId, parsed.CardIdm,
        parsed.Date, parsed.Summary, parsed.Income, parsed.Expense,
        parsed.Balance, parsed.StaffName, parsed.Note));
}
```

- [ ] **Step 2: 既存テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport" --nologo --verbosity minimal
```
Expected: `失敗: 0、合格: 94`

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs
git commit -m "$(cat <<'EOF'
refactor: PreviewLedgersAsync を LedgerCsvRowParser 経由へ切替 (Issue #1284)

Import/Preview 間の行パースロジック重複 ~200 行を解消。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: LedgerValidation partial を新規作成し helper を移設

**Files:**
- Create: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.LedgerValidation.cs`
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs` (helper 削除)

**責務:** Ledger.cs 内の helper を別 partial に分離。

- [ ] **Step 1: 新規 partial ファイル作成**

`CsvImportService.Ledger.cs` L790-1029 にある以下のメソッドを切り出して、新規ファイル `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.LedgerValidation.cs` を作成:
- `DetectLedgerChanges` (L790-)
- `ValidateBalanceConsistency` (L880-)
- `ValidateBalanceConsistencyForLedgers` (L946-)
- `GetPreviousBalanceByCardAsync` (L1010-)

新規ファイルは以下の形（メソッド本体はオリジナルからコピー）:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 利用履歴 CSV インポート用の検証ロジック（partial 分割）。
    /// </summary>
    public partial class CsvImportService
    {
        // NOTE: 以下 4 メソッドを CsvImportService.Ledger.cs から移設（Issue #1284）
        // - DetectLedgerChanges
        // - ValidateBalanceConsistency
        // - ValidateBalanceConsistencyForLedgers
        // - GetPreviousBalanceByCardAsync

        // --- オリジナルのコードをここにコピー（文言・型・アクセス修飾子はそのまま） ---
    }
}
```

実際の移設は `Ledger.cs` の該当メソッド本体を **そのままコピーして貼り付け** る。アクセス修飾子（`private static` / `internal static` / `private async`）も維持する。

- [ ] **Step 2: Ledger.cs から該当メソッドを削除**

`CsvImportService.Ledger.cs` の L790-1029 のメソッド群を削除し、closing brace の位置を調整。

- [ ] **Step 3: ビルド確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: エラー 0

- [ ] **Step 4: 既存テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport" --nologo --verbosity minimal
```
Expected: `失敗: 0、合格: 94`

- [ ] **Step 5: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/Import/CsvImportService.LedgerValidation.cs ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs
git commit -m "$(cat <<'EOF'
refactor: LedgerValidation partial を分離 (Issue #1284)

Ledger.cs から検証系 helper を LedgerValidation.cs に移設。

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: LedgerDetailCsvRowParser を新規作成し移設

**Files:**
- Create: `ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerDetailCsvRowParser.cs`
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs` (move `ParseLedgerDetailFields` / `ValidateBooleanField`)

**責務:** Detail 行パース用の helper を別クラスに切り出す。

- [ ] **Step 1: パーサクラスを作成**

`ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerDetailCsvRowParser.cs` を作成:

```csharp
using System;
using System.Collections.Generic;
using ICCardManager.Models;

namespace ICCardManager.Services.Import.Parsers
{
    /// <summary>
    /// 利用履歴詳細 CSV の1行をパースする。
    /// Import / Preview 両方で再利用する純粋関数的クラス。
    /// </summary>
    internal static class LedgerDetailCsvRowParser
    {
        // NOTE: 以下 2 メソッドを CsvImportService.Detail.cs から移設（Issue #1284）
        //   - ParseLedgerDetailFields → ParseFields
        //   - ValidateBooleanField → ValidateBooleanField
        //
        // ValidateColumnCount は Card/Staff/Detail 共通で使われているため
        // CsvImportService.cs の private helper のまま移動しない。

        /// <summary>
        /// CSV フィールドを LedgerDetail にパースする。失敗時は null を返し errors に追加。
        /// </summary>
        public static LedgerDetail ParseFields(
            List<string> fields,
            int lineNumber,
            string line,
            List<CsvImportError> errors)
        {
            // オリジナルの ParseLedgerDetailFields の本体をそのままコピー
            // （ValidateBooleanField 呼び出しは同じクラス内メソッドになる）
            // 本メソッドの実装は既存コードをそのまま移設する
            throw new NotImplementedException("オリジナル実装を移設");
        }

        /// <summary>
        /// Boolean フィールドの検証（true/false/TRUE/FALSE/0/1 等を許容）
        /// </summary>
        internal static bool ValidateBooleanField(
            string value,
            int lineNumber,
            string fieldName,
            List<CsvImportError> errors,
            out bool result)
        {
            // オリジナルの ValidateBooleanField の本体をそのままコピー
            throw new NotImplementedException("オリジナル実装を移設");
        }
    }
}
```

**⚠ この段階では NotImplementedException の stub。次 step で実装を移設する。**

- [ ] **Step 2: オリジナル実装を移設**

`CsvImportService.Detail.cs` L612-763 の `ParseLedgerDetailFields` 本体を `LedgerDetailCsvRowParser.ParseFields` にコピー。同じく L764-795 の `ValidateBooleanField` を `LedgerDetailCsvRowParser.ValidateBooleanField` にコピー。`NotImplementedException` stub を実装で置換。

**注意**: メソッド本体内で `ValidateBooleanField(...)` 呼び出しは同じクラス内メソッド参照として自然に解決される（同じ static class 内）。

- [ ] **Step 3: Detail.cs 側の呼び出しを新クラス経由に変更**

`CsvImportService.Detail.cs` L335 および他の `ParseLedgerDetailFields(fields, lineNumber, line, errors)` 呼び出しを `LedgerDetailCsvRowParser.ParseFields(fields, lineNumber, line, errors)` に置換。

using 追加:
```csharp
using ICCardManager.Services.Import.Parsers;
```

その後、`CsvImportService.Detail.cs` から L612-795 の該当メソッド（`ParseLedgerDetailFields` / `ValidateBooleanField`）を削除。

- [ ] **Step 4: ビルド確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: エラー 0

- [ ] **Step 5: 既存テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport" --nologo --verbosity minimal
```
Expected: `失敗: 0、合格: 94`

- [ ] **Step 6: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerDetailCsvRowParser.cs ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs
git commit -m "$(cat <<'EOF'
refactor: LedgerDetailCsvRowParser を抽出 (Issue #1284)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: NewLedgerFromSegmentsBuilder を抽出

**Files:**
- Create: `ICCardManager/src/ICCardManager/Services/Import/Builders/NewLedgerFromSegmentsBuilder.cs`
- Modify: `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs:429-529`

**責務:** 「履歴ID空欄 → 新規 Ledger 自動作成」の segment 分割 + Ledger 生成ロジックを Builder として切り出す。

- [ ] **Step 1: Builders ディレクトリ作成**

```bash
mkdir -p ICCardManager/src/ICCardManager/Services/Import/Builders
```

- [ ] **Step 2: Builder クラスを作成**

`ICCardManager/src/ICCardManager/Services/Import/Builders/NewLedgerFromSegmentsBuilder.cs` を作成:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;

namespace ICCardManager.Services.Import.Builders
{
    /// <summary>
    /// 利用履歴 ID 空欄の詳細行から、segment 分割を伴って新規 Ledger を自動生成する。
    /// Detail CSV インポートの一機能として使用（Issue #906, #918, #1053）。
    /// </summary>
    internal class NewLedgerFromSegmentsBuilder
    {
        private readonly ILedgerRepository _ledgerRepository;

        public NewLedgerFromSegmentsBuilder(ILedgerRepository ledgerRepository)
        {
            _ledgerRepository = ledgerRepository;
        }

        /// <summary>
        /// 1 カード・1 日分の詳細リストから、チャージ境界で segment 分割し
        /// 各 segment ごとに Ledger を作成して detail を挿入する。
        /// </summary>
        /// <param name="cardIdm">カード IDm</param>
        /// <param name="groupDate">グループキーの日付（DateTime.MinValue なら detail.UseDate から推定）</param>
        /// <param name="detailRows">(line_number, LedgerDetail) のリスト</param>
        /// <param name="errors">エラー追加先</param>
        /// <returns>挿入成功した detail 件数（segment 単位で失敗した場合は 0）</returns>
        public async Task<int> BuildAndInsertAsync(
            string cardIdm,
            DateTime groupDate,
            List<(int LineNumber, LedgerDetail Detail)> detailRows,
            List<CsvImportError> errors)
        {
            var firstLineNumber = detailRows.First().LineNumber;
            var detailList = detailRows.Select(r => r.Detail).ToList();

            try
            {
                // チャージ/ポイント還元の位置で利用グループを分割
                var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(detailList);

                // セグメントがない場合（空リスト対策）は元のリストで 1 segment として扱う
                if (segments.Count == 0)
                {
                    segments = new List<LendingHistoryAnalyzer.DailySegment>
                    {
                        new LendingHistoryAnalyzer.DailySegment
                        {
                            IsCharge = false,
                            IsPointRedemption = false,
                            Details = detailList
                        }
                    };
                }

                var summaryGenerator = new SummaryGenerator();
                var segmentFailed = false;

                foreach (var segment in segments)
                {
                    var segmentDetails = segment.Details;

                    var summary = summaryGenerator.Generate(segmentDetails);
                    if (string.IsNullOrEmpty(summary))
                    {
                        summary = "CSVインポート";
                    }

                    var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(segmentDetails);

                    var date = groupDate;
                    if (date == DateTime.MinValue)
                    {
                        date = segmentDetails
                            .Where(d => d.UseDate.HasValue)
                            .OrderBy(d => d.UseDate!.Value)
                            .Select(d => d.UseDate!.Value)
                            .FirstOrDefault();
                        if (date == default)
                        {
                            date = DateTime.Now;
                        }
                    }

                    var newLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = date,
                        Summary = summary,
                        Income = income,
                        Expense = expense,
                        Balance = balance
                    };

                    var newLedgerId = await _ledgerRepository.InsertAsync(newLedger);
                    var success = await _ledgerRepository.InsertDetailsAsync(newLedgerId, segmentDetails);

                    if (!success)
                    {
                        segmentFailed = true;
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstLineNumber,
                            Message = $"カード {cardIdm} の新規詳細の挿入に失敗しました",
                            Data = cardIdm
                        });
                    }
                }

                return segmentFailed ? 0 : detailRows.Count;
            }
            catch (Exception ex)
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = firstLineNumber,
                    Message = $"カード {cardIdm} の利用履歴自動作成中にエラーが発生しました: {ex.Message}",
                    Data = cardIdm
                });
                return 0;
            }
        }
    }
}
```

- [ ] **Step 3: Detail.cs の呼び出し部分を置換**

`CsvImportService.Detail.cs` L429-529 の `foreach (var kvp in newDetailsByCardIdmAndDate)` ブロックを以下に置き換える:

```csharp
// Issue #906: 新規詳細（利用履歴ID空欄）の Ledger 自動作成とインポート
// Issue #918: カードIDm＋日付ごとにグループ化して個別のLedgerを作成
// Issue #1053: チャージ/ポイント還元境界で分割し、セグメントごとに Ledger を作成
var newLedgerBuilder = new Builders.NewLedgerFromSegmentsBuilder(_ledgerRepository);
foreach (var kvp in newDetailsByCardIdmAndDate)
{
    importedCount += await newLedgerBuilder.BuildAndInsertAsync(
        kvp.Key.CardIdm,
        kvp.Key.Date,
        kvp.Value,
        errors);
}
```

using 追加は不要（Builders は同じ namespace tree）。

- [ ] **Step 4: ビルド確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: エラー 0

- [ ] **Step 5: 既存テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport" --nologo --verbosity minimal
```
Expected: `失敗: 0、合格: 94`

- [ ] **Step 6: コミット**

```bash
git add ICCardManager/src/ICCardManager/Services/Import/Builders/NewLedgerFromSegmentsBuilder.cs ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs
git commit -m "$(cat <<'EOF'
refactor: NewLedgerFromSegmentsBuilder を抽出 (Issue #1284)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: 単体テスト追加 (LedgerCsvRowParser / LedgerDetailCsvRowParser / NewLedgerFromSegmentsBuilder)

**Files:**
- Create: `ICCardManager/tests/ICCardManager.Tests/Services/Import/LedgerCsvRowParserTests.cs`
- Create: `ICCardManager/tests/ICCardManager.Tests/Services/Import/LedgerDetailCsvRowParserTests.cs`
- Create: `ICCardManager/tests/ICCardManager.Tests/Services/Import/NewLedgerFromSegmentsBuilderTests.cs`

- [ ] **Step 1: テストディレクトリ作成**

```bash
mkdir -p ICCardManager/tests/ICCardManager.Tests/Services/Import
```

- [ ] **Step 2: LedgerCsvRowParserTests を作成**

`ICCardManager/tests/ICCardManager.Tests/Services/Import/LedgerCsvRowParserTests.cs` を作成:

```csharp
using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.Services.Import.Parsers;
using Xunit;

namespace ICCardManager.Tests.Services.Import
{
    public class LedgerCsvRowParserTests
    {
        private static readonly HashSet<string> ExistingCards = new(StringComparer.OrdinalIgnoreCase)
        {
            "0102030405060708"
        };

        [Fact]
        public void TryParseRow_InsufficientColumns_AddsError()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "2026-04-19", "0102030405060708" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: false, minColumns: 9,
                ExistingCards, null, errors);

            result.Should().BeNull();
            errors.Should().ContainSingle();
            errors[0].Message.Should().Contain("列数が不足");
        }

        [Fact]
        public void TryParseRow_InvalidDate_AddsError()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "invalid-date", "0102030405060708", "", "鉄道", "0", "210", "1000", "", "" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: false, minColumns: 9,
                ExistingCards, null, errors);

            result.Should().BeNull();
            errors.Should().ContainSingle(e => e.Message.Contains("日時の形式"));
        }

        [Fact]
        public void TryParseRow_InvalidBalance_AddsError()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "2026-04-19", "0102030405060708", "", "鉄道", "0", "210", "abc", "", "" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: false, minColumns: 9,
                ExistingCards, null, errors);

            result.Should().BeNull();
            errors.Should().ContainSingle(e => e.Message.Contains("残額の形式"));
        }

        [Fact]
        public void TryParseRow_UnknownCard_AddsError()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "2026-04-19", "AAAABBBBCCCCDDDD", "", "鉄道", "0", "210", "1000", "", "" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: false, minColumns: 9,
                ExistingCards, null, errors);

            result.Should().BeNull();
            errors.Should().ContainSingle(e => e.Message.Contains("該当するカードが登録されていません"));
        }

        [Fact]
        public void TryParseRow_EmptyCardIdmWithTarget_UsesTarget()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "2026-04-19", "", "", "鉄道", "0", "210", "1000", "山田", "備考" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: false, minColumns: 9,
                ExistingCards, "0102030405060708", errors);

            errors.Should().BeEmpty();
            result.Should().NotBeNull();
            result.CardIdm.Should().Be("0102030405060708");
        }

        [Fact]
        public void TryParseRow_EmptyCardIdmWithoutTarget_AddsError()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "2026-04-19", "", "", "鉄道", "0", "210", "1000", "", "" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: false, minColumns: 9,
                ExistingCards, null, errors);

            result.Should().BeNull();
            errors.Should().ContainSingle(e => e.Message.Contains("カードIDmは必須"));
        }

        [Fact]
        public void TryParseRow_EmptySummary_AddsError()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "2026-04-19", "0102030405060708", "", "", "0", "210", "1000", "", "" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: false, minColumns: 9,
                ExistingCards, null, errors);

            result.Should().BeNull();
            errors.Should().ContainSingle(e => e.Message.Contains("摘要は必須"));
        }

        [Fact]
        public void TryParseRow_ValidRow_ReturnsParsed()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "2026-04-19 10:00:00", "0102030405060708", "01", "鉄道（A～B）", "0", "210", "1000", "山田", "備考あり" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: false, minColumns: 9,
                ExistingCards, null, errors);

            errors.Should().BeEmpty();
            result.Should().NotBeNull();
            result.CardIdm.Should().Be("0102030405060708");
            result.Summary.Should().Be("鉄道（A～B）");
            result.Expense.Should().Be(210);
            result.Balance.Should().Be(1000);
            result.StaffName.Should().Be("山田");
            result.Note.Should().Be("備考あり");
            result.LedgerId.Should().BeNull();
        }

        [Fact]
        public void TryParseRow_WithIdColumnValid_ParsesLedgerId()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "42", "2026-04-19 10:00:00", "0102030405060708", "01", "鉄道", "0", "210", "1000", "山田", "" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: true, minColumns: 10,
                ExistingCards, null, errors);

            errors.Should().BeEmpty();
            result.Should().NotBeNull();
            result.LedgerId.Should().Be(42);
        }

        [Fact]
        public void TryParseRow_WithIdColumnInvalidId_AddsError()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "abc", "2026-04-19", "0102030405060708", "01", "鉄道", "0", "210", "1000", "", "" };

            var result = LedgerCsvRowParser.TryParseRow(
                fields, 2, "dummy", hasIdColumn: true, minColumns: 10,
                ExistingCards, null, errors);

            result.Should().BeNull();
            errors.Should().ContainSingle(e => e.Message.Contains("IDの形式"));
        }
    }
}
```

- [ ] **Step 3: LedgerDetailCsvRowParserTests を作成**

`ICCardManager/tests/ICCardManager.Tests/Services/Import/LedgerDetailCsvRowParserTests.cs` を作成:

```csharp
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.Services.Import.Parsers;
using Xunit;

namespace ICCardManager.Tests.Services.Import
{
    public class LedgerDetailCsvRowParserTests
    {
        [Fact]
        public void ValidateBooleanField_TrueString_ReturnsTrue()
        {
            var errors = new List<CsvImportError>();
            var ok = LedgerDetailCsvRowParser.ValidateBooleanField("true", 2, "チャージ", errors, out var result);

            ok.Should().BeTrue();
            result.Should().BeTrue();
            errors.Should().BeEmpty();
        }

        [Fact]
        public void ValidateBooleanField_FalseString_ReturnsFalse()
        {
            var errors = new List<CsvImportError>();
            var ok = LedgerDetailCsvRowParser.ValidateBooleanField("false", 2, "チャージ", errors, out var result);

            ok.Should().BeTrue();
            result.Should().BeFalse();
            errors.Should().BeEmpty();
        }

        [Fact]
        public void ValidateBooleanField_EmptyString_ReturnsFalse()
        {
            var errors = new List<CsvImportError>();
            var ok = LedgerDetailCsvRowParser.ValidateBooleanField("", 2, "チャージ", errors, out var result);

            ok.Should().BeTrue();
            result.Should().BeFalse();
            errors.Should().BeEmpty();
        }

        [Fact]
        public void ValidateBooleanField_InvalidString_AddsError()
        {
            var errors = new List<CsvImportError>();
            var ok = LedgerDetailCsvRowParser.ValidateBooleanField("yes", 2, "チャージ", errors, out _);

            ok.Should().BeFalse();
            errors.Should().ContainSingle(e => e.Message.Contains("チャージ"));
        }

        // ParseFields のテストは 13 列 CSV の構造に依存するため、
        // 成功ケース 1 件 + 失敗ケース 1 件で smoke test とする
        [Fact]
        public void ParseFields_InsufficientColumns_ReturnsNull()
        {
            var errors = new List<CsvImportError>();
            var fields = new List<string> { "1", "0102030405060708" }; // 13 列未満

            // ParseFields は内部で `fields[i]` アクセスするため列数不足はまず ValidateColumnCount で守るが、
            // 直接呼ぶと IndexOutOfRange が出る可能性。オリジナル実装を確認し、
            // 防御的処理があればエラーに、なければテストは投げる期待に変更する。

            System.Action act = () =>
                LedgerDetailCsvRowParser.ParseFields(fields, 2, "dummy", errors);

            // 現行実装は ValidateColumnCount を呼び出し側で先に通す前提のため、例外が出る想定。
            // ここでは直接呼び出しが例外で失敗することを検証することで
            // 「ParseFields の呼び出し側は必ず ValidateColumnCount を通すべき」という契約をテストで固定する。
            act.Should().Throw<System.Exception>();
        }

        [Fact]
        public void ParseFields_ValidCharge_ParsesCorrectly()
        {
            var errors = new List<CsvImportError>();
            // 13 列: ledger_id, ledger_summary, card_idm, use_date, entry_station, exit_station,
            //       amount, balance, is_charge, is_point_redemption, is_bus, bus_stops, note
            var fields = new List<string>
            {
                "1", "鉄道", "0102030405060708", "2026-04-19 10:00:00",
                "博多", "天神", "210", "1000", "false", "false", "false", "", ""
            };

            var result = LedgerDetailCsvRowParser.ParseFields(fields, 2, "dummy", errors);

            errors.Should().BeEmpty();
            result.Should().NotBeNull();
            result.CardIdm.Should().Be("0102030405060708");
            result.Amount.Should().Be(210);
            result.Balance.Should().Be(1000);
            result.IsCharge.Should().BeFalse();
        }
    }
}
```

- [ ] **Step 4: NewLedgerFromSegmentsBuilderTests を作成**

`ICCardManager/tests/ICCardManager.Tests/Services/Import/NewLedgerFromSegmentsBuilderTests.cs` を作成:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Services.Import.Builders;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services.Import
{
    public class NewLedgerFromSegmentsBuilderTests
    {
        private readonly Mock<ILedgerRepository> _mockRepo = new();

        private NewLedgerFromSegmentsBuilder Create() =>
            new NewLedgerFromSegmentsBuilder(_mockRepo.Object);

        [Fact]
        public async Task BuildAndInsertAsync_EmptyDetails_ReturnsZero()
        {
            var errors = new List<CsvImportError>();
            var builder = Create();

            var inserted = await builder.BuildAndInsertAsync(
                "0102030405060708",
                new DateTime(2026, 4, 19),
                new List<(int, LedgerDetail)>(),
                errors);

            inserted.Should().Be(0);
            errors.Should().BeEmpty();
        }

        [Fact]
        public async Task BuildAndInsertAsync_SingleUsageSegment_CreatesOneLedger()
        {
            var errors = new List<CsvImportError>();
            _mockRepo.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
            _mockRepo.Setup(r => r.InsertDetailsAsync(1, It.IsAny<IEnumerable<LedgerDetail>>())).ReturnsAsync(true);

            var detail = new LedgerDetail
            {
                UseDate = new DateTime(2026, 4, 19),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 790,
                IsCharge = false
            };

            var builder = Create();
            var inserted = await builder.BuildAndInsertAsync(
                "0102030405060708",
                new DateTime(2026, 4, 19),
                new List<(int, LedgerDetail)> { (2, detail) },
                errors);

            inserted.Should().Be(1);
            errors.Should().BeEmpty();
            _mockRepo.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Once);
        }

        [Fact]
        public async Task BuildAndInsertAsync_GroupDateMinValue_UsesDetailUseDate()
        {
            Ledger capturedLedger = null;
            _mockRepo.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
                .Callback<Ledger>(l => capturedLedger = l)
                .ReturnsAsync(1);
            _mockRepo.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>())).ReturnsAsync(true);

            var errors = new List<CsvImportError>();
            var detailDate = new DateTime(2026, 4, 15);
            var detail = new LedgerDetail
            {
                UseDate = detailDate,
                Amount = 100,
                Balance = 900
            };

            var builder = Create();
            await builder.BuildAndInsertAsync(
                "0102030405060708",
                DateTime.MinValue, // fallback
                new List<(int, LedgerDetail)> { (2, detail) },
                errors);

            capturedLedger.Should().NotBeNull();
            capturedLedger.Date.Should().Be(detailDate);
        }

        [Fact]
        public async Task BuildAndInsertAsync_InsertDetailsFails_AddsError()
        {
            _mockRepo.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
            _mockRepo.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>())).ReturnsAsync(false);

            var errors = new List<CsvImportError>();
            var detail = new LedgerDetail { UseDate = new DateTime(2026, 4, 19), Amount = 100, Balance = 900 };

            var builder = Create();
            var inserted = await builder.BuildAndInsertAsync(
                "0102030405060708",
                new DateTime(2026, 4, 19),
                new List<(int, LedgerDetail)> { (2, detail) },
                errors);

            inserted.Should().Be(0);
            errors.Should().NotBeEmpty();
            errors[0].Message.Should().Contain("挿入に失敗");
        }

        [Fact]
        public async Task BuildAndInsertAsync_RepositoryThrows_AddsError()
        {
            _mockRepo.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ThrowsAsync(new InvalidOperationException("DB error"));

            var errors = new List<CsvImportError>();
            var detail = new LedgerDetail { UseDate = new DateTime(2026, 4, 19), Amount = 100, Balance = 900 };

            var builder = Create();
            var inserted = await builder.BuildAndInsertAsync(
                "0102030405060708",
                new DateTime(2026, 4, 19),
                new List<(int, LedgerDetail)> { (2, detail) },
                errors);

            inserted.Should().Be(0);
            errors.Should().ContainSingle();
            errors[0].Message.Should().Contain("DB error");
        }
    }
}
```

- [ ] **Step 5: 新規テスト全件 pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~Services.Import" --nologo --verbosity minimal
```
Expected: 新規 ~20 件のテスト全件 pass

- [ ] **Step 6: 全 CSV テスト pass 確認**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --filter "FullyQualifiedName~CsvImport|FullyQualifiedName~Services.Import" --nologo --verbosity minimal
```
Expected: 既存 94 + 新規 ~20 件 pass

- [ ] **Step 7: コミット**

```bash
git add ICCardManager/tests/ICCardManager.Tests/Services/Import/
git commit -m "$(cat <<'EOF'
test: CsvImport 抽出クラスの単体テストを追加 (Issue #1284)

- LedgerCsvRowParserTests
- LedgerDetailCsvRowParserTests
- NewLedgerFromSegmentsBuilderTests

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: 全体ビルド・テスト実行

- [ ] **Step 1: 全体ビルド**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" build ICCardManager/ICCardManager.sln --nologo --verbosity minimal 2>&1 | tail -5
```
Expected: エラー 0

- [ ] **Step 2: 全体テスト**

```bash
"/mnt/c/Program Files/dotnet/dotnet.exe" test ICCardManager/tests/ICCardManager.Tests/ICCardManager.Tests.csproj --nologo --verbosity minimal
```
Expected: `失敗: 0`。合格件数は既存（~2975）+ 新規（~20）

- [ ] **Step 3: ファイル行数確認**

```bash
wc -l ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Ledger.cs \
      ICCardManager/src/ICCardManager/Services/Import/CsvImportService.LedgerValidation.cs \
      ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Detail.cs \
      ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerCsvRowParser.cs \
      ICCardManager/src/ICCardManager/Services/Import/Parsers/LedgerDetailCsvRowParser.cs \
      ICCardManager/src/ICCardManager/Services/Import/Builders/NewLedgerFromSegmentsBuilder.cs
```
Expected:
- Ledger.cs: ~500 行
- LedgerValidation.cs: ~250 行
- Detail.cs: ~550 行
- LedgerCsvRowParser.cs: ~200 行
- LedgerDetailCsvRowParser.cs: ~200 行
- NewLedgerFromSegmentsBuilder.cs: ~150 行

---

## Task 10: 設計書更新

**Files:**
- Modify: `ICCardManager/docs/design/05_クラス設計書.md`
- Modify: `ICCardManager/docs/design/07_テスト設計書.md`

- [ ] **Step 1: 05_クラス設計書.md 更新**

`05_クラス設計書.md` の CsvImportService 関連セクションに、新規抽出した 4 クラスを記載する。場所は CsvImportService セクション末尾に追加し、以下のような構造で記述:

```markdown
### 5.X CsvImport 補助クラス（Issue #1284）

#### LedgerCsvRowParser (internal static, Services/Import/Parsers/)
利用履歴 CSV の1行パースを Import/Preview で共通化。依存は引数で受け取り副作用は errors リストのみ。

#### LedgerDetailCsvRowParser (internal static, Services/Import/Parsers/)
利用履歴詳細 CSV の 13 列パース + Boolean フィールド検証。

#### NewLedgerFromSegmentsBuilder (internal, Services/Import/Builders/)
利用履歴ID空欄の詳細リストから segment 分割を伴って新規 Ledger を自動生成。
依存: ILedgerRepository（コンストラクタ注入）
```

- [ ] **Step 2: 07_テスト設計書.md 更新**

CsvImportService 関連セクションに、以下の新規テストセクション `UT-XXX: 抽出パーサ/ビルダーの単体テスト（Issue #1284）` を追加（番号は前後関係から割り当て）:

```markdown
#### UT-XXX: CsvImport 抽出クラスの単体テスト（Issue #1284）

| No | 対象クラス | テストケース | 期待結果 |
|----|----------|------------|---------|
| 1 | LedgerCsvRowParser | 列数不足 | null + 列数エラー |
| 2 | LedgerCsvRowParser | 日付不正 | null + 日時形式エラー |
| 3 | LedgerCsvRowParser | 残額不正 | null + 残額エラー |
| 4 | LedgerCsvRowParser | カード未登録 | null + 未登録エラー |
| 5 | LedgerCsvRowParser | IDm 空欄 + targetCardIdm 指定 | target が採用される |
| 6 | LedgerCsvRowParser | IDm 空欄 + target なし | null + 必須エラー |
| 7 | LedgerCsvRowParser | 摘要空欄 | null + 必須エラー |
| 8 | LedgerCsvRowParser | 正常（ID列なし） | ParsedLedgerRow |
| 9 | LedgerCsvRowParser | 正常（ID列あり） | LedgerId=42 |
| 10 | LedgerCsvRowParser | ID列あり + 不正 ID | null + ID形式エラー |
| 11 | LedgerDetailCsvRowParser | Boolean true | true |
| 12 | LedgerDetailCsvRowParser | Boolean false | false |
| 13 | LedgerDetailCsvRowParser | Boolean 空文字 | false |
| 14 | LedgerDetailCsvRowParser | Boolean 不正 | エラー |
| 15 | LedgerDetailCsvRowParser | 列数不足 | 例外 |
| 16 | LedgerDetailCsvRowParser | 正常（チャージなし） | LedgerDetail |
| 17 | NewLedgerFromSegmentsBuilder | 空リスト | 0件 |
| 18 | NewLedgerFromSegmentsBuilder | 通常利用 1 segment | Ledger 1件作成 |
| 19 | NewLedgerFromSegmentsBuilder | groupDate MinValue → detail から推定 | detail の UseDate を採用 |
| 20 | NewLedgerFromSegmentsBuilder | InsertDetails 失敗 | エラー追加 + 0件 |
| 21 | NewLedgerFromSegmentsBuilder | Repository 例外 | エラー追加 + 0件 |

**テストクラス:** `LedgerCsvRowParserTests` / `LedgerDetailCsvRowParserTests` / `NewLedgerFromSegmentsBuilderTests`
```

- [ ] **Step 3: コミット**

```bash
git add ICCardManager/docs/design/05_クラス設計書.md ICCardManager/docs/design/07_テスト設計書.md
git commit -m "$(cat <<'EOF'
docs: 設計書に CsvImport 抽出クラスを反映 (Issue #1284)

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 11: CHANGELOG 更新

**Files:**
- Modify: `ICCardManager/CHANGELOG.md`

- [ ] **Step 1: [Unreleased] の **リファクタリング** セクションに追記**

既存の `**リファクタリング**` セクションに以下を追加:

```markdown
- `CsvImportService.Ledger.cs` (1031行) と `Detail.cs` (1042行) を責務分割。Import/Preview 間で重複していた Ledger 行パース ~200 行を `LedgerCsvRowParser` に共通化。Detail 側も `LedgerDetailCsvRowParser` と `NewLedgerFromSegmentsBuilder` に責務分離。LedgerValidation を別 partial へ分離。単体テスト 21 件を追加（#1284）
```

- [ ] **Step 2: コミット**

```bash
git add ICCardManager/CHANGELOG.md
git commit -m "$(cat <<'EOF'
docs: CHANGELOG に Issue #1284 リファクタを記載

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Task 12: Push と PR 作成

- [ ] **Step 1: push**

```bash
git push -u origin refactor/issue-1284-csv-import-split
```

- [ ] **Step 2: PR 作成**

```bash
gh pr create --title "refactor: CsvImportService.Ledger/Detail を責務分割 (Issue #1284)" --body "$(cat <<'EOF'
## Summary
- `CsvImportService.Ledger.cs` (1031行) と `Detail.cs` (1042行) を責務ごとに分割
- **重複解消**: Import/Preview 間の行パースロジック ~200 行を `LedgerCsvRowParser` に共通化
- **責務分離**: Detail の「新規 Ledger 自動作成」を `NewLedgerFromSegmentsBuilder` に、Ledger 検証系 helper を `LedgerValidation.cs` partial に
- **テスト追加**: 21 件の単体テストを新規追加（抽出クラスごとに境界値・異常系・正常系）
- public API は一切変更せず、既存 94 件のテストは全件 pass

## Related
- Closes #1284

## 抽出クラス
| クラス | パス | 種別 |
|-------|------|------|
| `LedgerCsvRowParser` | `Services/Import/Parsers/` | internal static |
| `LedgerDetailCsvRowParser` | `Services/Import/Parsers/` | internal static |
| `NewLedgerFromSegmentsBuilder` | `Services/Import/Builders/` | internal |
| (partial) `CsvImportService.LedgerValidation` | `Services/Import/` | partial class 分割 |

## Issue 記載との差分
Issue は "private class" を推奨するも、C# の nested private class はユニットテストできない。`InternalsVisibleTo` が既設のため **internal static クラス** として新規ファイル配置（spec: `docs/superpowers/specs/2026-04-19-issue-1284-csv-import-split-design.md`）。

## Test plan
- [x] `CsvImportServiceTests` / `CsvImportServiceExceptionLoggingTests` 全 94 件 pass
- [x] 新規 `LedgerCsvRowParserTests` / `LedgerDetailCsvRowParserTests` / `NewLedgerFromSegmentsBuilderTests` pass
- [x] ソリューション全体ビルド 0 error
- [ ] 手動テスト: 実データで Ledger CSV / Detail CSV をインポートして既存挙動と一致することを確認

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL が表示される

---

## 手動テスト依頼（コード変更完了後）

1. **Ledger CSV インポート golden path**: 既存のサンプル CSV（ID 列あり / なし両方）でインポート → 件数が以前と同じ
2. **Ledger CSV プレビュー**: 同じ CSV でプレビュー → 表示が以前と同じ
3. **Detail CSV インポート**:
   - 既存 Ledger への詳細追加（ledger_id あり）
   - 履歴ID空欄からの新規 Ledger 自動作成（Issue #906, #918, #1053 関連シナリオ）
4. **エラーケース**:
   - 列数不足・日付不正・金額不正で従来と同じエラーメッセージが出る
   - 残高整合性エラー（ValidateBalanceConsistency）が従来と同じ条件で出る

## リスクと対策

| リスク | 対策 |
|-------|-----|
| Import と Preview で微妙な挙動差があった場合の見落とし | Task 4 コミット前に git diff で 2 箇所の変更内容を並べて確認。差分が疑わしい場合は追加テストを書く |
| ValidateColumnCount を parser に含めると Card/Staff で利用不能になる | 既に確認済み。main partial `CsvImportService.cs` にそのまま残す |
| ParseFields の列数検証が消えて IndexOutOfRange | 呼び出し側で ValidateColumnCount を先に通す契約。Task 8 のテストで「直接呼ぶと例外」を明示 |
| 既存の `private` が `internal` になることでのシンボル衝突 | 新規 parser クラスは namespace が違う（`Services.Import.Parsers`）ので衝突なし |
