# Issue #1688 帳票出力前プリフライトチェック 設計書

作成日: 2026-07-25

## 1. 背景と目的

月次の物品出納簿作成は管理者の最頻定例作業である。現状は帳票を出力してから Excel を開き、月計・繰越・負残高を目視確認する運用になっている（管理者マニュアル §5.6.6）。

出力後に問題へ気づくと「修正 → 再出力」の手戻りや、検収での差戻しが発生する。帳票を出力する**前**に機械的に検出し、警告として提示することでこの手戻りを削減する。

## 2. スコープ

### やること

- 帳票出力対象（選択カード × 対象年月）に対する5種別の事前検証
- 検証結果を一覧表示する専用ダイアログ
- 「作成」押下時の自動実行（続行/中止を選択可能）と、出力を伴わない手動チェックの2経路

### やらないこと（YAGNI）

- **警告行から履歴画面へのジャンプ**。`ReportDialog` はメイン画面からのモーダルであり、ジャンプにはダイアログを閉じて `MainViewModel` を操作する経路が必要になる。今回は警告行に「カード名・日付・摘要・金額」を明記し、ユーザーが自力で該当行を特定できる情報表示に留める。
- **出力の強制ブロック**。Issue の方針どおり、警告があっても出力自体は可能とする。
- 警告の永続化・履歴管理。プリフライトはその場限りの検査とする。

## 3. アーキテクチャ

```
ReportViewModel
  ├─ CreateReportAsync()        ← 冒頭でプリフライト実行（自動）
  └─ RunPreflightCheckAsync()   ← 「事前チェック」ボタン（手動）
        │
        ▼
  ReportPreflightChecker（新規 Service）
        ├─ IReportDataBuilder.BuildAsync()   → MonthlyReportData（帳票が実際に描画するデータ）
        └─ ILedgerRepository.GetAllLentRecordsAsync() → 貸出中レコード
        │
        ▼
  ReportPreflightResult { List<ReportPreflightWarning> }
        │
        ▼
  ReportPreflightViewModel → ReportPreflightDialog（DialogResult で続行/中止を返す）
```

### 3.1 設計判断: 検証対象は `MonthlyReportData`

DB を別クエリで再集計すると「帳票には出ているのにチェックは通る」という乖離が生まれる。`ReportDataBuilder.BuildAsync` が返す `MonthlyReportData` は、繰越行の合成・月計/累計の算出・`（貸出中）`レコードの除外をすべて済ませた**帳票の描画元そのもの**なので、これを検証対象とする。

例外は「未返却」検出のみ。`ReportDataBuilder` が `（貸出中）` レコードを除外する（`ReportDataBuilder.cs:54`）ため、`ILedgerRepository.GetAllLentRecordsAsync()` で別途取得する。

### 3.2 設計判断: 判定ロジックは `internal static` の純粋関数

`ReportPreflightChecker` の検出ロジックは `MonthlyReportData` と貸出中レコードのリストを引数に取る `internal static` メソッド群に切り出す。DB・モックなしで単体テストでき、境界値テストが書きやすい。

## 4. 検出ルール

`ReportPreflightIssueType` 列挙型で5種別を表す。

### 4.1 `UnreturnedAcrossMonth`（未返却のまま月をまたぐ）

**判定**: 対象カードの貸出中レコード（`Ledger.IsLentRecord == true`）が存在し、その `Date` **<** 対象月の初日。

**意味**: 前月以前に貸し出されたまま返却されていない。当該カードは対象月の帳票に一切の払出が計上されず、実際の残高と帳票の残額が乖離する。

### 4.2 `LendingRecordInMonth`（対象月内に未返却の貸出がある）

**判定**: 貸出中レコードの `Date` が対象月内（初日 ≦ Date ≦ 末日）。

**意味**: `ReportDataBuilder` が `（貸出中）` 摘要のレコードを帳票から除外する（設計上正しい）ため、当該レコードは帳票に現れない。返却してから出力すべき旨を通知する。

