# Issue #1370: カード/職員CSVインポートのプレビューが備考欄の変更を検出しない問題

- **Issue**: #1370 (カードデータインポート時に備考欄が無視される)
- **作成日**: 2026-04-20
- **対象ブランチ**: `fix/issue-1370-preview-note-detection`

## 背景

カードおよび職員のCSVインポート機能は以下の2段階で動作する:

1. **プレビュー**: `CsvImportService.PreviewCardsAsync` / `PreviewStaffAsync` が CSV を読み取り、既存データとの差分を検出して `CsvImportPreviewResult` を返す
2. **実インポート**: `ImportCardsAsync` / `ImportStaffAsync` が再度 CSV を読み、`skipExisting` オプションに基づいて挿入/更新/スキップを実施

`skipExisting=false`（更新モード）では、実インポートは CardType/CardNumber/Note（職員は Name/Number/Note）を全て上書きするが、プレビューは Note フィールドを差分検出から除外しているため、「備考のみ変更された行」が `ImportAction.Skip` として表示される。

## 問題

### 再現手順
1. カード管理画面でカードを1件登録（備考="旧備考"）
2. データエクスポート/インポート画面から「カード」をCSVエクスポート
3. CSV をエディタで開き、備考列のみを "新備考" に書き換える
4. 「既存データをスキップする」をOFFにしてインポートプレビューを表示
5. 期待: `ImportAction.Update` として表示され、変更点に「備考: 旧備考 → 新備考」が出る
6. 実際: `ImportAction.Skip` として表示される

### 根本原因
`CsvImportService.Card.cs` / `CsvImportService.Staff.cs` のプレビュー処理で:

- `PreviewCardsInternalAsync`（CsvImportService.Card.cs:284-286）は fields[3] の note を読み取っていない
- `DetectCardChanges`（同 383-408）は CardType と CardNumber のみ比較している
- `PreviewStaffInternalAsync`（CsvImportService.Staff.cs:287-289）と `DetectStaffChanges`（同 381-406）も同様

インポート本体側はすでに Note を読み書きしているため、プレビューと実処理の**セマンティック乖離**が発生している。

## 修正方針

### スコープ
カードと職員の両方を修正する（同一の根本原因で、対称な実装になっているため）。

### 変更箇所

#### 1. `src/ICCardManager/Services/Import/CsvImportService.Card.cs`

**PreviewCardsInternalAsync** に note 読み取りを追加:

```csharp
var cardIdm = fields[0].Trim().ToUpperInvariant();
var cardType = fields[1].Trim();
var cardNumber = fields[2].Trim();
// Issue #1370: プレビューでも備考の差分検出のため note を読み取る
var note = fields.Count > 3
    ? Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[3].Trim())
    : "";
```

**DetectCardChanges** に newNote 引数を追加し、Note を比較:

```csharp
private static void DetectCardChanges(
    IcCard existingCard,
    string newCardType,
    string newCardNumber,
    string newNote,
    List<FieldChange> changes)
{
    // ... 既存の CardType/CardNumber 比較 ...

    // Note の比較（空文字/null は同一扱いに正規化）
    var existingNote = string.IsNullOrWhiteSpace(existingCard.Note) ? null : existingCard.Note;
    var normalizedNewNote = string.IsNullOrWhiteSpace(newNote) ? null : newNote;
    if (existingNote != normalizedNewNote)
    {
        changes.Add(new FieldChange
        {
            FieldName = "備考",
            OldValue = existingNote ?? "(なし)",
            NewValue = normalizedNewNote ?? "(なし)"
        });
    }
}
```

呼び出し側（Restore 分岐 / Update 分岐）でも note を渡す。

#### 2. `src/ICCardManager/Services/Import/CsvImportService.Staff.cs`

カードと同様の変更を職員側にも適用:
- `PreviewStaffInternalAsync` に note 読み取りを追加
- `DetectStaffChanges` のシグネチャに `newNote` を追加し、Note 比較ロジックを同一仕様で実装

### Note 比較のセマンティクス

既存の保存ロジック（`ImportCardsInternalAsync` 108行目等）は `string.IsNullOrWhiteSpace(note) ? null : note` で正規化してから保存している。差分検出も同じ正規化を適用することで、`null` と `""` は同一扱いとする。

