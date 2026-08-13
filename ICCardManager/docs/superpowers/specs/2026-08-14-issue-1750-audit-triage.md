# Issue #1750 バグ監査（2026-08-07）未検証指摘 54 件の精査結果

**実施日**: 2026-08-14
**対象**: `BUG-AUDIT-2026-08-07.data.json` の `unverified` 54 件
**基点**: `main` の 359f436f（Issue #1749 / PR #1804 マージ後）

## 1. この文書の位置づけ

リポジトリ全体バグ監査（2026-08-07）は 93 件の一次指摘を挙げ、うち重大度上位 30 件だけが敵対的検証を通って 28 件が confirmed になった。残る 54 件は**未検証のまま**で、誤検出が混在し得る状態だった。

Issue #1750 はその 54 件を精査するトラッキング Issue で、手順を「該当コードを読み、故障シナリオが実行経路で成立するか確認 → 成立なら個別 Issue 化、棄却なら理由を付けて取り消し線」と定めていた。本書はその実施結果である。

**一次指摘のデータには `title` しか無い**（`description` / `scenario` は confirmed 側にしか含まれない）ため、精査は症状の記述だけを手がかりに実コードを読む形で行った。監査時点から行番号がずれている項目も多く、行番号ではなく症状でコードを特定している。

## 2. 結果の要約

| 判定 | 件数 | 意味 |
|------|------|------|
| CONFIRMED | 33 | 実行経路を確認し、故障シナリオが成立する |
| PARTIAL | 8 | 一部は成立するが、単独修正が危険か仕様判断が先 |
| REFUTED | 2 | 実行経路をたどると成立しない |
| ALREADY_FIXED | 3 | 監査後にマージされた Issue で対応済み |

起票した個別 Issue: **19 件（#1805〜#1823）**。同一原因のものは束ねている（54 件 → 19 Issue）。

### 束ねの判断基準

**同じ設計判断を 2 回することになる項目は 1 つの Issue にした。** 例えば「モーダル表示中もカード読み取りが生きている」は 3 つの一次指摘（未登録カードダイアログ、Error ハンドラの二重購読、職員登録ダイアログ）に分かれていたが、根はすべて「抑制スコープの取得と解放が処理範囲と一致していない」ことで、`IDisposable` な抑制スコープを 1 つ用意すれば同時に塞げる。別々に Issue 化すると 3 つの PR がそれぞれ独自の解を持ち込むことになる。

## 3. 起票した Issue

### 重大度 high

| Issue | 内容 | 元の一次指摘 |
|-------|------|-------------|
| #1805 | 返却のコミット後処理で例外が出ると「返却失敗」と誤表示され、再タッチで貸出として再記録される | `LendingService.cs:721` |

**一次指摘は medium だったが high に引き上げた。** 台帳には返却が記録済みなのに `Success = false` が返り、`LastProcessed*` も未設定のため 30 秒ルール（再タッチによる逆処理）が武装されない。案内どおり再タッチすると `is_lent = 0` のため**貸出として新規記録**され、カードは手元にないのにシステム上は「貸出中」になる。共有モードの一瞬の切断で成立する。

### 重大度 medium

| Issue | 内容 | 元の一次指摘 |
|-------|------|-------------|
| #1806 | 統合取り消しが rowid だけで明細を移動し、「取り消し済み」マークとも原子的でない | `LedgerRepository.cs:1394`, `:1417` |
| #1807 | モーダルダイアログ表示中もカード読み取りが有効で、ダイアログが多重に開く | `MainViewModel.cs:1363`, `:1345`, `StaffManageViewModel.cs:568` |
| #1808 | CSV インポートの無言欠陥 3 件 | `CsvImportService.Detail.cs:483`, `Staff.cs:401`, `Staff.cs:79` |
| #1809 | DbContext の接続ライフサイクル（使用中接続の Close+Dispose、PRAGMA 未適用接続の再利用） | `DbContext.cs:728`, `:618` |
| #1810 | 帳票の改ページ（旧改ページ残存でページ番号が飛ぶ、印刷プレビューで合計行が孤立） | `ReportService.cs:437`, `PrintService.cs:321` |
| #1811 | 警告メッセージ（カードリーダーエラーの無限蓄積、バス停名の類似警告が表示前に上書き） | `MainViewModel.cs:2356`, `BusStopInputViewModel.cs:359` |
| #1812 | 繰越月＝登録月のとき繰越レコードが 1 年前の日付になる | `SummaryGenerator.cs:1075` |
| #1813 | バックアップ保持世代 30 の前提が共有モードで破綻する | `AppConstants.cs:41` |
| #1814 | 履歴のページ数クランプ後に再読込しないため、空の一覧と件数表示が食い違う | `MainViewModel.cs:1470` |
| #1815 | 管理者ダッシュボードの「その他」系列が最上位系列と同色になる | `AdminDashboardViewModel.cs:533` |
| #1816 | カード登録の OnCardRead が無保護の fire-and-forget／「すべて統合」が自動検出化 | `CardManageViewModel.cs:832`, `LedgerDetailViewModel.cs:509` |
| #1817 | 生の `ex.Message` が UI へ出る箇所が 5 つ残っている | `LendingService.cs:1376` ほか 4 件 |