### 4.3 `NegativeBalance`（残額がマイナス）

**判定**: 以下のいずれかが `< 0`
- `MonthlyReportData.Ledgers[*].Balance`
- `MonthlyTotal.Balance`（値がある場合、すなわち4月）
- `CumulativeTotal.Balance`（値がある場合）

**意味**: 交通系ICカードの残高は物理的にマイナスにならないため、データ誤登録が確定している。

### 4.4 `CarryoverMismatch`（繰越額と前月末残高の不一致）

**判定**: 繰越行（`Carryover`）と明細行がともに存在するとき

```
Carryover.Balance + Ledgers[0].Income - Ledgers[0].Expense == Ledgers[0].Balance
```

が成立しなければ警告。

**意味**: 既存の `LedgerConsistencyChecker` は「最初の行は前行がないためスキップ」する（`LedgerConsistencyChecker.cs:71-73`）ため、月の先頭行と繰越行の接続だけが検証の空白地帯になっている。帳票では繰越行が描画されるので、ここを埋める。

**除外条件**: 対象月の先頭行が「○月から繰越」（`SummaryGenerator.IsMidYearCarryoverSummary`）の場合はスキップ。紙出納簿移行カード（Issue #510）では当該レコードが `Income = 残高` で保存されるため、通常の残高チェーン式が成立しない。

### 4.5 `TotalMismatch`（受入 − 払出 = 残額 の不成立）

`.claude/rules/business-logic.md`（Issue #1494）が定める不変条件の機械検証。

**累計行**（`CumulativeTotal != null`、すなわち5月以降）:

```
CumulativeTotal.Income - CumulativeTotal.Expense == CumulativeTotal.Balance
```

**月計行（4月）**（`MonthlyTotal.Balance` に値がある）:

```
MonthlyTotal.Income - MonthlyTotal.Expense == MonthlyTotal.Balance
```

**月計行（5月以降）**（`MonthlyTotal.Balance` が null のため残高チェーンで検算）:

```
PrecedingBalance + MonthlyTotal.Income - MonthlyTotal.Expense == 月末残高（Ledgers.Last().Balance）
```

**除外条件（5月以降の月計のみ）**:
- `PrecedingBalance` が null（新規購入カードで過去データなし）→ 検算不能のためスキップ
- 対象月に「○月から繰越」レコードが含まれる（紙出納簿移行月）→ 当該レコードの `Income` が集計から除外される一方で残高チェーンには寄与するため、式が成立しない。スキップする。
- 明細行が0件 → 月末残高が確定しないためスキップ

**誤検知しないことの確認（紙出納簿移行カード / 累計）**:

| 項目 | 値 |
|---|---|
| 紙時代の受入合計 / 払出合計 | 10,000 / 7,000（移行時残高 3,000） |
| DB の「6月から繰越」行 | Income=3,000, Balance=3,000（集計からは除外） |
| 7月のチャージ / 利用 | +1,000 / −500（残高 3,500） |

`yearlyIncome = 1,000 + 0 + 10,000 = 11,000`、`yearlyExpense = 500 + 7,000 = 7,500`、`currentBalance = 3,500`。
`11,000 − 7,500 = 3,500` で成立する（`ReportDataBuilder.cs:112-128`）。

## 5. データモデル

`ConsistencyResult` が `LedgerConsistencyChecker.cs` に同居する既存パターンに倣い、`ReportPreflightChecker.cs` 内に定義する。

```csharp
public enum ReportPreflightIssueType
{
    UnreturnedAcrossMonth,
    LendingRecordInMonth,
    NegativeBalance,
    CarryoverMismatch,
    TotalMismatch
}

public class ReportPreflightWarning
{
    public string CardIdm { get; set; }
    public string CardDisplayName { get; set; }   // 「はやかけん No.3」
    public ReportPreflightIssueType IssueType { get; set; }
    public DateTime? Date { get; set; }           // 該当行の利用日（行に紐づかない場合 null）
    public int? LedgerId { get; set; }            // 該当行の ID（合成行・合計行の場合 null）
    public string RowSummary { get; set; }        // 該当行の摘要 or 合計行ラベル
    public string DisplayText { get; set; }       // 一覧に出す1行サマリ
    public string DetailText { get; set; }        // 「なぜ」「どうすれば」を含む詳細
}

public class ReportPreflightResult
{
    public bool HasWarnings => Warnings.Count > 0;
    public List<ReportPreflightWarning> Warnings { get; } = new();
}
```