| 既存DB値 | CSV値 | 変更扱い？ |
|---------|-------|-----------|
| null | "" | ❌ 変更なし |
| null | "新" | ✅ 変更あり |
| "旧" | "旧" | ❌ 変更なし |
| "旧" | "新" | ✅ 変更あり |
| "旧" | "" | ✅ 変更あり（削除） |

`FieldChange` の表示では、null または空を `(なし)` と表示する（既存の CardNumber 比較パターンと同じ）。

### 後方互換性
- インポート本体（`ImportCardsInternalAsync` / `ImportStaffInternalAsync`）は変更しない。元々 Note を正しく読み書きしているため。
- プレビューAPIの返却型は変更しない。`CsvImportPreviewItem.Changes` のリストに備考の `FieldChange` が追加で現れるだけ。
- 既存のテストは Note 差分を検証していないため影響を受けない。

## テスト設計

### 新規テスト（`CsvImportServiceTests.cs` に追加）

| # | テストメソッド | 検証内容 |
|---|---------------|---------|
| 1 | `PreviewCardsAsync_NoteChanged_DetectsAsUpdate` | 既存カードの備考のみ異なる CSV をプレビュー → `ImportAction.Update`、`Changes` に "備考" の `FieldChange` を含む、updateCount が +1 |
| 2 | `PreviewCardsAsync_AllFieldsIdentical_DetectsAsSkip` | 既存カードと全フィールド同一の CSV をプレビュー → `ImportAction.Skip`、skipCount が +1 |
| 3 | `PreviewStaffAsync_NoteChanged_DetectsAsUpdate` | 職員版。備考のみ異なる → Update |
| 4 | `PreviewStaffAsync_AllFieldsIdentical_DetectsAsSkip` | 職員版。全フィールド同一 → Skip |

### 境界値ケース
テスト1は以下の代表ケースを1件で検証（シンプルな DB "旧" → CSV "新"）:
- 既存 Note が null、CSV 側に空文字 → 変更なし（追加テストとして確認）
- 既存 Note に値あり、CSV 側で空 → 変更あり（削除）

境界値が複雑になる場合は Theory パラメタライズドテストを検討。

### 既存テストへの影響
既存の `ImportCardsAsync_*` テストと `ImportStaffAsync_*` テストは Note 差分の検証を行っていないため、修正対象外。再実行してグリーンであることを確認。

## ドキュメント更新

- **`ICCardManager/CHANGELOG.md`**: `[Unreleased]` セクションの `Fixed` に記述
- **`docs/design/07_テスト設計書.md`**: 新規テスト4件の一覧を反映

仕様文書（`docs/design/04_機能設計書.md` 等）は「プレビューはすべての変更可能フィールドで差分検出」という挙動が正しく戻るだけなので更新不要。

## リスクと緩和策

| リスク | 緩和策 |
|-------|--------|
| Note 比較の null/空文字正規化がインポート本体と食い違う | 同じ正規化ロジック (`IsNullOrWhiteSpace(x) ? null : x`) を適用して両者を一致させる |
| 新規テストが環境依存で不安定になる | 既存テストと同じ `_testDirectory` + `File.WriteAllText` パターン、Mock 戦略を踏襲 |
| 備考欄に式インジェクション文字（`=+-@`）が含まれる | プレビューにも `FormulaInjectionSanitizer.Sanitize` を適用（インポート本体と同じ） |

## 検証計画

1. `"/mnt/c/Program Files/dotnet/dotnet.exe" build` で全プロジェクトビルドが成功
2. `"/mnt/c/Program Files/dotnet/dotnet.exe" test` で追加テスト4件を含む全テストがグリーン
3. PR を作成しCI（GitHub Actions）でグリーンを確認

## 参考

- Issue: #1370
- 関連ファイル:
  - `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Card.cs`
  - `ICCardManager/src/ICCardManager/Services/Import/CsvImportService.Staff.cs`
  - `ICCardManager/tests/ICCardManager.Tests/Services/CsvImportServiceTests.cs`
- 関連仕様: `.claude/rules/error-messages.md`（メッセージ品質基準）
