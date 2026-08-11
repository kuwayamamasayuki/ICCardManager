# Issue #1740 設計: Edit モードの残高自動計算が前行を無視する問題

- 作成日: 2026-08-11
- 対象 Issue: #1740（medium / data-integrity）
- 対象クラス: `LedgerRowEditViewModel` / `MainViewModel` / `LedgerRowEditDialog.xaml`

## 1. 問題

履歴行編集ダイアログ（`LedgerRowEditDialog`）の「自動計算」チェックボックスを **Edit モードで ON にすると、残高が `0 + 受入 - 払出` で再計算され、DB の正しい残高を破壊する**。

```csharp
// LedgerRowEditViewModel.RecalculateBalance()（修正前）
if (!IsAutoBalance) return;

if (Mode == LedgerRowEditMode.Add)
{
    // ここでしか PreviousBalance を設定していない
    PreviousBalance = (InsertIndex > 0 && InsertIndex <= _allLedgers.Count)
        ? _allLedgers[InsertIndex - 1].Balance : 0;
}

Balance = PreviousBalance + Income - Expense;   // ← 式は全モード共通
```

`Mode == Add` のガードが囲っているのは **`PreviousBalance` の供給だけ**で、計算式は全モード共通。Edit モードには前行残高の供給経路が存在せず、`PreviousBalance` は `[ObservableProperty]` の既定値 0 のまま使われる。

チェックボックスにはモードによる無効化が無く（`LedgerRowEditDialog.xaml`）、ToolTip は「前行の残高 + 受入 - 払出 で自動計算されます」と全モード共通で説明している。

### 現状が「一見正常に見える」理由

`IsAutoBalance` の既定値は `true`。`InitializeForEditAsync` は `Income` / `Expense` を代入した時点で `OnIncomeChanged` → `RecalculateBalance()` を発火させており、**初期化中にも既に誤った再計算が走っている**。その直後の `Balance = ledger.Balance`（DB 値の復元）が結果を打ち消し、さらに `IsAutoBalance = false` が立つため、初期表示だけは正しい。偶然による無害化であり、設計として意図されたものではない。

### 影響

チャージ行（受入 3,000 円・払出 0 円・残高 5,000 円・前行残高 2,000 円）を修正で開き、金額を直したあとチェックを入れると残高が 3,000 円になる。負値ではないためバリデーションを通過し保存され、以降の行と残高チェーンが合わなくなって物品出納簿の残額欄が狂う。事後に `LedgerConsistencyChecker` が不整合を警告するが自動修正はしないため、破損値は DB に残る。

## 2. 方針

**修正の本体は「Edit モードにも前行残高の供給経路を作る」こと**であり、計算式やガードを増やすことではない。前行が特定できない場合は自動計算を無効化して手入力に倒す（fail-safe）。

Issue に併記されていた「Edit モードでは自動計算チェックボックスを無効化する」案は採らない。金額の誤りを直すのは Edit モードの中心的な用途であり、そこで残高を利用者に手計算させるのは機能の後退になるため。

## 3. 変更内容

### 3.1 `LedgerRowEditViewModel`

| 変更 | 内容 |
|------|------|
| `InitializeForEditAsync` に第3引数 `int? previousBalance = null` を追加 | 値あり → `PreviousBalance` を設定し `CanAutoBalance = true`。`null` → `CanAutoBalance = false` |
| 新規 `[ObservableProperty] bool _canAutoBalance = true` | 自動計算が使えるか。Add モードは常に `true` |
| 新規 `AutoBalanceToolTip`（読み取り専用） | チェックボックスの ToolTip。無効時は理由を説明する |
| `RecalculateBalance()` 冒頭を `if (!IsAutoBalance \|\| !CanAutoBalance) return;` に | 前行不明時は残高を書き換えない |
| `InitializeForEditAsync` の冒頭で `IsAutoBalance = false` を先に立てる | 初期化中の誤った再計算そのものを消す（偶然の無害化に依存しない） |

**第3引数の既定値を `null`（＝自動計算不可）とする理由**: 呼び出し元が渡し忘れたときに失われるのは「便利機能が使えること」だけで、残高破壊は起きない。fail-safe の向きに倒れる（Issue #1739 と同じ判断）。

**`PreviousBalance` を Edit モードで毎回再計算しない理由**: Add モードの `PreviousBalance` は挿入位置（`InsertIndex`）に追随して変わるが、Edit モードでは編集対象行が固定なので前行も固定。初期化時に一度設定すれば足りる。

### 3.2 `MainViewModel`

`EditLedgerWithAuthAsync` が表示順（`HistoryLedgers`）から前行残高を求めて渡す。算出は `internal int? FindPreviousBalanceForEdit(LedgerDto)` に切り出して単体テスト可能にする。

```csharp
internal int? FindPreviousBalanceForEdit(LedgerDto ledger)
{
    var index = HistoryLedgers.ToList().FindIndex(l => l.Id == ledger.Id);
    return index > 0 ? HistoryLedgers[index - 1].Balance : (int?)null;
}
```

`HistoryLedgers` を供給源にする根拠:

- Add モードの `PreviousBalance` も同じ `HistoryLedgers`（`InitializeForAddAsync` の `allLedgers`）から取っており、同一ダイアログ内で供給源が揃う
- `LedgerOrderHelper.ReorderByBalanceChain` で残高チェーン順に並べ替え済み。`ledger.date` は同一日内で時刻を持たず id 順が時系列と一致しない（Issue #1731）ため、日付や id から前行を引くのは誤りになりうる
- 1 ページ目の先頭には `BuildCarryoverRowAsync` の繰越行が挿入される（Issue #1155）ため、期間先頭の実データ行にも前行残高が供給される