### 5.1 メッセージ品質

`.claude/rules/error-messages.md` の3要素（何が / なぜ / どうすれば）に準拠する。

| 種別 | DisplayText | DetailText |
|---|---|---|
| `UnreturnedAcrossMonth` | `⚠️ はやかけん No.3: 2026-06-28 から未返却のまま7月をまたいでいます` | `未返却のカードは払出が帳票に計上されないため、7月の残額が実際のカード残高と一致しません。カードを返却してから帳票を作成してください。` |
| `LendingRecordInMonth` | `⚠️ はやかけん No.3: 2026-07-15 の貸出が未返却です` | `「（貸出中）」の履歴は帳票に出力されないため、この利用分が7月の帳票から欠落します。カードを返却してから帳票を作成してください。` |
| `NegativeBalance` | `⚠️ nimoca No.1: 2026-07-15「鉄道（博多～天神）」の残額が -120円（マイナス）です` | `交通系ICカードの残額はマイナスになりません。履歴画面で該当行の受入金額・払出金額を修正してください。` |
| `CarryoverMismatch` | `⚠️ nimoca No.1: 繰越額 3,000円 と先頭行の残額が一致しません（期待 2,800円 / 実際 2,500円）` | `「6月から繰越」の残額に先頭行の受入・払出を加減した額が、先頭行の残額と一致しません。前月の帳票と突き合わせて該当行を修正してください。` |
| `TotalMismatch` | `⚠️ nimoca No.1:「累計」で 受入 − 払出 = 残額 が成立しません（受入 11,000円 − 払出 7,500円 ≠ 残額 3,400円）` | `帳票の検収では「受入 − 払出 = 残額」の成立が確認されます。履歴画面で残高の不整合を修正してください。` |

## 6. UI 設計

### 6.1 `ReportPreflightDialog`

- カードごとにグループ化した警告一覧（`ItemsControl` + `CollectionViewSource` グルーピング）
- 各行: 種別アイコン（⚠️）・日付・摘要・メッセージ。選択すると `DetailText` を下部に表示
- 背景色は `ErrorBackgroundBrush`（ヘッダー）/ `ReturnBackgroundBrush`（説明）を `DynamicResource` で参照（色値リテラル禁止、Issue #1392/#1461）
- ボタン:
  - 確認モード（作成フロー経由）: `[中止して修正する]`（`IsDefault`, `DialogResult=false`） / `[このまま作成する]`（`DialogResult=true`）
  - 参照モード（手動チェック経由）: `[閉じる]` のみ
- 警告0件のとき（手動チェックのみ到達）: 「問題は見つかりませんでした」を表示

### 6.2 `ReportDialog` の変更

操作ボタン行に `[事前チェック]` を追加（`[閉じる] [事前チェック] [プレビュー] [作成]`）。

### 6.3 `ReportViewModel` の変更

```csharp
[RelayCommand] public async Task RunPreflightCheckAsync()   // 参照モードで表示
public async Task CreateReportAsync()
{
    ... 既存バリデーション ...
    // プリフライト（既存ファイル上書き確認より前に実施）
    var preflight = await _preflightChecker.CheckAsync(cardIdms, SelectedYear, SelectedMonth);
    if (preflight.HasWarnings && !ShowPreflightDialog(preflight, confirmMode: true))
    {
        SetStatus("プリフライトチェックの警告により帳票作成を中止しました", false);
        return;
    }
    ... 既存の上書き確認・生成 ...
}
```

