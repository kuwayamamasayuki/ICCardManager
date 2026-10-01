# Issue #2159 設計: 履歴パネルを MainViewModel から HistoryPanelViewModel へ抽出する

- 作成日: 2026-10-01
- 対象 Issue: #2159（#2158 partial 分割の第 2 段）
- 対象クラス: `MainViewModel`（`Main/MainViewModel.History.cs` / `HistoryEdit.cs` / `HistoryMerge.cs` / `ReturnFlow.cs` の返却確認部分）→ 新規 `HistoryPanelViewModel`

## 1. 目的

#2158 の partial 分割はファイルを分けただけで、依存と状態は 1 クラスに残った。メイン画面の**履歴パネル**（期間・ページ送り・繰越行・残高不整合ハイライト・行の追加/変更/削除・統合/取り消し・返却確認）は、カードタッチの状態機械から独立性が高い。これを子 ViewModel へ抽出し、

- `MainViewModel` のコンストラクタ依存を減らす（25 → 21。`DbContext` / `IStaffAuthService` / `LedgerMergeService` / `OperationLogger` / `LedgerConsistencyChecker` が親から消え、`HistoryPanelViewModel` が 1 つ増える）
- 履歴のテストを、カードリーダー・タイマー・共有モード監視を組み立てずに書けるようにする

## 2. 境界 — 親と子が実際に跨ぐもの

Issue 起票時（2026-09-30）の確認では「跨ぐのは 3 点のみ」だったが、実コードを数え直すと次の 5 点だった。

| # | 子 → 親 / 親 → 子 | 現行コード | 抽出後 |
|---|---|---|---|
| 1 | 子 → 親 | 残高不整合警告（`BalanceInconsistency`）の生成・除去（`ReplaceWarnings`、#1739） | `IHistoryPanelHost.ReplaceBalanceInconsistencyWarning(cardIdm, warning)` |
| 2 | 子 → 親 | 履歴の追加・変更・削除・統合・明細編集の後のダッシュボード更新・警告再チェック（**起票時の確認から漏れていた**） | `IHistoryPanelHost.RefreshDashboardAsync()` / `CheckWarningsAsync()` |
| 3 | 子 → 親 | 読み込み中オーバーレイ（`MainWindow.xaml` の全面オーバーレイは**親の** `IsBusy` に束縛。**起票時の確認から漏れていた**） | `IHistoryPanelHost.BeginBusy(message)` |
| 4 | 子 → 親 | 貸出中レコードを削除して `is_lent` を戻したあとの貸出中一覧（**現行コードは再読込していなかった**） | `IHistoryPanelHost.RefreshLentCardsAsync()`（一覧の再読込・ダッシュボード更新の後ろで頼む） |
| 5 | 親 → 子 | カードタッチ・警告クリック・ダッシュボードからの履歴表示、返却確認（#1907）の開閉、各操作後の再読込 | 子の公開メソッド（§3） |

- カードタッチの操作者 `_currentStaffIdm` は跨がない（履歴編集は都度 `RequestAuthenticationAsync` で認証する）。起票時の確認どおり。
- 4 は既存の欠陥である。`DeleteLedgerRowCoreAsync` は `ic_card.is_lent` を false に戻すが、親の「貸出中」一覧（`LentCards`）を読み直していなかったため、次のカード操作・共有モードの定期更新まで、返却済みでもないのに「貸出中」と表示が残っていた。抽出で境界を明示したことで表面化したので、本 Issue で直す。

### 2.1 なぜ `IMessenger` ではなくホストのインターフェースにするか

Issue は「`IMessenger` で親へ要求を送るか、親のデリゲートを渡す」としていた。後者（デリゲートの束＝インターフェース）を採る。

- **順序が仕様になっている**。履歴削除は「一覧の再読込 → ダッシュボード → 警告 → 整合性 → 競合の案内」の順で、案内文が「一覧を再読み込みしました」と述べる以上、再読込の完了を待ってから案内する（#1753）。`IMessenger.Send` は同期の通知で、受け手の非同期処理の完了を待てない。
- **5 つの要求は 1 つの役割（履歴パネルを載せる画面）にまとまる**。デリゲートを個別に渡すと、1 つの配線漏れが「その操作だけダッシュボードが古い」形で潜在化する。インターフェースならコンパイラが実装漏れを止める。
- テストでは記録用のホスト（`RecordingHistoryPanelHost`）を差し込むだけで、親を組み立てずに「親へ何を要求したか」を表明できる。

### 2.2 警告の入れ替えの責任は親に残す

子は**警告の中身**（`WarningItem` の文言、#2007 の導入時残高の誤りの名指し）を組み立て、親は**入れ替えの規約**（種別ごとに自分の行だけを入れ替える、#1739）を持つ。`ReplaceBalanceInconsistencyWarning(cardIdm, null)` は「このカードの不整合警告を取り除く」。種別とカードで絞る述語を子へ渡す形（汎用の `ReplaceWarnings(selector, …)`）にすると、子が他の種別の警告を消せてしまうため、種別を固定したメソッドにする。

## 3. 子の公開面（親から呼ぶもの）

