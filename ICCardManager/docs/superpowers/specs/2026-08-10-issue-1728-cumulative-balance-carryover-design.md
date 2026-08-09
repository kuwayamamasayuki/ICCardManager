# Issue #1728: 年度内に台帳が1件もないカードの累計残額が0円になる

**作成日**: 2026-08-10
**対象 Issue**: #1728（priority: high / type: bug / area: service）
**対象ファイル**: `ICCardManager/src/ICCardManager/Services/ReportDataBuilder.cs`

## 1. 問題

`ReportDataBuilder.BuildAsync` は年度累計の残額を次の式で決めていた。

```csharp
var monthEndBalance = ledgers.LastOrDefault()?.Balance ?? 0;          // 当月台帳の末尾残高
var currentBalance = yearlyLedgers.LastOrDefault()?.Balance ?? monthEndBalance;
```

`yearlyLedgers` は年度開始（4月1日）から当月末までの範囲、`ledgers` は当月のみ。**前者は後者を包含する**ため、`yearlyLedgers` が空なら `ledgers` も必ず空であり、`monthEndBalance` は常に 0 になる。つまりこのフォールバックは一度も機能せず、実質 `?? 0` と等価だった。

前年度繰越（`precedingBalance` / `fiscalYearCarryoverIncome`）へは落ちないため、年度内に一度も使われていないカードで次の 2 か所が 0 円になる。

| 波及先 | 症状 |
|--------|------|
| 5月以降の `cumulativeTotal.Balance` | 累計行が「受入 7,500 / 払出 0 / 残額 0」となり、同一帳票内の繰越行「5月より繰越 残額 7,500」と矛盾する |
| 3月の `carryoverToNextYear` | 「次年度へ繰越」が 0 円で出力され、翌年度4月の「前年度より繰越」（DB から直接取得＝7,500）と一致しない |

`ReportPreflightChecker.AddTotalMismatchWarning` は `受入 − 払出 = 残額` を機械検証するため、この状態では毎月 `TotalMismatch` 警告が出る。しかし原因は台帳データではなく集計側にあり、案内どおり履歴画面を見ても直せないため誤警告として常態化する。

### 実装漏れである根拠

4月だけは同じ欠損に対して明示的なガードが入っている。

```csharp
var aprilBalance = yearlyLedgers.Any() ? currentBalance : (precedingBalance ?? 0);
```

同じ考慮が 5月以降と3月に入っていないため、意図的な設計ではなく実装漏れと判断する。テストも4月版（`BuildAsync_April_NoLedgers_MonthlyTotal_IncomeEqualsBalance`）しか存在しない。

また `.claude/rules/business-logic.md` と `docs/design/04_機能設計書.md` は「紙の出納簿様式での『受入 − 払出 = 残額』が月計・累計のいずれでも成立すること」を要件として明記しており、本挙動はこれに反する。

## 2. 故障シナリオ

前年度末残高 7,500 円のまま当年度に一度も使われていない遊休カード（複数職員でシェアする運用では常時発生する）について、2025年6月の帳票を作成する。

1. `GetPreviousMonthBalanceAsync` が5月→4月と遡って空 → `GetCarryoverBalanceAsync(2024)` = 7,500 を返す
2. 繰越行は「5月より繰越 残額 7,500」と印字される
3. `GetByDateRangeAsync(4/1〜6/30)` が空 → `currentBalance` = 0
4. 累計行は「受入 7,500 / 払出 0 / 残額 0」と印字される
5. 同一帳票内で残高が矛盾し、プリフライトが毎月 `TotalMismatch` を出す
6. 翌年3月の帳票では「次年度へ繰越 0 円」となり、翌年度4月の「前年度より繰越 7,500 円」と食い違って年度間の繰越チェーンが切れる

## 3. 修正方針

### 3.1 フォールバック先を前年度繰越にする

```csharp
// Issue #1728: 年度内に台帳が1件もない遊休カードでは前年度繰越へフォールバックする。
var currentBalance = yearlyLedgers.LastOrDefault()?.Balance
    ?? precedingBalance
    ?? fiscalYearCarryoverIncome;
```