プリフライトを**上書き確認より前**に置くのは、中止する場合に不要なダイアログを見せないため。

### 6.4 テスタビリティ

`ReportViewModel` から WPF ダイアログを直接生成すると単体テストできない。既存の `INavigationService.ShowDialog<TDialog>(Action<TDialog>)` を経由し、テストではモックが `bool?` を返すようにする。

## 7. テスト計画

### 7.1 `ReportPreflightCheckerTests`（新規）

判定ロジック（`internal static`）に対する純粋関数テスト:

| # | テスト | 期待 |
|---|---|---|
| 1 | 前月から未返却の貸出中レコードあり | `UnreturnedAcrossMonth` 1件 |
| 2 | 対象月内の貸出中レコードあり | `LendingRecordInMonth` 1件 |
| 3 | 貸出中レコードが翌月以降 | 警告なし |
| 4 | 明細行に負残高 | `NegativeBalance` 1件（該当行の日付・摘要を含む） |
| 5 | 累計残額が負 | `NegativeBalance` 1件 |
| 6 | 残高すべて正 | 警告なし |
| 7 | 繰越行と先頭行が不整合 | `CarryoverMismatch` 1件（期待値・実際値を含む） |
| 8 | 繰越行と先頭行が整合 | 警告なし |
| 9 | 先頭行が「○月から繰越」（紙出納簿移行） | `CarryoverMismatch` を出さない |
| 10 | 累計で 受入−払出≠残額 | `TotalMismatch` 1件 |
| 11 | 4月月計で 受入−払出≠残額 | `TotalMismatch` 1件 |
| 12 | 5月以降の月計で 前月末残高+受入−払出≠月末残高 | `TotalMismatch` 1件 |
| 13 | `PrecedingBalance` が null | 月計の `TotalMismatch` を出さない |
| 14 | 「○月から繰越」を含む月 | 月計の `TotalMismatch` を出さない |
| 15 | 紙出納簿移行カードの累計（§4.5 の数値例） | 警告なし |
| 16 | 正常な通常カード（全項目） | 警告0件 |
| 17 | `CheckAsync` が複数カードを走査し警告を統合 | カード数分の警告を含む |
| 18 | `BuildAsync` が null（カード未検出） | 例外を投げずスキップ |
| 19 | 各警告のメッセージ品質（20文字以上・行動指示で終わる） | 3要素準拠 |

### 7.2 `ReportViewModelTests`（追加）

| # | テスト | 期待 |
|---|---|---|
| 20 | 警告ありでダイアログが `false` を返す | 帳票が作成されない・ステータスに中止が出る |
| 21 | 警告ありでダイアログが `true` を返す | 帳票作成が継続する |
| 22 | 警告なし | ダイアログを表示せず作成が継続する |
| 23 | `RunPreflightCheckCommand` 実行 | 参照モードでダイアログが表示される |

## 8. ドキュメント更新

| ファイル | 更新内容 |
|---|---|
| `docs/design/04_機能設計書.md` | §13 配下に「13.4 帳票出力前プリフライトチェック」を新設（検出5種別・判定式・除外条件） |
| `docs/design/07_テスト設計書.md` | 新規テストクラスの記載、§1.1a / §8.1 の件数同期 |
| `docs/manual/管理者マニュアル.md` | §5.6.6「帳票出力の確認」に事前チェックの操作手順と警告の読み方を追記 |
| `ICCardManager/CHANGELOG.md` | `### Unreleased` に追記 |

## 9. リスクと緩和

| リスク | 緩和策 |
|---|---|
| 紙出納簿移行カードでの誤検知 | §4.4 / §4.5 の除外条件を実装し、テスト #9・#14・#15 で固定する |
| カード数が多いとチェックが遅い | `BuildAsync` は帳票生成でも同じ回数走るため追加コストは概ね2倍以内。`BeginBusy` で進捗を表示する |
| 警告が多すぎて読めない | カードごとにグループ化し、種別アイコンで一目で分類できるようにする |