### 重大度 low

| Issue | 内容 | 元の一次指摘 |
|-------|------|-------------|
| #1818 | BusLabel / BusPlaceholder のハードコードが組織設定と乖離（計 9 箇所） | `LedgerMergeService.cs:483`, `WarningService.cs:86` |
| #1819 | 本番ログに残らない LogDebug/LogTrace が 3 箇所 | `FelicaCardReader.cs:709`, `StationMasterService.cs:126`, `LendingService.cs:840` |
| #1820 | 帳票の FileNameFormat が無視され、TemplateMapping は 18 項目中 5 項目しか使われていない | `ReportService.cs:697`, `:334` |
| #1821 | testing.md に反するテスト 3 件 | `DbContextCleanupTests.cs:195`, `ReportViewModelTests.cs:814`, `CardLockManagerTests.cs:487` |
| #1822 | 実装と食い違うドキュメント・規約外の記述・死にコード 4 件 | `LedgerDetail.cs:83`, `FelicalibIntegrityGuard.cs:28`, `LedgerDetailDialog.xaml:31`, `MainWindow.xaml:814` |
| #1823 | static Random の競合・ClearAllLocks の TOCTOU 残存・ConfigureAwait 未付与 | `DbContext.cs:162`/`:1228`, `CardLockManager.cs:216`, `CardRepository.cs:43`, `CacheService.cs:56`, `CsvExportService.cs:62` |

## 4. 棄却した 2 件

### `MainViewModel.cs:786` 共有モード定期リフレッシュ失敗の LogDebug

`.claude/rules/development-conventions.md` が Issue #1730 の節で **「MainViewModel の定期リフレッシュ失敗は切断中 15 秒ごとに出続け、かつ同じ切断を `LogConnectionCheckOutcome` が Information で既に記録しているため据え置き」** と明示的に判断済み。規約が対象外と宣言している箇所を一次指摘が拾ったもの。

### `MainWindow.xaml:814` ローカル値の Background が DataTrigger を無効化

WPF の依存関係プロパティ値の優先順位（ローカル値 > Style Trigger）という指摘自体は正しいが、**残額警告の色は別経路で正しく出る**。`CardBalanceDashboardItem.RowBackgroundResourceKey` が `IsBalanceWarning` に応じてリソースキーを返し、`ResourceKeyToBrushConverter` が同じブラシへ解決する（Issue #1461 の局所値方式）。

ただし Style 側に**恒久的に効かない Setter と DataTrigger が残っている**ため、死にコードとして #1822 に含めた。

## 5. 対応済みだった 3 件

監査（2026-08-07）以降にマージされた変更で解決していたもの。**一次指摘のスナップショットが古いために生じた見かけ上の指摘**であり、精査ではこの確認も必要になる。

| 一次指摘 | 対応 |
|---------|------|
| `LedgerOrderHelper.cs:127` 繰越判定のハードコード | Issue #1749 / PR #1804（`Ledger.IsCarryover` へ委譲） |
| `OperationLogSearchViewModel.cs:47` 操作ログの英語表示 | Issue #1787 / PR #1797（`OperationLogDisplayNames` へ一元化） |
| `WpfDispatcherService.cs:23` 内側 Task の破棄 | Issue #1725（`.Task.Unwrap()` ＋ `ObserveTask`） |

## 6. 精査で分かった一次指摘の傾向

今後同種の監査を行う際の参考として記録する。

- **プロジェクト固有の意思決定を知らないための誤検出がある。** 棄却した 2 件はいずれも「規約やコードコメントに判断が記録済み」だった。静的な観点だけで見ると違反に見えるが、なぜそうしているかが別の場所に書かれている
- **同じ欠陥が複数の指摘に分裂する。** 「モーダル中のカード読み取り」は 3 件に、「static Random」は 2 件に分かれていた。逆に **1 つの指摘が複数箇所を取りこぼす**こともあり、static Random の使用箇所は指摘の 2 箇所ではなく実際は 3 箇所だった
- **事実誤認を含む指摘がある。** 「`Data/Repositories` 全体で `ConfigureAwait(false)` が一箇所も付与されていない」は誤りで、実測では `LedgerRepository` 60/121、`SettingsRepository` 10/46、`OperationLogRepository` 6/18 が付与済み。未付与は 2 ファイルのみだった
- **重大度の再評価が必要。** 一次評価が medium だった `LendingService.cs:721` は、実行経路をたどると「返却済みのカードが貸出中として再記録される」という high 相当の実害があった。逆に low へ下げた項目もある
- **「単独では直せない」項目がある。** PARTIAL 8 件のうち 5 件は、修正の方向自体に仕様判断が必要だった（詳細は #1750 のコメント参照）

## 7. 関連

- Issue #1750（本トラッキング Issue）
- `BUG-AUDIT-2026-08-07.md` / `BUG-AUDIT-2026-08-07.data.json`（監査の生データ）
- Issue #1749 / #1787 / #1725（対応済みだった 3 件）