`null` を返す（＝自動計算が使えない）のは次の場合:

- 編集対象がリストの先頭行（ページ 2 以降の先頭行、繰越行が無い期間の先頭行）
- 対象が `HistoryLedgers` に見つからない（`FindIndex` が -1）

### 3.3 `LedgerRowEditDialog.xaml`

「自動計算」CheckBox に `IsEnabled="{Binding CanAutoBalance}"` と `ToolTip="{Binding AutoBalanceToolTip}"` を追加する。レイアウトは変更しない。

無効時の ToolTip は「なぜ使えないか」と「どうすればよいか」を含める（`.claude/rules/error-messages.md`）。

## 4. テスト

### 4.1 `LedgerRowEditViewModelTests`（追加）

| No. | 検証内容 |
|-----|----------|
| 1 | Edit モードで前行残高を渡し自動計算 ON → `前行残高 + 受入 - 払出` になる（回帰の本丸） |
| 2 | Edit モードで前行残高を渡し、金額変更でも前行残高を起点に追随する |
| 3 | 前行残高 `null` → `CanAutoBalance` が false |
| 4 | 前行残高 `null` で `IsAutoBalance = true` にしても `Balance` が変化しない |
| 5 | Edit モード初期化直後、`Balance` が DB 値のままで `IsAutoBalance` が false |
| 6 | Add モードでは `CanAutoBalance` が true（既存挙動の不変） |

### 4.2 `MainViewModelTests`（追加）

| No. | 検証内容 |
|-----|----------|
| 1 | 先頭行 → `null` |
| 2 | 2 行目以降 → 直上行の `Balance` |
| 3 | 繰越行が先頭にある場合、その次の行は繰越行の残高を得る |
| 4 | 一覧に無い行 → `null` |

### 4.3 `LedgerRowEditDialogAutoBalanceLayoutTests`（新設・XAML 静的検証）

「自動計算」CheckBox に `IsEnabled` バインドと `ToolTip` バインドが存在すること。ViewModel のテストは XAML の結線漏れを検出できないため（`CardManageDialogStatusAreaLayoutTests` と同じ理由）。

## 5. ドキュメント更新

- `ICCardManager/docs/design/03_画面設計書.md` §3.12: 残高欄の説明と機能欄に「Edit モードでも前行残高から自動計算する」「前行が表示範囲外の場合は手入力」を追記
- `ICCardManager/docs/design/07_テスト設計書.md` §1.1a: 単体テスト件数の同期
- `ICCardManager/CHANGELOG.md` `### Unreleased` の「バグ修正」

## 6. スコープ外

- **編集によって後続行の残高チェーンが崩れる点**: 1 行の編集は本来後続行すべてに波及するが、本システムは自動補正せず `LedgerConsistencyChecker` の警告で運用に委ねる設計。本 Issue は「起点が 0 で計算される」誤りのみを対象とする。

## 7. コードレビューを受けた追加対応

当初「Add モードで挿入位置が先頭のとき `PreviousBalance = 0` になる点」をスコープ外としていたが、コードレビューで**同じ 0 起点の残高破壊が Add モードで到達可能**であること、および §3.1 の XML doc がそれと矛盾する記述（「Add モードは常に true」）になっていることが指摘されたため、本 PR に含めた。あわせて次を是正した。

| 対応 | 内容 |
|------|------|
| 自動計算の可否を動的化 | `AutoBalanceUnavailableReason`（`None` / `PreviousRowNotIdentified` / `EditDateChanged`）を導入し、挿入位置・利用日の変更で都度再判定する。使えなくなったらチェックも解除する |
| Edit モードの利用日変更 | 日付を変えると行の入る位置と直前行が変わるため自動計算を無効化する（元の日付へ戻せば再び使える） |
| Add モードの先頭挿入 | 一覧の先頭がカードの履歴の先頭でもあるときだけ 0 起点を許す。判定材料は `InitializeForAddAsync` の `historyStartsAtCardBeginning`（1ページ目かつ繰越行が無い＝表示期間より前に履歴が無い） |
| ON→OFF の復元 | 自動計算 ON の直前の残高を退避し、OFF で復元する |
| 残高チェーンのシード | 1ページ目では表示期間の直前残高を `ReorderByBalanceChain` のシードとして渡す。`GetPrecedingBalanceAsync` を `BuildCarryoverRowAsync` から切り出し、シードと繰越行で同じ値を使う |
| 繰越行のナビゲーションガード | 「戻る」「次へ」が Id=0 の合成繰越行を編集対象に選ばないようにする（`EditAdjacentLedgerAsync` に集約） |
| 初期化順序 | 対象行の取得結果（null チェック）を通ってから状態を書き換える。他 PC が削除済みの行で無関係なバリデーションエラーが出ないようにする |
| ToolTip 文言 | 画面に存在しない「表示期間を広げる」を案内しない。理由ごとに文言を分ける |
| ToolTip の折り返し | 要素構文へ変え `TextWrapping="Wrap"` と `MaxWidth` を指定。DataContext は継承に頼らず `PlacementTarget` 経由で解決する |
| テストヘルパーの重複 | 6 クラスに複製されていた `ResolveViewPath` を `Views/Helpers/ViewSourceLocator` へ集約 |