| メソッド | 呼び出し元 | 備考 |
|---|---|---|
| `ShowCardHistoryAsync(IcCard?)` | 待機中のカードタッチ・ダッシュボード・残額不足／残額の食い違い警告のクリック | ハイライトを消してから当月の履歴を開く（従来の `_balanceInconsistencies.Clear(); ShowHistoryAsync(card)` の組） |
| `ShowBalanceInconsistencyAsync(IcCard)` | 残高不整合警告のクリック | 全期間の判定から導入行の日付を決めて開き、ハイライトを適用（#2007） |
| `ShowReturnHistoryReviewAsync(card, result, settings)` | 返却後処理 | 返却確認の自動表示（#1907） |
| `CloseReturnHistoryReviewIfUntouched()` | 職員証タッチ | 操作されていない返却確認だけを閉じる |
| `IsHistoryVisible` / `LoadHistoryLedgersAsync(preserveCheckedRows)` | 貸出・返却・バス停名・同行者数・定期更新・インポート・バス停未入力の後 | 「開いていれば再読込」は呼び出し元に残す（従来どおり。チェックの引き継ぎは呼び出し元が明示する #1923） |
| `CheckAllCardsConsistencyAsync()` | データのインポート後 | 全カードの整合性判定 |

`MarkReturnHistoryReviewTouched()` と期間ポップアップの `HistoryOpenMonthSelector()` は View（`MainWindow.xaml.cs`）から直接呼ぶ。

## 4. View の束縛

- `MainViewModel` は `public HistoryPanelViewModel History { get; }` を公開する。
- `MainWindow.xaml` の履歴エリア（`HistoryArea_PreviewInput` を持つ `Border`）に `DataContext="{Binding History}"` を置き、内側の束縛は名前を変えずにそのまま子へ届く。
- 履歴エリアの外にある「使い方ガイド」（`IsHistoryVisible` の反転で表示）は `History.IsHistoryVisible` へ束縛し直す。
- `DataGrid` の行ボタンの `DataContext.ShowLedgerDetailCommand` / `DataContext.EditLedgerCommand`（`RelativeSource AncestorType=DataGrid`）は、`DataGrid` の `DataContext` が子になるので変更不要。
- `MainWindow.xaml.cs` の返却確認のスクロール（`IsReturnHistoryReview` の立ち上がり）は子の `PropertyChanged` を購読する。

## 5. 子のライフサイクル

- DI では `HistoryPanelViewModel` を `AddTransient` で登録し、`MainViewModel` のコンストラクタへ注入する。親はコンストラクタで `History.AttachHost(this)` を呼ぶ。
- ホスト未接続のまま親への要求が起きたら `InvalidOperationException` にする（黙って何もしないと、配線漏れが「ダッシュボードが古いまま」の形で潜在化する。#1820）。
- 子は `ObservableObject` を継承し、`ViewModelBase` を継承しない。継承すると子にも `IsBusy` が生え、誰も束縛していない 2 つ目の処理中状態ができる（子の `BeginBusy` を呼んでもオーバーレイは出ない）。処理中は必ずホスト経由で親へ寄せる。

## 6. ファイル配置

```
ViewModels/HistoryPanelViewModel.cs                         本体（プロパティ・表示・読込・期間・ページ・繰越行・明細）
ViewModels/HistoryPanel/HistoryPanelViewModel.Consistency.cs 残高整合性（#1052 / #1739 / #2007）
ViewModels/HistoryPanel/HistoryPanelViewModel.Edit.cs        行の追加・削除・変更（#635）
ViewModels/HistoryPanel/HistoryPanelViewModel.Merge.cs       統合・取り消し（#548）
ViewModels/HistoryPanel/HistoryPanelViewModel.ReturnReview.cs 返却確認（#1907）
ViewModels/IHistoryPanelHost.cs                              親への要求
```

撮影対応表（`screenshot-sources.json`）のメイン画面のエントリーへ、これらのファイルを併記する（履歴パネルの見た目を変える変更で撮り直しを検知するため）。

## 7. テスト

- `MainViewModelTests` の履歴 region（自動計算の起点 #1740・ハイライト #1052・導入時残高 #2007・矢印 #2030・全カード整合性 #1058・繰越行 #1155・削除フロー #1486/#1574・ページのクランプ #1814・削除の確認 #1837・削除の競合 #1944）を `HistoryPanelViewModelTests` へ移す。親の状態（`WarningMessages` 等）を見ていた表明は、記録用ホストへの要求の表明へ置き換える。
- 親のフロー（カードタッチ・返却後処理・定期更新・警告クリック）を通る表明は `MainViewModelTests` に残し、`viewModel.History.*` を見る。
- 親との連携を実配線で 2 件（Issue の指定）:
  1. 履歴の整合性判定で見つけた不整合の警告が、親の `WarningMessages` に届く（解消すれば消える）
  2. 貸出中レコードの削除で `is_lent` を戻したあと、親の貸出中一覧（`LentCards`）が再読込される
- 2 は対で「他に貸出中レコードが残っていて `is_lent` を戻さなかったときは再読込を要求しない」も置く（常に再読込する実装でも緑になるため）。