- `precedingBalance` は 4月なら `GetCarryoverBalanceAsync(year - 1)`、5月以降は `GetPreviousMonthBalanceAsync`（年度内が空なら同じ前年度繰越へフォールバックする）
- `precedingBalance` が `null` になるのは前年度繰越も存在しないケース（新規購入カード等）で、そのとき `fiscalYearCarryoverIncome` は 0 になる
- 使われなくなる `monthEndBalance` 変数は削除する

### 3.2 4月専用ガードを `currentBalance` に統合する

修正後、`aprilBalance` は `currentBalance` と等価になる。

| 条件 | `currentBalance`（修正後） | `aprilBalance`（現行） |
|------|---------------------------|----------------------|
| 4月・年度内台帳あり | `yearlyLedgers.Last().Balance` | 同左 |
| 4月・台帳なし・前年度繰越あり | `precedingBalance` | `precedingBalance` |
| 4月・台帳なし・前年度繰越なし | `fiscalYearCarryoverIncome` = `precedingBalance ?? 0` = 0 | 0 |

よって `aprilBalance` を削除し `monthlyTotal.Balance = currentBalance` とする。**4月の振る舞いは不変**であり、既存テストの修正は不要。

統合する理由は「同じ考慮が 2 か所に分かれていること」が本 Issue の再発原因そのものだから。片方だけ直す事故を構造的に防ぐ。

### 3.3 波及範囲

`ReportService`（Excel 出力）・`PrintService`（印刷プレビュー）・`ReportPreflightChecker` はいずれも `MonthlyReportData` を無加工で消費するため、3 経路すべてが同時に修正される。DB スキーマ変更・マイグレーションは不要。

## 4. スコープ外

紙の出納簿から年度途中で移行したカード（Issue #1215）で年度内が無利用の場合、5月以降の累計は `受入 = 前年度繰越 + CarryoverIncomeTotal` / `払出 = CarryoverExpenseTotal` / `残額 = 前年度繰越` となり等式は成立しない。これは「紙時代の累計を年度累計に含める」という Issue #1215 の設計上の帰結であり、年度内に台帳がある場合も同様に成立しない。本 Issue では扱わない。

## 5. テスト方針

`ReportDataBuilderTests` に追加する。**既存テストは変更しない。**

既存の `SetupBasicMonth` ヘルパーは「前月に台帳あり／年度範囲は空」という実 DB では生じ得ないモック構成のため流用しない。年度開始月から当月まで全ての `GetByMonthAsync` が空を返し、`GetByDateRangeAsync` も空を返す整合したヘルパー `SetupIdleFiscalYear` を新設する（`GetPreviousMonthBalanceAsync` が 4月まで 1 ヶ月ずつ遡るため、途中月の設定漏れがあるとテストが実挙動から乖離する）。

| # | テスト | 検証内容 |
|---|--------|---------|
| 1 | 6月・年度内無利用・前年度繰越 7,500 | `CumulativeTotal` が 受入 7,500 / 払出 0 / 残額 7,500 で、`Income − Expense == Balance` |
| 2 | 3月・年度内無利用・前年度繰越 7,500 | `CarryoverToNextYear == 7500` |
| 3 | 3月 → 翌年度4月 | 3月の `CarryoverToNextYear` と4月の `Carryover.Income` が一致（年度間チェーンの連続性） |
| 4 | 6月・前年度繰越も存在しない | `CumulativeTotal.Balance == 0`（新規カードの回帰防止） |
| 5 | 12月・年度途中まで利用があり以降空白 | `CumulativeTotal.Balance` が最後の利用月の残高（既存挙動の固定。年度内に台帳があるケースを壊していないこと） |

加えて `ReportPreflightCheckerTests` に 1 件、同条件（年度内無利用の遊休カード）で `TotalMismatch` 警告が発生しないことを表明する。誤警告の解消が本 Issue の実質的な受益であり、`ReportDataBuilder` 側の値だけを見ても表現できないため。

## 6. ドキュメント更新

| ファイル | 内容 |
|---------|------|
| `ICCardManager/CHANGELOG.md` | `### Unreleased` に修正内容を追記 |
| `docs/design/04_機能設計書.md` | 累計残額の算出規則に「年度内に台帳がない場合は前年度繰越」を明記 |
| `docs/design/07_テスト設計書.md` | `ReportDataBuilderTests` の一覧・件数、§1.1a の総件数を同期 |
| `.claude/rules/business-logic.md` | 「ledger を集計するときの前提」に、年度内無利用でも残高チェーンは前年度から続くという知見を追記 |
