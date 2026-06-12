# 更新履歴

### Unreleased

**バグ修正**
- Issue #1616 文字サイズ設定（小/中/大/特大）に追従しない固定 FontSize と、特大文字で手詰まりになる固定幅ダイアログを修正。(a) `BusStopInputDialog` の入力例ヒント「入力例: ○○バス停～△△バス停」の `FontSize="11"` ハードコードを `DynamicResource SmallFontSize` に変更し、文字サイズ設定に追従するようにした。(b) `StaffAuthDialog` / `CardRegistrationModeDialog` / `CardTypeSelectionDialog` の `ResizeMode="NoResize"` を `CanResize` に変更し、特大文字で長い職員名・カード種別名の折返しが多発しても利用者がウィンドウを広げられるようにした（3 ダイアログとも `MinWidth="380"` / `MinHeight` 指定済みのため極小化はしない。`SizeToContent="Height"` と `CanResize` の併用は、利用者が手動リサイズした時点で WPF が `SizeToContent` を `Manual` に切り替える標準パターン。他 13 ダイアログは既に `CanResize` 系で、03_画面設計書 §5.6（Issue #1280）の「リサイズ可能にする」原則とも整合）。回帰防止として `DialogLayoutConventionTests` 3 件を新設（UT-058d: Views 配下全 XAML の FontSize 数値ハードコード禁止 / 全ダイアログの `ResizeMode="NoResize"` 禁止 / `SizeToContent` 使用時の `MinWidth`・`MinHeight` 必須）。03_画面設計書 §4.2.3（FontSize は DynamicResource 参照の実装規約）・§5.6（NoResize 禁止）、07_テスト設計書 §1.1a・§2.45 を同期更新（#1616）
- Issue #1584 インストーラーのマップトドライブ検出で、日本語を含む共有名（例: `\\10.250.3.16\道路_建設推進課`）が `?` に化けて昇格セッションへの再マッピングが失敗する問題を修正。`Win32_MappedLogicalDisk.ProviderName` を一時ファイル経由で受け渡す際の `Out-File -Encoding ASCII` が non-ASCII を `?` に lossy 置換していたため、`-Encoding UTF8`（PowerShell 5.1 は BOM 付き UTF-8 を書き出し、Inno Setup の `LoadStringsFromFile` が BOM を自動検出して Unicode デコードする）に変更（#1595）


### v2.9.2 (2026-05-28)

**ドキュメント**
- Issue #1473 README の「誤操作の取消」を「連続操作」に修正（#1473）
- Issue #1473 ルート README.md を充実化（#1473）


### v2.9.1 (2026-05-27)

**バグ修正**
- Issue #1584 インストーラーのマップトドライブ検出を wmic から PowerShell に置換（#1584）

**ドキュメント**
- Issue #1489 ユーザーマニュアル概要版を縦長2ページ構成に改訂（#1489）


### v2.9.0 (2026-05-26)

**バグ修正**
- 何らかの原因（アプリ異常終了、DB 手動修復、過去バージョンの不具合等）で「（貸出中）」状態の履歴行（`ledger.is_lent_record = 1`）が残ってしまった場合に、変更ボタンから開いた `LedgerRowEditDialog` に「削除」ボタンが表示されず、また `MainViewModel.DeleteLedgerRow` 側でも `IsLentRecord=true` のレコードを拒否ガードで弾いていたため、対応する物理カードが既に手元にないケースで「返却操作で復旧する」ことが不可能になり、データ整合性を取り戻す手段がなくなる問題を修正した。Issue #750（履歴行の追加／削除／変更機能）と Issue #1486（削除ガードのエラーメッセージ強化）で導入された安全策は通常運用での誤削除防止には有効だったが、異常状態からの**復旧不可能**という副作用が残っていた。具体的修正: (a) `LedgerRowEditViewModel.InitializeForEditAsync` で `CanDelete = !ledgerDto.IsLentRecord` だった条件を `CanDelete = true` に変更し、新規 `[ObservableProperty] bool IsLentRecord` を追加して編集対象が貸出中レコードかどうかを保持する、(b) `LedgerRowEditViewModel.RequestDelete` の確認ダイアログを `IsLentRecord` で分岐し、貸出中の場合は「この履歴は『貸出中』状態のレコードです。削除すると、このカードの貸出中状態も解消されます（他に貸出中レコードが残っている場合は維持されます）。通常は、メイン画面で交通系ICカードをタッチして返却操作を行うのが正しい復旧方法です。それでも削除しますか？」という専用警告を表示する、(c) `MainViewModel.DeleteLedgerRow` から `IsLentRecord` 拒否ガード（`NavigationService.ShowWarning` で「削除不可」を出していた早期 return）を削除し、確認 `MessageBox.Show` を貸出中レコード専用文言に分岐、(d) `MainViewModel` に private ヘルパー `ResetIsLentIfNoOtherLentRecordsAsync(Ledger deletedLedger)` を新設し、削除した行が `IsLentRecord=true` の場合のみ新規追加した `ILedgerRepository.HasOtherLentRecordsAsync(cardIdm, excludeLedgerId)` で同一カードに他の貸出中レコードが残っているかを判定し、残っていなければ `_cardRepository.UpdateLentStatusAsync(cardIdm, isLent:false, lentAt:null, staffIdm:null)` で `ic_card.is_lent` をリセット（多重貸出中の異常状態では他レコードがあるため `is_lent=true` を維持し段階的復旧を可能にする）、(e) `MainViewModel.EditLedgerWithAuthAsync` の `IsDeleteRequested` 分岐（変更ダイアログ経由の削除パス、Issue #750）にも同じ `ResetIsLentIfNoOtherLentRecordsAsync` 呼び出しを追加。新規 `HasOtherLentRecordsAsync` は `SELECT COUNT(*) FROM ledger WHERE card_idm = @cardIdm AND is_lent_record = 1 AND id <> @excludeLedgerId` で実装し、ledger DELETE 用既存トランザクションとは別接続で `is_lent` リセットを行う（`UpdateLentStatusAsync` がトランザクション引数を持たない現行設計に揃えた最小修正。部分失敗時は `CheckAndNotifyConsistencyAsync` が警告を出すので運用上検知可能）。回帰防止として `LedgerRepositoryTests` に `HasOtherLentRecordsAsync` のテスト 4 件追加（削除対象除外で他になし → false / 同一カードに他の貸出中あり → true / 別カードの貸出中は無視 → false / 通常レコードは無視 → false）、`LedgerRowEditViewModelTests` の旧仕様検証テスト `EditMode_LentRecord_CanDelete_IsFalse` を `EditMode_LentRecord_CanDelete_IsTrue_Issue1574` に書き換え（`CanDelete=true` と `IsLentRecord=true` の伝播を検証）、`EditMode_NormalRecord_CanDelete_IsTrue` にも `IsLentRecord=false` 検証を追加、`MainViewModelTests` の Issue #1486 旧仕様検証テスト 2 件（`ShowsWarningWithRecoveryAction` / `DoesNotProceedToAuthenticationOrDelete`）を新仕様検証 3 件（`LentRecord_DoesNotShowBlockingWarning` / `LentRecord_StartsAuthenticationFlow` / `LentRecord_WhenAuthCancelled_DoesNotDelete`）に書き換え。マニュアル・設計書の同期更新は本 PR には含めず、PR レビューで実機検証（変更ダイアログから貸出中行を削除して `ic_card.is_lent` がリセットされること、貸出中カード一覧から消えること）を併せて確認する（Issue #1574）
- 同方向の往復が **3 回以上**（6+ 経路、例: 天神→博多→天神→博多→天神→博多→天神）または「**2 往復 + 末尾に往復に使われない経路 1 件**」（5 経路、例: 天神→博多→天神→博多→天神→博多）のパターンで、摘要が「バス（天神～博多 往復）」「バス（天神～博多）」のように **大きく経路情報が欠落** する問題を修正した。原因は `SummaryGenerator.ConsolidateRoutes`（Issue #878）が `AreTransferStations(currentEnd, routes[i].Entry)` の隣接判定だけで「乗継（順方向に進む）」と「往復（戻ってくる）」を区別できておらず、A→B→A→B 型のチェーンを乗継として 1 経路に統合してしまっていたため。Issue #878 が偶数長循環チェーンに対して mid 分割の特殊処理を入れていたが、それは 2 往復ケースだけたまたま救う応急処置で、3 往復以上（分割後の奇数長サブチェーン `[A,B], [B,A], [A,B]` がさらに 1 経路に潰れる）や末尾が始点に戻らない奇数長チェーンでは情報が失われ続けていた。Issue #1579 で `GetRemainingRoutes` 側のバグは修正したが、`ConsolidateRoutes` の構造的バグは残っていた。具体的修正: `ConsolidateRoutes` のチェーン構築ループに「チェーン内既訪問駅集合（`visitedInChain`）」を導入し、次経路の終点がチェーン内既訪問なら方向反転とみなして乗継統合を打ち切るルールに変更。例外として「次経路の終点 == チェーンの始点」かつ「チェーン長 ≥ 3」となる場合のみ「閉じた循環（A→B→C→A 型の単一周回移動）」とみなしてチェーンを継続させ、末尾の `AddConsolidatedChain` の循環検出に個別表示を委ねる（Issue #878 で確立された奇数長循環＝個別表示の設計を維持。TC014 の「天神→姪浜→西新→天神」型の地下鉄経由循環移動の従来表示「鉄道（天神～姪浜、姪浜～西新、西新～天神）」を破壊しない）。既訪問判定は `AreTransferStations` による同一視を考慮するため、TC038 の「博多→天神→西鉄福岡(天神)→西鉄二日市→西鉄福岡(天神)→天神→博多」のような **乗継駅グループ越しの multi-transfer 往復** も従来通り「博多～西鉄二日市 往復」として正しく統合される。鉄道側も同じ `BuildRouteSummary` 経由のため鉄道履歴の同等パターンでも本修正で解消。回帰防止として `SummaryGeneratorComprehensiveTests` に 4 件追加（`TC_BUG1580_バス_同方向の往復3回_全て表示される` / `TC_BUG1580_鉄道_同方向の往復3回_全て表示される` / `TC_BUG1580_バス_同方向の往復2回プラス余り1件_全て表示される` / `TC_BUG1580_鉄道_同方向の往復2回プラス余り1件_全て表示される`）。テスト設計書 §1.1a / §2.2 UT-002c / §8.1 を同期更新（Issue #1580）
- 同方向の往復が複数回ある利用履歴（例: 天神→博多→天神→博多→天神 の 4 経路）で、摘要が「バス（天神～博多 往復、天神～博多 往復、天神～博多、博多～天神）」のように **個別経路 2 件分が末尾に重複表示** される問題を修正した。原因は `SummaryGenerator.GetRemainingRoutes` の `usedCount` ロジックが「方向ペア `(Entry, Exit)` ごとに独立にカウント」していたことで、N 往復ある同方向のうち forward 1 件と reverse 1 件だけが「往復消費済み」と判定され、残り `2(N-1)` 件が「往復の余り」として末尾に連結される構造だったため。具体的修正: `GetRemainingRoutes` を「forward 方向と reverse 方向それぞれで `roundTrips.Count` まで消費可能」とするロジックに置き換えた。`forwardQuotas[(Start, End)] = N` で各往復ペアの件数を集計し、経路を順に forward → reverse の優先順位で消費可能かチェックして、どちらの枠も埋まっている場合だけ remaining に追加する。鉄道側も同じ `BuildRouteSummary` 経由のため同等のバグが鉄道履歴でも出ていた（A駅→B駅→A駅→B駅 の 4 経路で重複表示）が、本修正で両方解決。Issue #1570（バス停入力に往復ボタン追加）でユーザーが同方向の往復を連続入力するハードルが下がったため再現確率が上がっていた。回帰防止として `SummaryGeneratorComprehensiveTests` に 2 件追加（`TC_BUG1579_バス_同方向の往復2回_重複表示なし` / `TC_BUG1579_鉄道_同方向の往復2回_重複表示なし`）。なお、3 往復以上（6+ 経路）および「2 往復 + 余り 1 件」（5 経路）のケースは `ConsolidateRoutes`（Issue #878）が「A→B→A→B」型のチェーンを乗継として 1 経路に誤統合し情報を失う別の構造的バグの影響を受けるため、本修正のスコープ外として Issue #1580 でフォローアップする（Issue #1579）
- デバッグビルドの仮想タッチ機能（`MainViewModel.OpenVirtualCardAsync` → `ProcessVirtualTouchAsync`）からバス利用を含む履歴を返却した際に、バス停名入力ダイアログ（`Views.Dialogs.BusStopInputDialog`）が表示されない問題を修正した。原因は `ProcessReturnAsync`（通常返却フロー）には `LendingService.ReturnAsync` の戻り値 `HasBusUsage` を見てダイアログを開く分岐があるのに対し、`ProcessVirtualTouchAsync` 側にはサウンド再生とトースト通知だけが実装されており、後続のバス停入力・履歴再読み込み・警告再チェック・「履歴が不完全」トーストが**まるごと欠落**していた「機能の劣化コピー」状態。再発防止として、返却成功時の共通後処理を `MainViewModel.HandleReturnSuccessAsync(IcCard, LendingResult)` という `internal` メソッドに抽出し、`ProcessReturnAsync`（リリース・デバッグ共通）と `ProcessVirtualTouchAsync`（`#if DEBUG` 配下）の両方から呼び出す構造にリファクタした。共通メソッドにまとめた処理: (a) `_soundPlayer.Play(SoundType.Return)`、(b) `_toastNotificationService.ShowReturnNotification`、(c) `RefreshLentCardsAsync` / `RefreshDashboardAsync`、(d) `IsHistoryVisible` 時の `LoadHistoryLedgersAsync`、(e) `CheckWarningsAsync`、(f) `HasBusUsage && CreatedLedgers.Count > 0` 時に `_settingsRepository.GetAppSettingsAsync()` で `SkipBusStopInputOnReturn` を確認し、false なら Summary が「バス」を含む `Ledger` 群を抽出して `_navigationService.ShowDialogAsync<BusStopInputDialog>` を呼び出し、ダイアログ後に履歴再読み込みと警告再チェックを再実行、(g) `MayHaveIncompleteHistory` 時の「履歴が不完全」警告トースト。`ProcessReturnAsync` 固有の `_lastProcessedStaffIdm` / `_lastProcessedStaffName` 退避と `ResetState()` は共通メソッドに含めず呼び出し側に残し、仮想タッチ側はメイン状態を変更しない既存セマンティクスを維持。`HandleReturnSuccessAsync` を `internal` 公開したことで `MainViewModelTests` から直接呼び出してバス停ダイアログ表示条件を 3 ケース（HasBusUsage=true + busLedgers あり → `ShowDialogAsync<BusStopInputDialog>` を `Times.Once` で検証 / HasBusUsage=false → `Times.Never` / `SkipBusStopInputOnReturn=true` → `Times.Never`）で回帰検出できる構造にした。**影響範囲**: 本問題は `#if DEBUG` 配下のみで発生するためリリースビルド（Release 構成）には影響なし。開発者が手元のデバッグビルドで仮想タッチを使ってバス利用シナリオを再現する際に「バス停名が `★` のまま残る」「以後 `WarningService.CheckIncompleteBusStopsAsync` が `★` を検出して警告表示が出続ける」副作用があった（Issue #1577）
- `LedgerRepository.InsertDetailsAsync(int, IEnumerable<LedgerDetail>)` の 1 引数版が、外側で既に `DbContext.BeginTransactionAsync` が握っているセマフォを内部で再取得しようとして無限待機（デッドロック）する問題を修正した。Issue #1456 で 1 引数版が「内部で自前トランザクションを開いて commit/rollback まで責任を持つ」設計に変更された際、Issue #1481 の tx 伝搬作業で `LendingService.PersistReturnAsync` → `CreateUsageLedgersAsync` 経路の伝搬が漏れており、利用履歴を含む返却処理（仮想タッチ／物理カード返却の両方）が UI スレッドに戻らないまま静かにハングする状態だった。表面上は「画面に何も変化が起きず、メニューの仮想タッチボタンが非活性のまま」「Ledger 行は DB に書かれているが UI 通知（音・トースト・履歴再読込）が一切出ない」という症状で、`PersistReturnAsync.scope.Transaction` を `CreateUsageLedgersAsync` まで伝搬するピンポイント修正は callsite が多すぎて既存テスト 25 件のモック Setup/Verify 書き換えを要したため、より根本的かつ防御的な Repository 側自己防御を選択した。具体的修正: (a) `DbContext` に `private int _activeTransactionCount`（容量 1 のセマフォで直列化されているため値は 0 か 1 のみ）と `public bool HasActiveTransactionScope => _activeTransactionCount > 0` を追加し、`BeginTransactionAsync` の入口・Lease 解放（コミット／ロールバック後の `scope.Dispose`）でそれぞれ increment / decrement する、(b) `LedgerRepository.InsertDetailsAsync(int, IEnumerable<LedgerDetail>, SQLiteTransaction)`（3 引数本体）の `transaction=null` 経路に「`_dbContext.HasActiveTransactionScope` が真なら自前 `BeginTransactionAsync` を開かず `LeaseConnectionAsync` で接続を取得して `command.Transaction=null` で INSERT を発行する（外側 tx の暗黙参加）」分岐を追加。`LendAsync.InsertAsync(1 引数版)` / `UpdateAsync(1 引数版)` 等の既存メソッドが既にこの暗黙参加パターンで動作しており、本修正はそれらと挙動を揃える形になっている。これにより `LendingService` 経路は完全に無変更で済み、既存 166 件の `LendingService*` テストはすべて無修正のまま pass。回帰防止として実 SQLite + 実 Repository + LendingService の最小組み合わせで貸出→返却を 10 秒タイムアウト付きで実行する `LendingServiceReturnDeadlockTests`（1 件）を新規追加した。本テストは修正を巻き戻すと確実に timeout failure を起こすことを Red→Green サイクルで確認済み。**影響範囲**: Issue #1456 の混入コミット（2026-05-20）は v2.8.1 リリース（2026-05-11）以降の `### Unreleased` 状態にしか含まれていないため、リリース済みバージョンへの影響はない。`main` から自前ビルドしている開発者のみが影響を受ける（Issue #1575）

**バグ修正（インストーラー）**
- インストーラーのマップトドライブ検出処理で使用していた `wmic.exe`（`Win32_MappedLogicalDisk` クエリ）を PowerShell の `Get-CimInstance` に置き換えた。`wmic.exe` は Windows 11 22H2 以降でオプション機能に格下げされており、未インストール環境では GPO やログインスクリプトで割り当てたマップトドライブの検出がサイレントに失敗していた。PowerShell（`WindowsPowerShell\v1.0\powershell.exe`）は Windows 10/11 に標準搭載されているため追加インストール不要。出力形式（`DeviceID=`/`ProviderName=` ペア）は従来と同一で、既存のパース処理に変更なし（Issue #1584 フォローアップ）
- インストーラー実行中の「データベースの保存先」および「帳票出力先フォルダ」ページで、`net use` 等で設定したマップトドライブが「参照...」ボタンのフォルダ選択ダイアログに表示されない問題を修正した。原因はインストーラーが `PrivilegesRequired=admin`（管理者権限）で動作するため、UAC 昇格後のプロセスが標準ユーザーセッションのドライブマッピングを参照できないという Windows の仕様上の制約。対策として、レジストリ `HKCU\Network` からマップトドライブ情報を列挙し、`net use <ドライブレター> <UNCパス> /persistent:no` で昇格セッションに一時的に再マッピングする処理を `InitializeWizard` 内で実行するようにした。これにより「参照...」ボタンのフォルダ選択ダイアログにマップトドライブが自然に表示され、サブフォルダへのナビゲーションも可能になる。`/persistent:no` フラグにより再マッピングは昇格セッション限定で、ログオフ・再起動後に残らない（`net use /delete` による明示的クリーンアップは `HKCU\Network` のレジストリエントリも削除してしまいユーザーの元のマッピングを破壊するため行わない）。`net use` が失敗した場合（資格情報やネットワーク接続の問題）は該当ドライブをスキップし、ユーザーは従来どおり UNC パスの直接入力でフォールバック可能。マップトドライブが 1 つも検出されない環境では追加処理は一切行われず、従来と同一の動作を維持する。管理者マニュアル §2.2 にマップトドライブの補足を追記（Issue #1584）

**機能改善（ユーザビリティ）**
- バス停名入力ダイアログ（`BusStopInputDialog`）の 2 行目以降の各行に「↑往復」ボタンを追加し、1 つ上の行のバス停名（A～B）の起点と終点を入れ替えた値（B～A）を当該行にワンクリックで入力できるようにした。改修前は出勤・退勤の往路復路や行きと帰りで起点・終点が反転する典型ケースで毎回フルテキスト入力（または同じサジェスト候補からの再選択）が必要だった。具体的修正: (a) `BusStopInputItem` に `[ObservableProperty] BusStopInputItem? PreviousItem` と派生プロパティ `HasPreviousItem` (`[NotifyPropertyChangedFor]` で連動)、`[RelayCommand] ApplyRoundTrip` メソッドを追加。`ApplyRoundTrip` は `PreviousItem.BusStops` を `～` で分割して 2 要素になった場合のみ前後 `Trim` 後に左右入れ替えて `BusStops` にセットし、空欄／`★`／`～` なし／`～` が複数（`A～B～C`）の場合は no-op。(b) `BusStopInputViewModel` に private ヘルパー `LinkPreviousItems()` を追加し、4 つの初期化メソッド（`InitializeAsync` / `InitializeWithDetailsAsync` / `InitializeWithDetails` / `InitializeWithLedgersAsync`）の `BusUsages.Add` ループ後に毎回呼ぶことで、複数 Ledger 横断（Issue #1203）でも `BusUsages[i].PreviousItem = BusUsages[i-1]` のリンクを保証。(c) `BusStopInputDialog.xaml` の `DataTemplate` 行レイアウトに 4 列目（`ColumnDefinition Width="Auto"`）を追加し、`Visibility="{Binding HasPreviousItem, Converter={StaticResource BoolToVisibilityConverter}}"` で先頭行のみ非表示にする「↑往復」ボタンを配置。`AutomationProperties.Name` / `HelpText` / `ToolTip` で「一つ上の行のバス停名の起点と終点を入れ替えて入力」の操作ガイダンスを提示しスクリーンリーダーにも読み上げさせる。当該行に既存値があってもユーザーの明示操作とみなして上書きする仕様。回帰防止として `BusStopInputViewModelTests` に 10 件追加: (1) 先頭行は `HasPreviousItem=false`、2 行目以降は `true`、(2) `PreviousItem` が直前アイテムを参照、(3) 基本ケース `天神～博多駅前 → 博多駅前～天神`、(4) 前後空白トリム `  天神  ～  博多   → 博多～天神`、(5) 前の行が空欄なら no-op、(6) `～` なしなら no-op、(7) `★` のみなら no-op、(8) `～` 複数なら no-op、(9) 先頭行で実行しても no-op、(10) 既存値の上書き。テスト設計書 §1.1a / §2.22（新規 UT-029d 追加）/ §3.8（画面設計書）を同期更新（#1570）

**機能改善（保守性・防御）**
- `MigrationHelpers.AddColumnIfNotExists` / `HasColumn` の引数 `column` / `typeAndConstraints` をホワイトリスト regex で検証する防御層を追加（Issue #1466）。識別子は `^[A-Za-z_][A-Za-z0-9_]*$`、型句は `INTEGER` / `TEXT` / `REAL` / `BLOB` / `NUMERIC` に `NOT NULL` / `DEFAULT <整数\|小数\|'literal'\|NULL>` / `REFERENCES <table>(<col>)` を組み合わせた構文のみ受理。範囲外の値を渡すと `ArgumentException` を即座に投げ SQL 実行に到達させない。既存呼び出し 7 callsite（`Migration_002` / `_003` / `_005` / `_006` ×2 / `_009` ×2）の実引数はすべて受理される。`HasColumn` の `table` 検証も既存の `IndexOfAny(['\'', '"', ';', ' '])` から識別子 regex に統一し、ヘルパー全体で一貫した防御に揃えた。本変更は開発者の事故予防が目的で、引数は元々外部入力ではないため攻撃面の縮小ではない（Sec M3, 2026-05-08 リポジトリ全体レビュー由来）

**ドキュメント**
- 管理者マニュアル（§2.3「2 台目以降の PC への導入」の入力例表・補足、§10.4「ネットワーク共有フォルダのアクセス権設定ガイド」の接続確認手順、付録 B 用語集の「共有モード」エントリ）およびユーザーマニュアル（FAQ Q5）から「UNC パス推奨」表現を削除し、UNC パスとマップドネットワークドライブを並列扱いに改訂した。想定環境ではシステム管理者によりドライブレターが全 PC で統一されているケースが多く、実運用ではマップドドライブの方が使われることもあるため、UNC を一方的に推奨する旧記載は実態と乖離していた。コード本体は Issue #1559 で既に UNC とマップドネットワークドライブを等価に共有モード判定する実装に揃っており、本変更はドキュメント側の整合性を取るための文言修正のみで動作に変更はない（Issue #1571）
- マニュアル 4 ファイル（`かんたん導入ガイド.md` / `はじめに.md` / `ユーザーマニュアル概要版.md` / `開発者ガイド.md`）のヘッダー `**バージョン**` 文字列が v2.7.0 のまま v2.8.0 / v2.8.1 リリースで取り残されていた問題を修正。コード側 `<Version>` は v2.8.1（`ICCardManager.csproj`）、`ユーザーマニュアル.md` / `管理者マニュアル.md` は v2.8.1 へ既に同期済みだったが、PR #1446 で「ユーザー/管理者」2 本だけが更新対象に組み込まれて残り 4 本が見落とされていた構造的な漏れ。再発防止として `tools/bump-version.ps1` の更新対象に 4 ファイルを追加（`$QuickStartPath` / `$IntroPath` / `$UserManualBriefPath` / `$DeveloperGuidePath` を変数化し、3f-3i の置換ブロック・DryRun の files 配列・最終 `WriteAllText` 列・`$filesToAdd` 配列・PR body の更新ファイル一覧の 5 箇所を全部同期）。開発者ガイドだけ既存スタイルが `**最終更新日**: 2026年4月15日` の日付付き形式だったため、新規変数 `$TodayJpFull = Get-Date -Format "yyyy年M月d日"` を導入して開発者ガイドのみそちらを参照する分岐とした（他 5 マニュアルは従来通り `$TodayJp` の月のみ表記）。`.claude/skills/release/SKILL.md` §1 の「更新対象ファイル」リストも 4 ファイル追記し、ドキュメント・スクリプト・実体ファイルの三者整合を確保（#1462）

**機能改善**
- Ledger 関連の 7 callsite で Ledger 操作と監査ログ INSERT を同一 SQLiteTransaction に統合し、SMB 共有モード時の fsync を 2 回 → 1 回（1 RTT）に削減した。改修前は `LedgerRowEditViewModel.SaveAddAsync/SaveEditAsync` / `MainViewModel.DeleteLedgerRow` / `MainViewModel.EditLedgerWithAuthAsync` 内 Delete / `LedgerDetailViewModel.Save` / `LedgerMergeService.MergeAsync` / `LedgerSplitService.SplitAsync` のいずれも、本体の `LedgerRepository.*Async` の後で `OperationLogger.LogLedger*Async` を別接続リース・別トランザクションで逐次 await しており、共有モード（SMB）では fsync 1 回分のラウンドトリップが必ず加算されてカードタッチ → 「ピッ」音の体感速度に直接効く構造だった（2026-05-08 のリポジトリ全体レビュー Perf H4 由来）。具体的修正: (a) `IOperationLogRepository.InsertAsync(OperationLog, SQLiteTransaction)` を新規追加し、既存 `InsertAsync(OperationLog)` は `transaction: null` で新版に委譲（共通インフラ整備）。(b) `ILedgerRepository` に `DeleteAsync(int, SQLiteTransaction)` / `ILedgerMergeRepository` に `MergeLedgersAsync(int, IEnumerable<int>, Ledger, SQLiteTransaction)` / `ReplaceDetailsAsync(int, IEnumerable<LedgerDetail>, SQLiteTransaction)` を追加し、既存非 tx 版は新版に委譲する形へ統一。`MergeLedgersAsync` の旧版が内部で持っていた `BeginTransactionAsync` も新版に集約。(c) `OperationLogger` に 5 つの tx 受入オーバーロード `LogLedgerInsertAsync(Ledger, SQLiteTransaction)` / `LogLedgerUpdateAsync(Ledger, Ledger, SQLiteTransaction)` / `LogLedgerDeleteAsync(Ledger, SQLiteTransaction)` / `LogLedgerMergeAsync(IReadOnlyList<Ledger>, Ledger, SQLiteTransaction)` / `LogLedgerSplitAsync(Ledger, IReadOnlyList<Ledger>, SQLiteTransaction)` を追加。(d) 7 callsite すべてを `using var scope = await _dbContext.BeginTransactionAsync()` で外側 tx を持ち、Ledger 操作とログ INSERT を同一 `scope.Transaction` で実行して `scope.Commit()` する形に改修。`LedgerRowEditViewModel.SaveEditAsync` の Issue #983 バス停名同期（`SyncBusStopsFromSummaryAsync`）は副次処理として tx 外に保持し、SMB ロック競合を最小化。`LedgerDetailViewModel.Save` は `operatorIdm` が空（GUI 操作）の場合はログ記録自体がないため tx を使わず従来通り、`operatorIdm` がある場合のみ同一 tx 化する条件分岐。`LedgerMergeService.MergeAsync` では `SaveMergeHistoryAsync` は別系統データのため tx 外（スコープ拡大回避）、`LedgerSplitService.SplitAsync` は `ReplaceDetailsAsync` / `UpdateAsync` / `InsertAsync` / `InsertDetailsAsync` / `LogLedgerSplitAsync` 5 メソッドすべてを単一 tx で実行して部分書き込み消失も原子的に解消（旧実装ではループ中の各 InsertAsync が独立 autocommit だったため失敗時に部分挿入が残る経路があった）。`MainViewModel` / `LedgerRowEditViewModel` / `LedgerDetailViewModel` / `LedgerMergeService` / `LedgerSplitService` のコンストラクタに `DbContext` を DI 追加。(e) 副次効果として、Ledger 本体と監査ログが原子的にコミットされるためデータ整合性も向上（改修前は Ledger UPDATE 成功＋ログ挿入失敗で監査欠落の可能性があった）。Issue 本文の選択肢 2（Channel ベースバックグラウンドキュー）は不採用：`DbContext` が `SemaphoreSlim(1,1)` で単一接続を直列化する構造上、並行性メリットが構造的に得られず投資対効果に見合わないため（スコープ外として Issue 別建て可能）。`LendingService` への監査ログ追加は Issue 本文の認識誤りで現状 `LendingService` は `OperationLogger` を呼ばないため、本 PR ではスコープ外（要件次第で別 Issue）。回帰防止として 4 種類のテストを追加: (a) `OperationLogRepositoryTransactionTests`（2 件: tx Commit で行可視 / Rollback で行消失）、(b) `OperationLoggerTransactionTests`（5 件: 各 LogLedger*Async が Mock<IOperationLogRepository>.InsertAsync(log, tx) を正しい引数で 1 回呼ぶことを Moq で検証）、(c) `LedgerRepositoryTransactionTests` の拡張（+6 件: `DeleteAsync(int, SQLiteTransaction)` Commit/Rollback × 2、`MergeLedgersAsync(..., SQLiteTransaction)` Commit/Rollback × 2、`ReplaceDetailsAsync(..., SQLiteTransaction)` Commit/Rollback × 2）、(d) `LedgerLogAtomicityTests`（3 件: UPDATE+Log を同一 tx で Commit して両方永続化 / Rollback で両方消失 / DELETE+Log を同一 tx で Commit）。既存 `LedgerRowEditViewModelTests` / `MainViewModelTests` / `MainViewModelSharedDbStateTests` / `MainViewModelSyncDisplayTests` / `MainViewModelIntegrationTests` / `LedgerDetailViewModelTests` / `LedgerMergeServiceTests` / `LedgerSplitServiceTests` のモック Setup/Verify/Callback を新シグネチャに更新（Mock<DbContext> ではなく `TestDbContextFactory.Create()` の実体を注入し、`scope.Transaction` を受け取った `_ledgerRepository.*Async(arg, It.IsAny<SQLiteTransaction>())` 形のセットアップに揃えることで、ALL OR NOTHING の挙動を mock 経由でも検出可能にした）。テスト設計書 §1.1a を 3,278 + 26 = 3,304 → 3,294 + 26 = 3,320（+16 件）に同期更新（#1458）
- `LedgerRepository.GetPagedAsync` の `detail_count` 取得を相関サブクエリ（N+1）から CTE による page-scoped 集計に変更し、履歴ページャ表示時の SQLite 実行コストを削減した。改修前は メイン SELECT 句に `(SELECT COUNT(*) FROM ledger_detail WHERE ledger_id = l.id) as detail_count` が埋め込まれており、pageSize=50 では本体 SELECT + 50 個のサブクエリ実行が積み上がる N+1 構造だった。`idx_detail_ledger(ledger_id)` でインデックスは効いていてもサブクエリ起動コスト自体は行数倍に効き、`ledger_detail` が数十万行スケールに育つ現場や共有モード（SMB）の往復遅延が乗る環境では履歴ページャの体感応答が悪化しうる構造だった（2026-05-08 のリポジトリ全体レビュー Perf H3 由来）。具体的修正: (a) `LedgerRepository.GetPagedAsync` のメインクエリを `WITH paged_ledger AS (...)` の CTE 構造へ書き換え、CTE 内でページ対象の ledger 行を `ORDER BY ... LIMIT @pageSize OFFSET @offset` で確定。(b) 外側 SELECT で `LEFT JOIN (SELECT ledger_id, COUNT(*) AS cnt FROM ledger_detail WHERE ledger_id IN (SELECT id FROM paged_ledger) GROUP BY ledger_id) d` を組み、`COALESCE(d.cnt, 0) AS detail_count` で詳細 0 件の ledger も従来同等のセマンティクス（旧 `(SELECT COUNT(*) ...)` は対象なしで 0 を返す）を維持。(c) ledger_detail への集計は CTE 経由で「現在ページの ID 集合」に絞り込まれるため、`ledger_detail` 全件スキャンに広がらない。(d) 元コードにあった `whereClause.Replace("card_idm", "l.card_idm").Replace("date ", "l.date ")` の文字列書き換えが不要となり、CTE 内では `ledger` テーブルを直接参照する形になったため、`whereClause` を素のまま埋め込めるようになって保守性も向上。`Ledger.DetailCount` / `LedgerDto.DetailCountValue` の利用側（`LedgerDto.HasDetails => DetailCount > 1` 等）・列順序・`MapToLedgerWithDetailCount` のマッパーは変更せず、戻り値の互換性を完全に維持。回帰防止として `LedgerRepositoryTests` に 2 件追加: (a) `GetPagedAsync_DetailCount_AccurateForVariousCounts` で詳細 0/1/3 件の ledger を混在登録して各行の `DetailCount` が正確に返ることを検証（旧相関サブクエリと同セマンティクスの維持）、(b) `GetPagedAsync_DetailCount_OnlyCountsRowsForPagedLedgers` で 5 件の ledger をそれぞれ詳細 1〜5 件で登録し、pageSize=2 のページングで page1（利用1=1件 / 利用2=2件）と page3（利用5=5件）の `DetailCount` がページ外の集計に汚染されないこと（CTE スコープが効いていること）を検証する（#1457）
- `LedgerRepository.InsertDetailsAsync` を単一トランザクション＋単一 `SQLiteCommand` 再利用に変更し、複数明細を一括書込みする際の I/O を大幅に削減した。改修前は `foreach (var detail in details) await InsertDetailAsync(detail, transaction)` の N+1 ループで、`transaction=null` 経路では呼び出しごとに `LeaseConnectionAsync()` を取り直し独立した autocommit で 1 件ずつ INSERT していたため、`journal_mode=DELETE` では各 INSERT が rollback journal の作成・fsync・削除を伴い、共有モード（SMB）では往復遅延 1〜10ms × 行数が直線的に効いて返却処理・分割・CSV インポートが遅かった。具体的修正: (a) `LedgerRepository.InsertDetailsAsync(int, IEnumerable<LedgerDetail>, SQLiteTransaction)` 本体を書き換え、`transaction=null` の場合は内部で `_dbContext.BeginTransactionAsync()` を開いて commit/rollback まで責任を持ち、`transaction` 指定時は呼び出し元 tx を共有して commit/rollback には介入しない、(b) 新設 private static `InsertDetailsCore` で 1 つの `SQLiteCommand` を生成し、11 個のパラメータを `Parameters.Add(name, DbType)` で宣言したうえでループ内では `.Value =` だけ再代入して `ExecuteNonQueryAsync` を回す、(c) 空コレクション入力は接続も tx も取らずに `true` を即時返却する早期 return を追加、(d) 例外時は内部 tx を rollback してから再スロー、`ExecuteNonQueryAsync` が 0 を返した場合も rollback する。挙動の改善として、旧実装で `transaction=null` 経路が途中失敗時に「N-1 件 commit 済みの不整合状態」を残し得たのが、新実装では ALL OR NOTHING に統一された。呼び出し元（`LedgerSplitService` / `NewLedgerFromSegmentsBuilder` / `ReplaceDetailsAsync` 経由の CSV インポート / `LendingService` 経由の貸出返却）はシグネチャ変更なしで自動的に高速化される。回帰防止として `LedgerRepositoryBatchInsertTests`（5 件: 100 件 tx=null で全件挿入 / 100 件 caller-tx Rollback で全消滅 / 空入力で副作用なし / 各行 LedgerId 上書き / 例外後のセマフォ解放）を新規追加（#1456）
- 共有モード時の DB 接続状態を「Connected / Reconnecting / Disconnected」の 3 状態でステータスバーに表示するようにした。改修前は `WarningMessages` ヘッダー警告でしか切断を知ることができず、運用者が「⚠ 切断 → 🔄 再接続中 → 🔗 接続中」の状態遷移を一目で把握できなかったため、Issue #1428 でカードリーダー接続状態に導入済みのアイコン＋色＋テキスト＋ToolTip の 4 要素表示パターンを共有 DB 接続にも適用した。具体的修正: (a) `Services/SharedDbConnectionState.cs` に新規 enum `SharedDbConnectionState`（Connected/Reconnecting/Disconnected）と `SharedDbConnectionStateChangedEventArgs` を追加、(b) `SharedModeMonitor` に `CurrentConnectionState` プロパティと `ConnectionStateChanged` イベントを追加し、`ExecuteHealthCheckAsync` 内で「前回 Disconnected の場合は次のチェック実施中を Reconnecting と通知 → 結果に応じて Connected か Disconnected」の遷移を発火するロジックを組み込んだ（連続成功・連続失敗時は遷移を起こさず無音）、(c) `MainViewModel` に `[ObservableProperty] SharedDbConnectionState SharedDbConnectionState` を追加し、`OnSharedDbConnectionStateChanged` ハンドラで遷移エッジに応じて `_dispatcherService.InvokeAsync` 経由でプロパティ更新と Toast を発火（Connected→Disconnected の初回切断時のみ `ShowWarning`、Disconnected/Reconnecting→Connected の復帰時のみ `ShowInfo`、Reconnecting→Disconnected の再失敗時は抑止）、(d) `MainWindow.xaml` の「🔗 共有モード」StatusBarItem を 3 状態 DataTrigger 化（既定: 「🔗 共有モード」+ PrimaryBrush、Reconnecting: 「🔄 共有モード（再接続中）」+ WarningActionBrush、Disconnected: 「⚠ 共有モード（切断中）」+ ErrorBorderBrush）。ToolTip も状態ごとに切り替えて 4 要素原則（アイコン・色・テキスト・ToolTip）を満たす。ローカルモード時は `IsSharedMode` Visibility で StatusBarItem ごと非表示のため、enum 既定値 Connected が UI に露出することはない。回帰防止として `SharedModeMonitorConnectionStateTests`（6 件: 初期値 Connected / Success-on-Connected で発火なし / Failure-on-Connected で Disconnected 遷移と単発イベント / Disconnected からの成功で Reconnecting 経由 Connected の 2 連発火 / Disconnected からの再失敗で Reconnecting→Disconnected / 連続成功でイベント無発火）と `MainViewModelSharedDbStateTests`（5 件: 初期値 Connected / Disconnect 時 ShowWarning 1 回 / 再失敗時 ShowWarning 連続発火抑止 / 復帰時 ShowInfo 1 回 / 接続維持中は Toast 無発火）を新規追加。`docs/design/03_画面設計書.md` §6 ステータスバー仕様に 3 状態表示を追記（#1470）

**ユーザビリティ修正**
- `Common/PathValidator.cs` の 12 箇所のエラーメッセージを `.claude/rules/error-messages.md` の「何が・なぜ・どうすれば」3 要素ガイドラインに沿って書き換えた。Issue #1275 で `ValidationService` に導入した品質基準を follow-up として `PathValidator` にも適用したもの。具体的な修正対象は (1) バックアップパス未指定、(2) パス長超過（実際の入力長と上限 260 文字を明示）、(3) 使用できない文字（予約文字の例を併記）、(4) 絶対パスでない（ローカル / UNC 両形式の例を併記）、(5) ドライブ未準備（USB 抜けの可能性を示唆し別ドライブ指定を促す）、(6) UNC 共有名欠落（`\\server\share` 形式を明示）、(7) UNC サーバー名空（ホスト名・IP アドレスの選択肢を提示）、(8) UNC 共有名空、(9) UNC 到達不可（タイムアウト時間と確認すべき項目を明示）、(10) パストラバーサル（`..` を含まない形式を案内）、(11) フォルダ書き込み権限欠如、(12) 親ディレクトリ書き込み権限欠如。回帰防止として新規 `PathValidatorErrorMessageQualityTests` を追加（9 ケース、`ValidationServiceErrorMessageQualityTests` と同じ `AssertQualityCriteria` を採用し、20 文字以上 / 句点 / 「〜してください」終止を機械検証）。既存 `PathValidatorTests` は部分文字列マッチでアサーションされているため、互換キーワード（"指定されていません" / "長すぎます" / "絶対パス" / "サーバー名と共有名が必要" など）を新メッセージに含めることで 1 件を除き修正不要とした。例外として Issue #1483 の `BothUncPrefixVariants_ProduceSameFormatVerdict` のみ OR 句の「サーバー名が不正」「共有名が不正」を「サーバー名が空」「共有名が空」に最小修正し、プレフィックス両形式の等価判定意図を維持した。テスト設計書 §1.1a / §2.23 / §8.1 を同期更新（単体テスト件数 3,251 → 3,260、合計 3,277 → 3,286）（#1471）

**セキュリティ修正**
- `Process.Start(UseShellExecute=true)` で開くパスのうち、`report_output_config.txt` 由来の `OutputFolder` ・ エクスポート系の `LastExportedFile` ・ バックアップフォルダ ・ ヘルプ `Docs` フォルダなど **7 callsite**（`ReportViewModel.OpenOutputFolder` / `OpenCreatedFile`、`DataExportImportViewModel.OpenExportedFile` / `OpenExportFolder`、`SystemManageViewModel.OpenBackupFolder`、`MainViewModel.OpenHelp`、`OperationLogSearchViewModel.OpenExportedFile`）に、攻撃者が書換可能な設定ファイル経由で任意 `.exe` を仕込んだ際にユーザーの合法的なボタンクリックでコード実行へ誘導される脆弱性があったため、defense-in-depth として 2 層の防御層を導入した（Sec M2, 2026-05-08 リポジトリ全体レビュー由来）。具体的修正: (a) `Common/SafeFilePathValidator` を新設し、フォルダパス／ファイルパスの **純粋関数による検証**（空・null・制御文字・`Path.GetInvalidPathChars()` を弾く）と、ファイルに対する **拡張子ホワイトリスト**（`.xlsx` / `.csv` のみ。本アプリが実際に生成する形式のみに絞り `.exe` / `.bat` / `.com` / `.vbs` / `.ps1` / `.js` / `.scr` / `.lnk` / `.msi` / `.hta` / `.pdf` / `.txt` / `.docx` 等はすべて Failure を返す）を実装、(b) `Services/ISafeFileLauncher` / `SafeFileLauncher` を新設し、フォルダオープンは **`explorer.exe` を `UseShellExecute=false`** で直接起動してシェル関連付けを経由しない経路に固定、ファイルオープンは Validator 通過後のみ `UseShellExecute=true` + `Verb="open"` で起動、(c) 7 callsite すべてを `_safeFileLauncher.LaunchFolder` / `LaunchFile` 経由に置換し、`SafeFileLaunchResult.Success=false` 時は `SetStatus(result.ErrorMessage, isError: true)`（または `MessageBox.Show`）でユーザーに「何が／なぜ／どうすれば」3 要素のエラーメッセージを提示、(d) `App.xaml.cs` で `services.AddSingleton<ISafeFileLauncher, SafeFileLauncher>()` を DI 登録、(e) `report_output_config.txt` 自体の ACL 強化（ProgramData 配下権限見直し）は別 Issue に委ね、本 Issue では「設定ファイル書換に対する 1 段の追加防御層」のスコープに集中。回帰防止として 3 種類のテストを追加: (i) `SafeFilePathValidatorTests`（35 件: 空 / 制御文字 / 改行 / `Path.GetInvalidPathChars` / UNC / ローカル / 拡張子 whitelist（`.xlsx` / `.csv` 大文字小文字無視）/ 実行可能拡張子 11 種 Failure / 非生成拡張子 4 種 Failure / 拡張子なし Failure / エラーメッセージ品質（20 文字以上・行動指示型語尾））、(ii) `SafeFileLauncherTests`（16 件: フォルダ空 / 存在しない / 制御文字、ファイル空 / 実行可能 / 存在しない `.xlsx` / `.pdf` 拒否、`SafeFileLaunchResult.Ok` / `Fail` の Success フラグ・ErrorMessage 保持。実 `Process.Start` 起動は GUI 起動を避けるため検証しない）、(iii) 4 ViewModel テストに合計 13 件追加（`ReportViewModelTests` 4 件 / `SystemManageViewModelTests` 3 件 / `OperationLogSearchViewModelTests` 2 件 / `DataExportImportViewModelTests` 4 件: `Mock<ISafeFileLauncher>.Verify` で各コマンドが `LaunchFolder` / `LaunchFile` を正しい引数で 1 回呼ぶこと・失敗結果が `StatusMessage` / `IsStatusError` へ反映されることを検証）。**手動検証項目**: (1) `OutputFolder` を `C:\Windows\System32\notepad.exe` 等に書き換えて「出力フォルダを開く」ボタン → エラー表示でメモ帳が起動しないこと、(2) 通常エクスポート → 「ファイルを開く」 → Excel が起動すること、(3) エクスポート後にファイルを削除 → 「ファイルを開く」 → エラー表示で何も起動しないこと（#1465）
- `BackupService` / `DbContext` / `FileLoggerProvider` の `EnsureDirectoryWithPermissions` がランタイムで親ディレクトリ（`C:\ProgramData\ICCardManager` 配下のデータ／バックアップ／ログディレクトリ）に **`BUILTIN\Users : FullControl`** を `AddAccessRule` で付与していた処理を撤廃した。`FullControl` は削除権限・所有権変更まで含むため、(1) 一般ユーザーが他職員のバックアップ／ログを削除・差替え可能となる過剰権限であり、特にリストア機能と組み合わせると任意 DB へのリストア（PII 置換攻撃）の足掛かりとなりうる、(2) `AddAccessRule` は冪等ではなく権限種別が一致しても新規 ACE が累積される（起動の度に ACL が膨張する）、という二重の問題があった。インストーラー (`installer/ICCardManager.iss:64-66`) が既に `{commonappdata}\ICCardManager` / `backup` / `Logs` に `Permissions: users-full` を設定済みのため、ランタイム側の再付与は機能的に冗長。3 ファイルとも `Directory.CreateDirectory` のみを残してランタイム ACL 操作を削除し、`System.Security.AccessControl` / `System.Security.Principal` の不要 using も整理。`DbContext.SetDatabaseFilePermissions` の XML doc コメントも `EnsureDirectoryWithPermissions` の挙動変更に合わせて更新。`docs/manual/開発者ガイド.md §5.4` に「ディレクトリ ACL はインストーラーが管理し、ランタイムでは変更しない」方針を明記。回帰防止として既存の `DbContextFilePermissionsTests` の 2 件（FullControl 付与検証）を更新し、(a) ディレクトリが作成されること、(b) 既存ディレクトリでも例外が発生せず冪等に動作すること、(c) `BUILTIN\Users` への明示的 FullControl ACE が付与されないこと、(d) 複数回呼び出しても明示的 ACE が累積追加されないこと、の 4 件で過剰権限・ACE 累積の再発を検出する（#1455）
- `DebugDataService.CleanExistingTestDataAsync` の SQL DELETE 4 文（`ledger_detail` / `ledger` / `ic_card` / `staff`）で `IN ({testCardIdms})` を文字列補間で構築していた箇所を IN 句パラメータ化（`@c0,@c1,...` / `@s0,@s1,...`）に書き換えた。該当箇所は `#if DEBUG` ガード配下でテスト固定 IDm（16進数文字列）のみが埋め込まれるため Release 出荷物には含まれず、実害となる攻撃面は存在しなかったが、「SQL に外部値を直接埋め込まない」コーディング標準を DEBUG 限定コードにも一貫適用する衛生改善（2026-05-08 のリポジトリ全体レビュー Sec L3 由来）。実装はメソッド内 `static` ローカル関数 `BuildInClause(SQLiteCommand, IEnumerable<string>, string)` がプレースホルダ列を構築すると同時に `cmd.Parameters.AddWithValue` を反復登録する形にして DRY を担保。`#if DEBUG` ガード・既存トランザクション境界（`_dbContext.BeginTransactionAsync` 内）・`LeaseConnectionAsync` 利用・ログ出力は不変。回帰防止として `DebugDataServiceTests` に 2 件追加: (a) `CleanExistingTestDataAsync_RemovesTestRecordsAndPreservesNonTestRecords` — テスト IDm の行が削除され非テスト IDm の行が残存することをインメモリ SQLite 上で実検証、(b) `CleanExistingTestDataAsync_DoesNotInjectFromQuotedIdm` — `'; DROP TABLE ic_card; --` を含むレコードが事前に存在しても `ic_card` テーブルが破壊されず、`TestCardList` に含まれないため削除対象外として残存することを検証（パラメータ化の本質を将来の保守者に伝える防御テスト）（#1485）

**バグ修正**
- 履歴行を右クリックから削除しようとして対象が貸出中レコード（`IsLentRecord=true`）の場合に表示される警告ダイアログ（タイトル「削除不可」）の本文を、「貸出中のレコードは削除できません。」のみ（「何が／なぜ」しか含まない）から、「貸出中のレコードは削除できません。先にメイン画面で交通系ICカードをタッチして返却操作を行ってから、再度削除してください。」へ拡張し、`.claude/rules/error-messages.md` の「何が／なぜ／どうすれば」3要素ガイドラインに準拠させた。「貸出中だから削除不可」までは伝わっても「どうすれば削除できるようになるか（先に返却する）」が示されないため、ユーザーが画面上のどの操作に進むべきか判断できず行き止まりになっていた。同じ修正の中で `MainViewModel.DeleteLedgerRow` の `MessageBox.Show` 直接呼び出しを `INavigationService.ShowWarning` 経由に置換し（既存の `_navigationService` フィールドを再利用、UI スレッド経路は `DialogService` 内で同一）、Issue #853 で確立した「ViewModel は `MessageBox` を直接呼ばず `IDialogService` 系を経由する」アーキテクチャ原則にも一致させた。これにより警告メッセージ本文・タイトルの単体テスト固定化が可能になり、`MainViewModelTests` に 3 件追加: (a) `DeleteLedgerRow_LentRecord_ShowsWarningWithRecoveryAction` で「貸出中」「削除できません」「返却」の3要素キーワードが含まれ末尾が `してください。` で終わり 20 文字を超えること、(b) `DeleteLedgerRow_LentRecord_DoesNotProceedToAuthenticationOrDelete` で警告表示後に `IStaffAuthService.RequestAuthenticationAsync` と `ILedgerRepository.DeleteAsync` のいずれも呼ばれないこと、(c) `DeleteLedgerRow_NullLedger_DoesNothing` で `null` 渡しの早期 return を維持していること、を回帰防止する。本 PR スコープは `MainViewModel.cs:1579` の 1 箇所のみで、他 `MessageBox.Show` 呼び出しは error-messages.md の段階適用方針に従い別 Issue で対応する（#1486）
- データエクスポート/インポートダイアログ（F4）でインポート → データ種別「利用履歴」→ インポート先カード「カードをタッチして指定」中に未登録の交通系ICカードをタッチすると、`MainViewModel.OnCardRead` 起点の `CardTypeSelectionDialog`（「未登録のカードです。どのように登録しますか？」）と `DataExportImportViewModel.OnCardRead` 起点の `DialogService.ShowWarning`（「未登録カード」）が**同時に表示**されてユーザーが矛盾した指示を受ける UX 不具合を修正。原因は `ICardReader.CardRead` を `MainViewModel` と `DataExportImportViewModel` の 2 つが同時購読しており、データインポート側に Issue #852 由来の `CardReadingSuppressedMessage` 抑制機構が組み込まれていなかったこと。修正内容: (a) `CardReadingSource` enum に `DataImport` を追加、(b) `DataExportImportViewModel` の constructor で `IMessenger` を required 注入、(c) `[ObservableProperty]` が生成する `partial void OnIsWaitingForCardTouchChanged(bool value)` をフックして待機状態の変化（true→抑制 ON / false→抑制 OFF）を一箇所に集約。これにより `StartCardTouchAsync` / `CancelCardTouch` / `OnCardRead` 完了 / `ClearTargetCard` / `Cleanup` / `OnSelectedImportTypeChanged` で利用履歴以外に切替 / `StartReadingAsync` 例外 catch のすべての経路で抑制 ON/OFF が自動同期され、抑制漏れ・解除漏れの再発を構造的に防止する。回帰防止として `DataExportImportViewModelMessagingTests` を新規追加し、(1) リーダー接続時の `StartCardTouchAsync` で抑制 ON が送信される、(2) リーダー未接続時は抑制メッセージが送信されない、(3) `CancelCardTouch` で抑制 OFF が送信される、(4) `ClearTargetCard` で抑制 OFF が送信される、(5) `Cleanup` で抑制 OFF が送信される、(6) 待機中に利用履歴以外へ画面切替すると抑制 OFF が送信される、(7) `StartReadingAsync` 例外時には ON→OFF の 2 連発火が起きる、の 7 ケースを検証する（#1514）
- UI 文言に残存していた「ICカード」単独表記を「交通系ICカード」に統一。`.claude/rules/development-conventions.md` の用語ルール（本システムは「職員証」と「交通系ICカード」の2種類を扱うため、交通系ICカードを指す場合は必ず「交通系ICカード」と記載し、単に「ICカード」とは書かない）に違反する箇所として、`VirtualCardDialog.xaml` の4箇所（L12 `AutomationProperties.HelpText`、L25 説明 TextBlock、L71 `AutomationProperties.HelpText`、L117 タッチ実行ボタンの `ToolTip`）と、`DataExportImportViewModel.cs:1009` の未登録カード警告 MessageBox（「対象のICカードを登録してください」→「対象の交通系ICカードを登録してください」）を修正した。`VirtualCardDialog` は DEBUG 専用画面だが `AutomationProperties` テキストはスクリーンリーダーで読み上げられる可能性があり、`DataExportImportViewModel` の MessageBox は本番運用で表示されるため放置すれば職員証と交通系ICカードの混同を招きうる。回帰防止として `UserFacingTextConventionTests` を新規追加し、(a) `Views/**/*.xaml` 配下の `Text` / `Content` / `Header` / `ToolTip` / `AutomationProperties.Name` / `AutomationProperties.HelpText` / `Title` / `Watermark` 属性、(b) `ViewModels/**/*.cs` 配下の `MessageBox.Show` / `_dialogService.ShowWarning` / `ShowError` / `ShowInformation` / `ShowConfirmation` / `ShowToast*` / `SetStatus` 呼び出しの第1引数文字列リテラル、を静的解析し、許可リスト（`交通系ICカード` / `仮想交通系ICカード` / `ICカードリーダー` / `ICカードリーダ` / `ICカード管理`）を取り除いてもなお「ICカード」が残るケースを検出する。検出ロジック自身の死を防ぐためのセルフテスト 10 ケース（違反入力で違反検出、許容入力で非違反、空文字・職員証のみのケース等）も合わせて追加（#1460）
- トースト通知（`ToastNotificationWindow.ApplyStyle`）とダイアログ（`LedgerDetailDialog.xaml`、`BusStopInputDialog.xaml`、`CardTypeSelectionDialog.xaml`、`DataExportImportDialog.xaml`）、および `CardBalanceDashboardItem.RowBackgroundColor` に残存していたカラーリテラル直書きを撤廃し、Issue #1392 で確立した「色値の Single Source of Truth は `AccessibilityStyles.xaml` のブラシキー」原則を全箇所で徹底した。具体的修正: (a) `ToastNotificationWindow.ApplyStyle` の `Color.FromRgb(255,243,224)` 等 25 個の `SolidColorBrush` 直生成を、`Application.Current.TryFindResource` 経由で `LendingBackgroundBrush` / `ReturnBackgroundBrush` / `ErrorBackgroundBrush` / `WaitingForegroundBrush` 等を解決する `ResolveBrush` ヘルパーに置換。(b) `LedgerDetailDialog.xaml` の `DataTrigger` 内 `Setter Value="#E3F2FD"` 等 10 個のリテラルを `GroupColor1〜5` / `GroupBadgeColor1〜5`（後者は新規追加、`#1976D2` / `#388E3C` / `#F57C00` / `#7B1FA2` / `#D32F2F`）の `StaticResource` 参照に変更。(c) 3 ダイアログのヒントテキスト色 `Foreground="#795548"` を、`AccessibilityStyles.xaml` に新設した `HintForegroundBrush`（マテリアル Brown 700）の `DynamicResource` 参照に統一。(d) `CardBalanceDashboardItem.RowBackgroundColor`（`"#FFEBEE"` 文字列返却）を `RowBackgroundResourceKey`（`"ErrorBackgroundBrush"` / `"Transparent"` キー名返却）にリネームし、`MainWindow.xaml` 側で新規 `ResourceKeyToBrushConverter` 経由でブラシ解決する形に変更。これにより `AccessibilityStyles.xaml` の単一修正で文字サイズ可変・色覚多様性対応・将来のテーマ切替がトースト／ダイアログ／ダッシュボードすべてに伝搬するようになった。回帰防止として `CardBalanceDashboardItemTests`（3 件、リソースキー名返却・カラーリテラル不返却を検証）、`ResourceKeyToBrushConverterTests`（6 件、null/空/Transparent/未登録キーで `Brushes.Transparent` フォールバック・`ConvertBack` 例外を検証）、`AccessibilityStylesResourceKeysTests`（13 件、`HintForegroundBrush` を含む必須ブラシキー 11 種の存在と Brown 700 色値を XAML テキスト検査で検証）を追加（#1461）
- 職員証認証ダイアログ（`StaffAuthDialog`）のスクリーンリーダー（NVDA / Narrator）への即時通知が発火しない不具合を修正。PR #1500（Issue #1468）で `StatusText` に `AutomationProperties.LiveSetting="Assertive"` を付与したが、(1) `StatusBorder` の初期 `Visibility="Collapsed"` により子要素 `StatusText` が AutomationTree から除外されていた、(2) WPF UI Automation の LiveRegion は「既に可視な要素の Text 変化」のみを通知し Visibility 変化に伴う要素出現は対象外、(3) `TextBlock.Text` 更新だけでは LiveRegionChanged が確実に発火しない、という 3 つの原因で実機検証時に沈黙していた。修正内容: (a) `StatusBorder` を常時可視化（`Background="Transparent" BorderThickness="0" MinHeight="44"`）して AutomationTree に常駐させ、`Text` 設定時に code-behind から背景色・枠を切り替える、(b) `ShowStatus()` 末尾で `UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を明示呼び出してスクリーンリーダー通知を発火、(c) 認証成功時にも `ShowStatus($"認証に成功しました（{職員名}）", false)` を呼び出して 700ms 表示してから閉じる（タイムアウト失敗側と同じ `CloseAfterDelay` テンプレートで共通化）。同時に `ShowStatus()` 内の色値リテラル直接指定（`Color.FromRgb(0xFF, 0xEB, 0xEE)` 等）を `AccessibilityStyles.xaml` のブラシキー（`ErrorBackgroundBrush` / `SuccessBackgroundBrush` / `ErrorBorderBrush` / `SuccessBorderBrush` / `ErrorForegroundBrush` / `SuccessForegroundBrush`）を `FindResource` 経由で参照する形に統一し、Issue #1392 の色値 Single Source of Truth 原則違反も解消した。回帰防止として `DialogAutomationPropertiesCoverageTests` に静的検査 4 件（StatusBorder の初期 Visibility が Collapsed でないこと、code-behind に `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` 呼び出しが含まれること、認証成功パスに `ShowStatus` 呼び出しが含まれること、`ShowStatus` 本体に色値リテラルが含まれないこと）を追加し、UI Automation 統合テスト 3 件（`ICCardManager.UITests/Tests/StaffAuthDialogLiveRegionTests.cs`）で FlaUI 5.0 を使った StatusText の UIA tree 到達性と Text 反映を検証する。`docs/design/03_画面設計書.md` §5.7 と `docs/design/07_テスト設計書.md` §2.45 (UT-058a に 4 行追加) と §2.46a (UT-058b 新設) を同期更新（#1509）
- `DbContext.HandleLegacyDatabase` がマイグレーション機構導入前のレガシー DB に対して `schema_migrations` テーブルと version=1 行を補填する処理で、INSERT 文に `OR IGNORE` が無く、共有モードで複数 PC が同時に初回起動した際の TOCTOU 競合（PC1 が「テーブル無し」と判定した直後に PC2 が補填を完了させ、PC1 の INSERT が `UNIQUE constraint failed: schema_migrations.version` で失敗する経路）に対して脆弱だった問題を修正。`.claude/rules/migrations.md` の冪等性ガイドライン（「素の `INSERT` ではなく `INSERT OR IGNORE` を使う」）に準拠させた。修正の本質は INSERT 文 1 行への `OR IGNORE` 付与のみだが、テスト容易性のため補填処理（`CREATE TABLE IF NOT EXISTS` + `INSERT OR IGNORE`）を `HandleLegacyDatabase` から `internal static BackfillLegacyMigrationVersion1(SQLiteConnection)` に切り出し、外側のテーブル存在チェック（`hasMigrationTable` で早期 return する経路）を経由せずに直接 2 回呼び出して冪等性を検証できる構造とした。回帰防止として `DbContextMigrationTests` に `BackfillLegacyMigrationVersion1_CalledTwice_DoesNotThrowAndKeepsSingleRow` を追加し、(a) 同一接続で `BackfillLegacyMigrationVersion1` を 2 回連続実行しても `SQLiteException`（UNIQUE 制約違反）が発生しないこと、(b) `schema_migrations` の `version=1` 行が 2 件に増殖せず 1 件のまま維持されること、を固定する。修正前のコード（素の `INSERT INTO`）で先に RED 確認（`UNIQUE constraint failed: schema_migrations.version` を確認）してから `INSERT OR IGNORE INTO` に変更し GREEN を確認した。`docs/design/07_テスト設計書.md` §2.24 UT-031 に No.8 を追記して同期更新（#1484）
- 操作ログダイアログ（`OperationLogDialog`）の検索条件 Border 内でウィンドウ幅を狭めると **終了日 DatePicker が右側でクリップ** され「20...」のように省略表示されて日付値の確認・編集が事実上できなくなる不具合を修正。原因は Row 0 に 期間 DatePicker StackPanel（Col 1、Width="*"）＋ 操作種別 ComboBox（Col 3、Width="*"）＋ 対象 ComboBox（Col 5、Width="*"）の 3 ブロックを並列配置していたため、`MinWidth=800` 時に Col 1 が約 180px しか割り当てられず、期間 StackPanel の希望幅 ~270px（DatePicker 120 + 「～」30 + DatePicker 120）が確保できずに終了日が Grid セル境界でクリップされていたこと。Issue #1505 と同根の問題だが、PR #1521 ではクイックフィルタの行分離のみ対応し、期間 DatePicker 自体のクリップが残存していた残存パターン。修正内容（案 B: 期間 StackPanel ColumnSpan 拡大 + 操作種別/対象の Row 1 退避）: (a) 期間 StackPanel に `x:Name="DateRangePanel"` を付与し `Grid.ColumnSpan="5"` で Col 1-5 を全幅占有させて DatePicker 2 つを常に確実に表示、(b) 操作種別 ComboBox と対象 ComboBox を Row 0 から Row 1 (Col 0/1 と Col 2/3) に退避、(c) クイックフィルタ `QuickFilterPanel` を Row 1 → Row 2 に繰り下げ、(d) 対象ID / 操作者名 / 検索ボタン群を Row 2 → Row 3 に繰り下げ、(e) `Grid.RowDefinitions` を 3 行構成から 4 行構成に拡張。検索条件 Grid 全体の意味的グループ化（Row 0=期間 / Row 1=絞り込み ComboBox / Row 2=クイックフィルタ / Row 3=絞り込みテキストボックス + 検索ボタン）も明確化された。回帰防止として静的解析テスト `OperationLogDialogQuickFilterLayoutTests` を Issue #1505 から拡張: (a) `Filter_grid_should_have_three_row_definitions` を `Filter_grid_should_have_four_row_definitions` にリネームしアサーションを `Be(3)` → `Be(4)` に更新、(b) `QuickFilterPanel_should_occupy_dedicated_row_with_full_span` の Row アサーションを `Grid.Row="1"` → `Grid.Row="2"` に更新、(c) 新規 `DateRangePanel_should_span_all_star_columns_on_row0`（期間 StackPanel の `Grid.Row="0"` / `Grid.Column="1"` / `Grid.ColumnSpan="5"` の 3 属性を検証）を追加、(d) 新規 `Row0_should_not_contain_action_or_target_combobox`（操作種別 / 対象 ComboBox の `Grid.Row` 値が `"0"` 以外であることを `[Theory]` で 2 ケース検証、属性順序非依存の正規表現で XAML Styler 等の整形ツール耐性も確保）を追加。`docs/design/03_画面設計書.md §3.13` の ASCII モックアップとクイックフィルタ行分離注記を 4 行構成へ更新、`docs/design/07_テスト設計書.md §UT-058c` のテストケース表を 10 件 → 13 件に拡張し設計判断と手動検証項目に Issue #1523 関連の検証（ウィンドウ最小幅 800px で終了日 DatePicker が完全表示・操作種別/対象 ComboBox の Row 1 独立表示・期間 DatePicker との矩形非衝突）を追記（#1523）
- 共有モード判定が **ローカルフォルダのフルパスを指定するだけで誤って共有モードとして動作する** 不具合を修正し、合わせて誤設定からの復旧手段を UI に追加した。`DbContext.cs:222` の `IsSharedMode = databasePath != null;` は「`database_config.txt` に何か書いてあれば共有モード」という実装になっており、設定画面（F5）で `C:\Users\foo\db\iccard.db` のようなローカルパスを指定すると `busy_timeout=15s` / `journal_mode=DELETE` / `SharedModeMonitor` 起動 / 15 秒キャッシュ TTL がすべて発動して、想定外のパフォーマンス低下と Issue #1470 で追加した「⚠ 共有モード（切断中）」警告の誤発火を起こしていた。CLAUDE.md / `business-logic.md` / `development-conventions.md` の記載は「UNCパス（`\\server\share`）指定時に自動的に共有モード」であり、実装と設計書が乖離していた。修正方針（案 C：折衷案）: (a) `DbContext` に `internal static bool IsNetworkDrive(string path)` を新設し、`Path.GetPathRoot` でルートを取得 → `new DriveInfo(root).DriveType == DriveType.Network` でマップドネットワークドライブ（`Z:\share\iccard.db` 等）を判定、例外（null / 不正パス / 未マウントドライブ等）時は `false`（ローカル扱い）にフォールバック、(b) `IsSharedMode = databasePath != null && (IsUncPath(databasePath) || IsNetworkDrive(databasePath))` に変更してローカルフルパス指定では共有モードを発動させない、(c) テスト容易性のため `internal DbContext(string path, ILogger<DbContext> logger, bool? forceSharedMode)` コンストラクタを追加（ローカル一時ファイル + 共有モード挙動の組合せ検証が可能。production の `public` コンストラクタは `forceSharedMode: null` で従来通り）、(d) `SettingsViewModel` に `IDialogService` を DI 注入し、`ResetDatabasePathToDefaultCommand`（確認ダイアログ → `database_config.txt` を `File.Delete` → `DatabasePath` を空欄 → `IsDatabasePathChanged=true` で再起動案内）を追加、(e) `SettingsDialog.xaml` の「データベース保存先」 GroupBox 内、参照ボタン直下に「デフォルトに戻す(_R)」ボタンを追加（ToolTip でデフォルトパス `C:\ProgramData\ICCardManager\iccard.db` を案内、`AutomationProperties.Name` でスクリーンリーダー対応）。回帰防止として `DbContextSharedModeDetectionTests` を新規追加（8 件: デフォルトパス false / ローカルフルパス false / UNC パス true / `IsNetworkDrive` の null・空文字・空白・ローカル・UNC で false）、既存の旧仕様前提テスト 6 件を修正（`DbContextSharedModeTests.IsSharedMode_パスを明示指定した場合trueであること` を `IsSharedMode_UNCパスを指定した場合trueであること` + `IsSharedMode_ローカルフルパスを指定した場合falseであること` の 2 件に分割、`DbContextResilienceTests.BusyTimeout_共有モードで15000msであること` / `DbContextConcurrentAccessTests.ExecuteWithRetryAsync_共有モードで最大5回リトライすること` / `BackupServiceRestoreSafetyTests.IsSharedMode_DbContextの状態を反映すること` を UNC パス指定へ、`DbContextResilienceTests.GetConnection_共有モードでbusy_timeoutが15000msに設定されること` / `DbContextConcurrentAccessTests.InitializeDatabase_ネットワークフォルダが存在しない場合にIOExceptionをスローすること` は実接続が必要なため新設 internal コンストラクタの `forceSharedMode: true` を使用）、`SettingsViewModelDatabasePathTests` を新規追加（3 件: 確認 OK で config 削除 + UI 状態更新 / キャンセルで config 保持 + フラグ false 維持 / config 不在でも安全実行）。`.claude/rules/business-logic.md` の共有フォルダモード仕様と `.claude/rules/development-conventions.md` の環境制約セクションを「UNCパス または マップドネットワークドライブ指定時に自動判定」に同期更新（#1559）

**訂正**
- v2.8.0（Issue #1468）の「認証画面では認証ステータス変化（成功・失敗）と残り時間カウントダウンの読み上げまで網羅」の記述は事実誤認だった。属性は付与されたが実機で読み上げが発火していなかった。本リリース（Issue #1509）で実通知発火を実装してこの主張を有効化した（#1509）

**コード品質改善**
- `VirtualCardDialog`（DEBUG 専用の仮想 ICカードタッチ設定ダイアログ、Issue #640）を Release ビルドから物理的に除外し、4 重ガードによる完全隔離を CI で固定した。これまで起動経路 3 つ（`MainWindow.xaml` の `Visibility="{Binding ... app:App.IsDebugBuild ...}"` / `MainViewModel.OpenVirtualCardAsync` 全体の `#if DEBUG` / `App.xaml.cs` の `AddTransient<VirtualCardDialog>` および `AddTransient<VirtualCardViewModel>` の `#if DEBUG`）は `#if DEBUG` で囲まれていたが、`Views/Dialogs/VirtualCardDialog.xaml` 本体は WindowsDesktop SDK の自動 `<Page>` 検出により Release dll にも `ICCardManager.Views.Dialogs.VirtualCardDialog` 型と XAML リソース（baml）が残置されていた。加えて code-behind の `#else` 側に引数なし `public VirtualCardDialog()` コンストラクタが定義されており、もし将来 Release コードに `new VirtualCardDialog()` が混入すれば DI バイパスで開けてしまう副作用があった。修正内容: (a) `ICCardManager.csproj` に `<ItemGroup Condition="'$(Configuration)'=='Release'">` を追加し、`<Compile Remove="Views\Dialogs\VirtualCardDialog.xaml.cs" />` と `<Page Remove="Views\Dialogs\VirtualCardDialog.xaml" />` を宣言。これにより Release ビルドの `obj/Release/net48/Views/Dialogs/` 下に `VirtualCardDialog.baml` / `VirtualCardDialog.g.cs` が生成されなくなり、`bin/Release/net48/ICCardManager.exe` を `strings` 走査しても `VirtualCardDialog` / `VirtualCardViewModel` / `OpenVirtualCardAsync` の文字列が一切現れないことを実機確認。(b) `VirtualCardDialog.xaml.cs` から `#else` ブロックと引数なしコンストラクタを撤去し、クラス宣言ごと `#if DEBUG / #endif` で囲んで Release コンパイル対象外であることをソース上も明示。回帰防止として `VirtualCardDialogDebugIsolationTests` を新規追加し、(1) csproj に Release 構成専用 ItemGroup と Compile/Page Remove 宣言が存在、(2) `MainViewModel.OpenVirtualCardAsync` が `#if DEBUG` ブロック内、(3) `App.xaml.cs` の `AddTransient<Views.Dialogs.VirtualCardDialog>` と `AddTransient<VirtualCardViewModel>` がいずれも `#if DEBUG` ブロック内、(4) `MainWindow.xaml` の `OpenVirtualCardCommand` ボタンが `app:App.IsDebugBuild` バインドの StackPanel 内に存在（閉じタグより前に出現）、(5) `VirtualCardDialog.xaml.cs` に `#else` と引数なしコンストラクタが存在しない、の 5 経路を静的解析で固定する（加えて検出ロジック `IsInsideIfDebugTakenBranch` 自身の `Theory` セルフテスト 6 ケースで「常に成功する死んだテスト化」を防止）。`docs/design/03_画面設計書.md` §3.20 に「Release ビルドからの除外（Issue #1487）」セクションを追加して 4 重ガードの全体像を表で記載、`docs/design/05_クラス設計書.md` §9.3 に Release 除外の補足、`docs/design/07_テスト設計書.md` §2.49 に UT-064 を追加（#1487）
- `EnsureDirectoryWithPermissions` を `EnsureDirectoryExists` にリネーム（`Data/DbContext.cs` の `internal static`、`Services/BackupService.cs` / `Infrastructure/Logging/FileLoggerProvider.cs` の `private static` の 3 箇所すべて）。Issue #1455 でランタイム ACL 操作（`BUILTIN\Users : FullControl` の `AddAccessRule` 付与）を撤廃した結果、本メソッドは実体上 `Directory.CreateDirectory` の薄いラッパーに縮退していたが、メソッド名は「`WithPermissions`」（権限を伴う）の名残を残しており、(1) `Goto Definition` で本文を読まずに呼び出した開発者が「ここで権限設定しているはず」と誤認するリスク、(2) 過剰権限を再導入する誤った修正を誘発する可能性（「権限関連のメソッドだから ACL を扱うべき」という思い込み）、を抱えていた。`.NET BCL` の `File.Exists` / `Directory.Exists` と連続するセマンティクスを持つ `EnsureDirectoryExists`（「ディレクトリの存在を保証する」述語形）へ統一し、命名と挙動の乖離を解消した。`internal static` の `DbContext` 側は `InternalsVisibleTo` 経由でテストから直接呼ばれているため `DbContextFilePermissionsTests` の 4 テストメソッド名・呼び出しもまとめてリネーム（`EnsureDirectoryWithPermissions_新規ディレクトリの場合_ディレクトリが作成される` → `EnsureDirectoryExists_新規ディレクトリの場合_ディレクトリが作成される` 等）。XML doc コメントは「旧名 `EnsureDirectoryWithPermissions`、Issue #1499 でリネーム」と Issue 番号を明示してリネーム経緯を辿れるようにし、`<see cref="...DbContext.EnsureDirectoryWithPermissions"/>` の参照（`BackupService` / `FileLoggerProvider`）も新名に置換。挙動は完全に不変（純粋なリネーム）。`docs/design/07_テスト設計書.md §6 行 1-7` と `docs/manual/開発者ガイド.md §5.4` のコード例も同期更新（Issue #1455 の CHANGELOG エントリ自体は当時のメソッド名を歴史的記述として保存）（#1499）
- `LedgerRepository.GetByIdAsync` および `GetLentRecordAsync` が利用履歴本体と詳細を別々の SELECT で取得していた構造を、複数結果セット方式（1 つの `CommandText` に SELECT 2 本を `;` 連結し `DbDataReader.NextResultAsync` で 2 つ目の結果セットへ進む）へ書き換え、SQLite 接続への送受信を 2 RTT から 1 RTT に削減した。SMB 共有モードでは詳細編集ダイアログ表示時のレスポンスが 5〜20ms 短縮される。`GetLentRecordAsync` 側は本体 SELECT 段階で `id` が確定しないため、詳細 SELECT の WHERE 句で同条件のサブクエリ（`card_idm` / `is_lent_record=1` / `lent_at DESC LIMIT 1`）を再評価して該当 ledger_id を解決する形にした。SQLite オプティマイザは同条件のサブクエリを単一スキャンで処理するため可読性のコスト以上の RTT 削減効果が得られる。2 つの SELECT 結果を読み出す共通処理は `private static async Task<List<LedgerDetail>> ReadAndSortDetailsAsync(DbDataReader reader)` ヘルパーに切り出して両メソッドから利用し、`NextResultAsync` → `MapToLedgerDetail` ループ → `LedgerDetailChronologicalSorter.Sort(preserveOrderOnFailure: true)` を一箇所に集約した。既存の `private GetDetailsAsync(int)` はテスタビリティ確保と将来の汎用呼び出しを考慮して残置。`ILedgerRepository` インターフェースは変更なし。回帰防止として `LedgerRepositoryTests` に 3 件追加: (a) 本体と詳細 2 件が同じ呼び出しで両方マップされる、(b) 本体ありで詳細 0 件のとき Details が空リスト（複数結果セットの 2 つ目が空でも例外にならない）、(c) 複数の貸出中レコードがあるとき `GetLentRecordAsync` が `lent_at` 最新の本体に紐づく詳細のみを返す（サブクエリが本体 SELECT と同じ id を解決する）。Local モードでは誤差レベルだが共有モードで体感速度向上（#1478）
- `PathValidator.ValidateUncPathFormat` の冗長な三項演算子（`path.StartsWith(@"\\") ? path.Substring(2) : path.Substring(2)`）を削除し、`path.Substring(2)` の単純呼び出しに置換。`\\` と `//` のプレフィックスは両者とも 2 文字長で一致するため分岐は本来不要だった。同関数内の `ValidateBackupPath` ステップコメントで `// 8.` が 2 回連続していた番号付け誤りも `// 9.` へ修正。Issue #1483 のレビュー指摘（`uncReachabilityChecker` パラメータの二重宣言疑い）は実コード上は二重宣言が存在しないことを精査で確認したが、同一ファイル内に類似の冗長性（同じ式を 2 度書く三項演算子）が見つかったため、Issue の趣旨である可読性改善として整理した。動作変更なし。回帰防止として `PathValidatorTests` に `Theory` で `\\server\share\backup` と `//server/share/backup` 等 3 組の入力ペアを比較し、両プレフィックスが UNC 形式検証で同じ判定（「サーバー名と共有名が必要」等の形式エラー有無）を返すことを固定する `ValidateBackupPath_BothUncPrefixVariants_ProduceSameFormatVerdict` を追加（#1483）
- `LendingService` の貸出／返却／カード登録時履歴インポート経路における ledger ヘッダ＋複数 ledger_detail 書込みのトランザクション境界を、コードから読み取り可能な形へ整理した。実挙動としては `LendingService.InsertLendLedgerAsync` / `PersistReturnAsync` / `ImportHistoryForRegistrationAsync` が既に `_dbContext.BeginTransactionAsync()` で全体を囲んでおり、内部の `_ledgerRepository.InsertAsync` / `InsertDetailAsync` は **同一 SQLiteConnection 上で BEGIN 発行後の SQLiteCommand が autocommit にならず当該トランザクションに暗黙参加する** SQLite のセマンティクスにより SMB 切断時にも ALL OR NOTHING で書込みされていた。一方で Issue 提起時のレビューで「各 Insert が autocommit に見える」と読み取られたとおり、コードから境界が読み取れない脆さがあった。改善内容: (a) `ILedgerRepository` / `LedgerRepository` に `SQLiteTransaction` を明示的に受け取る新オーバーロード（`InsertAsync(Ledger, SQLiteTransaction)` / `UpdateAsync(Ledger, SQLiteTransaction)` / `InsertDetailAsync(LedgerDetail, SQLiteTransaction)` / `InsertDetailsAsync(int, IEnumerable<LedgerDetail>, SQLiteTransaction)`）を追加。新オーバーロードは `command.Transaction = transaction` を明示設定して `SQLiteCommand` を渡されたトランザクションに参加させる ADO.NET 標準パターンに統一する。`transaction == null` の経路は従来通り `_dbContext.LeaseConnectionAsync()` で接続を取得する。(b) `LendingService.CreateUsageLedgersAsync` に `SQLiteTransaction transaction = null` 引数を追加し、内部で `InsertLedgerInTransactionAsync` / `UpdateLedgerInTransactionAsync` / `InsertDetailInTransactionAsync` / `InsertDetailsInTransactionAsync` の 4 ヘルパー経由で「tx 非 null なら新オーバーロード、null なら既存オーバーロード」を呼び分ける構造に整理。(c) `InsertLendLedgerAsync` / `PersistReturnAsync` / `ImportHistoryForRegistrationAsync` の 3 つの本番経路にコメントを追加し、暗黙参加の依存条件（同一接続・BEGIN 後の SQLiteCommand）と境界の責務（呼出元が `BeginTransactionAsync` を保持）を明示。本 PR スコープでは `LendingService` 経路は引き続き引数 1 版を呼び出し既存テストの `Mock<ILedgerRepository>` 設定（38 件超）を破壊しないが、新オーバーロード API が整備されたことで将来的に `scope.Transaction` を伝搬する明示参加への段階移行が可能になった。Issue 本文で言及された「`_operationLogger.LogLedgerInsertAsync` も同一トランザクションに含める / Channel 等のキューに乗せる」は `LendingService` 内では呼ばれていない（`LedgerRowEditViewModel.cs:610` でのみ使用）ため本 Issue のスコープ外として別 Issue 推奨。回帰防止として `LedgerRepositoryTransactionTests`（6 件）を新規追加し、(1) `InsertAsync(ledger, scope.Transaction)` で `scope.Commit()` 後にデータが永続化される、(2) `scope.Rollback()` 後にデータが残らない、(3) ledger ヘッダと複数 detail を同一 tx で書いた後 Rollback すると両方とも消える（ALL OR NOTHING）、(4) `InsertDetailsAsync(ledgerId, details, scope.Transaction)` で複数 detail が同一 tx で永続化される、(5) `UpdateAsync(ledger, scope.Transaction)` で Rollback 時に元値が保たれる、(6) `transaction: null` で新オーバーロードを呼ぶと既存挙動と等価、を固定する。既存テスト 3,220 件は全件 GREEN を維持（#1481）

**アクセシビリティ改善**
- 操作ログダイアログ（`OperationLogDialog`）と職員証認証ダイアログ（`StaffAuthDialog`）の `AutomationProperties.Name` / `AutomationProperties.HelpText` カバレッジを拡充し、スクリーンリーダー（NVDA / Narrator）でも検索条件・ページネーション・エクスポート操作を識別できるようにした。Issue #1468 のリポジトリ全体レビュー（UX 観点）で判明した「`OperationLogDialog` 17%、`StaffAuthDialog` 3 件しか付与されていない」状態を、業務監査画面（操作ログ）では検索ボタン・クリアボタン・対象ID/操作者名/操作種別/対象テーブルの絞り込み入力・期間クイック選択（今日/今月/先月）・ページネーション 4 ボタン・1ページ表示件数・Excel エクスポート・出力ファイルを開く・処理中オーバーレイまで、認証画面では認証ステータス変化（成功・失敗）と残り時間カウントダウンの読み上げまで網羅。動的に `Text` が書き換わる `TextBlock`（職員証認証ダイアログの `OperationDescriptionText` / `StatusText` / `TimeoutText`、操作ログダイアログのページ情報・現在ページ番号・検索ステータス・処理中メッセージ）では `AutomationProperties.Name` を**敢えて付与しない**設計とした。WPF の `TextBlockAutomationPeer.GetNameCore` は既定で `Text` を返すが、`AutomationProperties.Name` を明示すると Name 値が優先されて `Text` の動的更新がスクリーンリーダーへ通知されなくなるため、`HelpText` で補足情報を付与しつつ `LiveSetting` で変化通知を行う形に統一した。ステータス変化（成功・失敗）は `Assertive`（即時通知）、進捗・カウントダウンなど頻繁な更新は `Polite`（読み上げ阻害回避）で使い分け。回帰防止として `DialogAutomationPropertiesCoverageTests`（39件）を新規追加し、(a) 各ダイアログの `Name`/`HelpText` 出現回数が修正時点の最低水準を下回らないこと、(b) 主要操作（検索を実行・検索条件をクリア・対象ID・操作者名・操作種別・対象テーブル・Excelファイルにエクスポート・最初のページへ移動・最後のページへ移動・1ページあたりの表示件数）に個別 Name が付与されていること、(c) `StaffAuthDialog` の `StatusText` が `Assertive` / `TimeoutText` が `Polite` の `LiveSetting` を持つこと、(d) `OperationLogDialog` のステータスメッセージが `Polite` で変化通知すること、(e) 動的更新 `TextBlock`（`OperationDescriptionText` / `StatusText` / `TimeoutText`）に `AutomationProperties.Name` が**付かない**こと、を静的解析で検証する。実際にスクリーンリーダーで読み上げられるかは UI 自動化を要するため、PR テストプランで NVDA / Narrator による手動検証を行う（#1468）
- `OperationLogDialog` の動的 TextBlock 4 要素（`PageInfoText` / `CurrentPageNumberText` / `StatusMessageText` / `ProcessingOverlayText`）が `AutomationProperties.LiveSetting="Polite"` を付与済みにもかかわらず、テキスト変化時に NVDA / Narrator で読み上げが**発火しない**バグを修正。原因は WPF の `TextBlock.Text` バインド更新だけでは `LiveRegionChanged` イベントが確実に発火せず、`AutomationProperties.LiveSetting` は変化通知の意図を宣言するのみで実際の発火には `UIElementAutomationPeer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` の明示呼び出しが必要であること（Issue #1509 で `StaffAuthDialog` に対して既に確立されていた知見）。`StaffAuthDialog` ではコードビハインドが `StatusText.Text = message;` のように Text を直接代入する設計のため代入直後に発火を呼べたが、`OperationLogDialog` は ViewModel バインディング駆動のため同パターンを直接転写できなかった。修正方針: `OperationLogDialog.xaml.cs` のコードビハインドで `DataContext`（= `OperationLogSearchViewModel`）の `INotifyPropertyChanged.PropertyChanged` を購読し、`PageInfo` / `CurrentPage` / `TotalPages` / `StatusMessage` / `BusyMessage` / `IsBusy`（`true` への遷移時のみ）変化時に対応する TextBlock の `UIElementAutomationPeer` に対して `RaiseAutomationEvent(AutomationEvents.LiveRegionChanged)` を呼び出す。プロパティ名 → 対象 TextBlock 名 のマッピングは `internal static GetTargetElementName(string?, bool)` に分離して WPF UI スレッド依存を排除し、`Xunit.StaFact` パッケージ追加なしで純粋関数として単体テスト可能にした。`Window.Closed` で `PropertyChanged` を解除してメモリリーク防止。`OperationLogDialogLiveRegionTests` を新規追加（10 件: Theory 5 + 個別 Fact 2 + 対象外プロパティ Theory 3）し、`DialogAutomationPropertiesCoverageTests` に静的解析テスト 2 件（`RaiseAutomationEvent` / `AutomationEvents.LiveRegionChanged` 文字列存在 + `PropertyChanged` 購読・解除パターン存在）を追加して将来のリグレッションを CI で検知できるようにした。スクリーンリーダー実機読み上げ確認は単体テストで不可能なため、PR テストプランで NVDA / Narrator による手動検証（ページ送り通知・StatusMessage 通知・処理中オーバーレイ通知・起動時誤発火なし）を行う。`docs/design/07_テスト設計書.md §1.1a` 件数表を 3,253 → 3,265 件、`§2.50 OperationLogDialog の LiveRegion 発火` を UT-066 / UT-067 で新設。設計書 `ICCardManager/docs/superpowers/specs/2026-05-19-issue-1548-operationlog-liveregion-firing-design.md` に方針（アプローチ A: ViewModel.PropertyChanged 購読方式採用、添付プロパティ案や Microsoft.Xaml.Behaviors 導入案は YAGNI でノンゴール）と試験戦略を記録（#1548）
- Issue #1548 マージ前のスクリーンリーダー実機検証（Issue #1507）で、ページ送りボタン（◀/▶/«/»）操作後の `CurrentPageNumberText` だけが Narrator に読み上げられない残課題が判明し、複数段階の追加修正を実施。原因は当該 TextBlock が `<Run Text="{Binding CurrentPage}"/>` / `<Run Text=" / "/>` / `<Run Text="{Binding TotalPages}"/>` / `<Run Text=" ページ"/>` の 4 つの `<Run>` で構成されており、`<Run>` の `Text` 変更は親 `TextBlock` の `Text` プロパティ更新を伴わないため `TextBlockAutomationPeer` の Name キャッシュが invalidate されず、コードビハインドで `RaiseAutomationEvent(LiveRegionChanged)` を発火しても Narrator が新しいテキスト内容を取得できないという WPF の既知挙動。最終的な修正方針: (a) `OperationLogSearchViewModel` に派生プロパティ `PageNumberDisplay`（`$"{CurrentPage} / {TotalPages} ページ"`）を追加し `[NotifyPropertyChangedFor]` で `CurrentPage` / `TotalPages` 変化を自動伝搬、(b) XAML を `Text="{Binding PageNumberDisplay}"` の単一バインドに変更して `<Run>` 構成を解消、(c) コードビハインドの `GetTargetElementName` マッピングを `CurrentPage` / `TotalPages` 単体から `PageNumberDisplay` に集約、(d) `PageInfo` も `[NotifyPropertyChangedFor]` で `TotalCount` / `CurrentPage` / `PageSize` の setter から自動通知されるよう改善し `ApplyPage()` 内の手動 `OnPropertyChanged(nameof(PageInfo))` を削除（二重通知防止）、(e) `BusyMessage` のマッピングを `IsBusy=true` 時のみに限定し連続発火を抑制、(f) `RaiseAutomationEvent` を `Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ...)` で遅延発火し Binding の Text 更新と Narrator のフォーカス処理を待つ、(g) `CurrentPageNumberText` の `AutomationProperties.LiveSetting` を `Polite` → `Assertive` に変更、(h) ViewModel のページ送り 4 コマンド（`FirstPageAsync` / `PreviousPageAsync` / `NextPageAsync` / `LastPageAsync`）の最後で `AnnouncePageNavigation()` を呼び StatusMessage に「ページ N / M に移動しました（合計 X 件）」をセットすることで読み上げ実績がある StatusMessage ルート経由で補強。**既知制約**: Narrator はフォーカス位置のキー操作フィードバック（Space/Enter 押下）読み上げ中、Polite/Assertive 問わず Live Region 通知を抑制する仕様があり、ページ送りボタン上にフォーカスがある状態で連続送りした場合、中間ページの読み上げは Narrator に届かない場合がある（終端到達時にボタンが無効化されてフォーカスが外れたタイミングのみ確実に読み上げ）。視覚利用者にはステータスバーに「ページ 2 / 3 に移動しました…」と表示されるため情報量増加というメリットあり。`OperationLogSearchViewModelTests` に依存通知発火検証 6 件 + `FormatPageNavigationStatus` Theory 3 件、`DialogAutomationPropertiesCoverageTests` に `CurrentPageNumberText` の単一 Text バインド維持を静的解析で固定する回帰防止テスト 1 件、`OperationLogDialogLiveRegionTests` に `BusyMessage` の `IsBusy=false` 抑制検証を追加。`OperationLogDialogLiveRegionTests` の `Theory` データを新マッピング（`CurrentPage` / `TotalPages` 単体は対象外、`PageNumberDisplay` が対象）に同期。`docs/design/07_テスト設計書.md §1.1a` 件数表を Release 構成実測値（CI `test-count-sync` と整合）に同期し 3,277 件（単体テスト 3,251 件 + UI 26 件）、`§2.50` を Issue #1507 補強分（UT-068: ViewModel 依存通知発火検証、UT-069: `CurrentPageNumberText` 単一 Text バインド維持、UT-070: ページ送り完了時の StatusMessage アナウンスフォーマット）で拡張（#1507 / #1548）

**性能改善**
- 操作ログ検索画面（`OperationLogDialog`）の OFFSET ベースページネーションを **keyset pagination** に切り替えた。`OperationLogRepository.SearchAsync(criteria, page, pageSize)` を廃止し、`SearchFirstPageAsync` / `SearchNextPageAsync` / `SearchPreviousPageAsync` / `SearchLastPageAsync` の 4 メソッドに分割。各メソッドは `(timestamp, id)` 複合カーソルで「`WHERE timestamp > @ts OR (timestamp = @ts AND id > @id)`」のレンジスキャンを行い、`LIMIT pageSize+1` で +1 行余分に取得して `HasNext` / `HasPrevious` を判定する（最終ページのみ `totalCount % pageSize` で剰余ぴったりの行数を要求する補正あり）。改修前の OFFSET 方式は SQLite の線形コストにより `operation_log` が 6 年分（数十万行想定）蓄積した状態で末尾ページや最終ページへのジャンプに 0.1〜数秒を要していたが、keyset では既存の `idx_log_timestamp` インデックスのレンジスキャンで終了するため O(pageSize) となる。`OperationLogSearchViewModel.SearchAsync()` は Issue #787 の「最終ページ＝最新データを表示」要件を保ったまま、改修前の「先頭ページ取得→`TotalPages` を計算→最終ページ再取得」という 2 RTT を `SearchLastPageAsync` 一発（1 RTT）に短縮。並列して `CurrentPage` / `TotalPages` / `PageSize` / `PageInfo` の表示プロパティと「最初／前／次／最後」4 ボタン UI は維持されるため、ユーザー体感は「ボタン操作の応答性が向上」のみで操作手順は不変。Repository 層は新オーバーロード追加ではなく旧 `SearchAsync` を削除する破壊的変更を採用したが、外部依存は `OperationLogSearchViewModel` と既存テスト 2 ファイルのみで PR 内に完結する。回帰防止として `OperationLogRepositoryTests` に keyset 専用テスト 13 件を追加: (1) 先頭ページの基本動作（25 件で先頭 10 件を `timestamp ASC, id ASC` で取得）、(2) 空テーブルで `Items=[]` / カーソル `null`、(3) 行数 < pageSize で `HasNext=false`、(4) `SearchNextPageAsync` での順方向ナビゲーション（page2/page3 の境界と件数）、(5) 同一 `timestamp` 多発時に `id` がタイブレークすること（同 ts 5 件 + 別 ts 5 件で pageSize=3 の境界がタイ上にかかるケース）、(6) `SearchPreviousPageAsync` での逆方向ナビゲーション（Next→Next→Previous で page2 と一致）、(7) 先頭ページから Previous しても `HasPrevious=false`、(8) 末尾ページの剰余行数（25 件 / pageSize=10 で 5 件、IDs 21-25）、(9) 末尾ページが pageSize の倍数境界（20 件 / pageSize=10 で 10 件）、(10) 単一ページ結果で `HasPrev=HasNext=false`、(11) First→Next→…→末尾 の往復で全 27 件に到達し ID がユニークで `timestamp ASC`、(12) Last→Previous→…→先頭 の往復で全 23 件に到達、(13) `afterCursor=null` で `ArgumentNullException`、(14) `pageSize<=0` で `ArgumentOutOfRangeException`。既存の `SearchAsync_*` フィルタ系テスト 9 件は `SearchFirstPageAsync(criteria, pageSize:100)` 呼び出しに更新（`Result.Items.First()` 等の API 差分も解消）。`OperationLogSearchViewModelTests` は `SetupKeysetReturning` / `BuildPage` / `MakeLog` の 3 ヘルパーを新設して全テストを keyset モックに移行し、`SearchAsync_直接最終ページを取得する_Issue1479`（`SearchLastPageAsync` が 1 回、`SearchFirstPageAsync` が 0 回呼ばれること）、`OnPageSizeChanged_ページサイズ変更で再検索されること`、`NextPageAsync_HasNextがfalseなら呼ばれないこと`、`PreviousPageAsync_HasPreviousがfalseなら呼ばれないこと`、`LastPageAsync_最終ページに移動すること` の 5 件を新規追加。`docs/design/07_テスト設計書.md §8.1` の単体テスト件数スナップショットを 3,220 → **3,236 件** に同期（#1479）
- 共有モードの `SharedModeMonitor` ヘルスチェック間隔を **30 秒 → 15 秒** に短縮し、共有モード時のキャッシュ最大 TTL（`CacheOptions.CardListSeconds=15`）および `StaleThresholdSeconds=15` と一致させた。改修前は最短キャッシュ TTL（`LentCardsSeconds=10`）の 3 倍のヘルスチェック間隔となっており、他 PC で「ピッ」した直後に最大 30 秒近くダッシュボードへ反映されない、また「同期表示」が stale 判定された後もしばらく実際の接続確認が走らない不整合区間があった。`SharedModeMonitor.cs` に `internal const int HealthCheckIntervalSeconds = 15` を新設して `_healthCheckTimer.Interval` から参照する形にし、定数化することでテストから期待値を直接参照できるようマジックナンバーを排除した。共有モード時のみ `Start()` が呼ばれるためローカルモードには影響なし。`CheckConnection()` は `SELECT 1` 相当の軽量クエリのため DB アクセス頻度が 2 倍になっても 20 台同時接続時の SMB 負荷は無視できる（Issue #1107 の `busy_timeout=15000ms` で吸収）。回帰防止として `SharedModeMonitorTests` に 2 件追加: (a) `HealthCheckIntervalSeconds_共有モードの最大キャッシュTTLと一致すること`（`App.xaml.cs` 共有モード上書き値の `CardListSeconds=15` と一致を固定）、(b) `HealthCheckIntervalSeconds_StaleThresholdSecondsと一致すること`（`StaleThresholdSeconds=15` と一致を固定）。既存の `Start_ヘルスチェック用と同期表示用の2つのタイマーを生成すること` も `TimeSpan.FromSeconds(30)` ハードコードから `SharedModeMonitor.HealthCheckIntervalSeconds` 参照に更新し、TTL/間隔/しきい値の三項目が独立に変更されてドリフトするのを CI で検出する。`.claude/rules/business-logic.md` / `docs/design/00_用語集.md` / `docs/design/02_DB設計書.md` / `docs/design/04_機能設計書.md` / `docs/design/07_テスト設計書.md` / `docs/manual/管理者マニュアル.md` の「30秒」記述（計 8 箇所）を「15秒」に同期更新（#1493）
- 共有モードで月初の起動時に全 PC が同時に VACUUM を試行し、相互に SQLITE_BUSY を踏んで警告ログを汚す問題を解消。`SettingsRepository.TryAcquireMonthlyVacuumLockAsync(DateTime)` を新設し、`settings.last_vacuum_date` 行への `INSERT ... ON CONFLICT(key) DO UPDATE ... WHERE substr(value,1,7) <> :currentMonth` という単一 SQL を **compare-and-swap (CAS)** として用いる。SQLite のステートメントレベル原子性により、複数 PC が同時に呼び出しても正確に 1 台だけが `rowsAffected=1` を受け取り「自分が VACUUM 担当」と判定する。`App.xaml.cs:PerformStartupTasksAsync` の起動時 VACUUM 経路を「ロック獲得時のみ `VacuumAsync()` を呼ぶ」形に書き換え、ロック獲得後の VACUUM 失敗時は当月スキップとして確定する設計（来月まで誰も再試行しない＝デッドロックスパイラル防止）。ロック未獲得 PC はログを出さず素通りするため運用ログも汚れない。回帰防止として `SettingsRepositoryTests` に 4 件追加（前回実行なし→trueで当日保存／前月実行済み→trueで更新／当月実行済み→falseで非変更／10 並列同時呼び出しで true は 1 つだけ）。`App.xaml.cs` の起動時経路は xUnit 駆動困難のため PR テストプランで手動検証する（#1482）
- ClosedXML 月次帳票の `ExcelStyleFormatter` でセル/行単位のスタイル設定が繰り返されて遅い問題を改善。`ApplyDataRowBorder` / `ApplySummaryRowBorder` / `ApplyEmptyRowBorder` で行全体の `Range(row, 1, row, 12)` を 2 回ずつ取得していた重複生成を 1 回にまとめ、ClosedXML 内部のスタイル hash 更新の波及を削減した。加えて、`ReportService.FillEmptyRowsWithBorders` でループ実行されていた最終ページ空白行の罫線適用（per-row 経路）を `ExcelStyleFormatter.ApplyEmptyRowBordersToRange(worksheet, firstRow, lastRow)` の新規一括版に置き換え、連続する空白行 1〜12 行分に対して 1 つの複数行 Range で罫線・行高さ・両端太線（A 列 LeftBorder / L 列 RightBorder）を一括適用する形に統一した。複数行範囲では `TopBorder` / `BottomBorder` が各セルにカスケード適用され `InsideBorder` が内側（行間・列間）に Thin を適用するため、視覚的結果は per-row 適用と完全に同等であることを既存テストとの等価比較で確認した。セル結合（B-D 列・I-L 列）は複数行をまとめると 1 つの結合セルになる ClosedXML の挙動に合わせて行単位ループを維持する設計判断とした。回帰防止として `ExcelStyleFormatterTests` に 6 件追加: (1) 1 行範囲で per-row 版と全項目（罫線・太字・結合・行高さ）が等価、(2) 5 行範囲で全行に上下罫線・行高さ・結合が適用される、(3) 範囲両端（A/L 列）の太線が全行で Medium、(4) `firstRow > lastRow` の空範囲は例外を投げず no-op、(5) Issue #591 相当の太字書式リセットが全行に適用される、(6) 100 行範囲のスケーラビリティ（先頭・中間・末尾サンプルで罫線・結合が正しい）。既存 30 件＋新規 6 件の計 36 件で旧 per-row API 経路の挙動を固定し、`ReportService` 経路の全 117 件と全体 3,226 件も GREEN を維持。Issue 出典の「12 ヶ月一括生成ベンチマークを取って改善幅が誤差レベルか確認推奨」は本 Option A の改修範囲が空白行と内部 Range 重複削減に限定されるため改善幅は控えめ（数十〜数百 ms 想定）であり、本 PR では構造的な無駄を取り除く回帰防止の意義を主眼とした。`docs/design/07_テスト設計書.md §2.29` に UT-043B を新設し §8.1 のテスト件数スナップショットを 3,220 → 3,226 へ同期更新（#1480）

**ドキュメント整理**
- 設計書 4 ファイル（`docs/design/01_システム概要設計書.md` / `04_機能設計書.md` / `05_クラス設計書.md` / `07_テスト設計書.md`）に残存していた「ICカード」単独表記（自然言語）を「交通系ICカード」へ統一し、`.claude/rules/development-conventions.md` の用語ルール（「職員証」と「交通系ICカード」の混同防止のため交通系ICカードを指す場合は必ず「交通系ICカード」と記載する）を設計書本文にも適用した。修正対象は計 12 箇所: (a) `01_システム概要設計書.md` ハードウェア構成表の PaSoRi 用途記述、(b) `04_機能設計書.md` §2.3 章タイトル、(c) `05_クラス設計書.md` の `IcCard` クラス責務記述・`VirtualCardViewModel` 説明・`ICardRepository` 責務記述・`WaitingForIcCard` 列挙コメント、(d) `07_テスト設計書.md` の `FelicaHistoryBlockDecoder` 解説の FeliCa 生データ言及・TC048 バス順序テストデータ説明・UT-037/UT-039 テスト名・UT-039 期待結果の状態継続記述・UT-044 No.13 タイムアウト後のタッチ記述・UT-052 期待結果のタッチ記述・M5 エラーダイアログのタッチ条件・IT-001 シナリオ文言（3 箇所）・§10.2 テストデータ章タイトル。コード識別子（`IcCard` クラス名・`WaitingForIcCard` 列挙子・`ICardRepository` インターフェース名・`ic_card` テーブル名・`IcCardTests` テストクラス名等）と DB 列名はリファクタ連鎖・既存テスト破壊を避けるため変更対象外とし、Issue #1474 本文の指針「コード識別子に対応する解説文中の自然言語のみ修正対象」に厳密に従った。除外箇所として `07_テスト設計書.md` §2.48 UT-063（「UI 露出文字列における『ICカード』単独表記の禁止」テストの記述部分 3 行）は、検出対象の禁止表記を例示する性質上「ICカード」単独表記を**意図的に**残す必要があるため非修正。設計書本文（Markdown）は `UserFacingTextConventionTests`（UT-063）の検査対象（`Views/**/*.xaml` と `ViewModels/**/*.cs`）外のため新規単体テストの追加は不要。修正方針の妥当性は `grep -n "ICカード" | grep -v "交通系ICカード" | grep -v "ICカードリーダー" | grep -v "ICカード管理" | grep -v "ICカードリーダ"`（UT-063 の `HasStandaloneICCard` ロジックと同等の判定）で UT-063 関連 3 行のみが残存することを 4 ファイル全てで検証済（#1474）
- `docs/design/05_クラス設計書.md` §0「クラス番号の抜けについて」の文言を「過去の整理の名残で、欠番のままとしています。必要があれば Issue で整理予定です」から「**意図的に欠番とし、今後も欠番のまま維持します（Issue #1490 で決着）**」に確定させた。リナンバー案（§5.5〜§5.16 までの章番号と `§5.5b` / `§5.8a` / `§5.8b` 等の Issue 紐付き派生章の内部参照を全て書き換え）は変更コストに見合う読みやすさの改善が得られないため不採用とし、文書冒頭で読者に対し「これは未決事項ではなく確定方針である」ことを明示した。2026-05-08 のリポジトリ全体レビュー（ドキュメント観点エージェント、Docs L2）以来 5 か月以上「整理予定です」と未決断のまま放置されていた状態を文言 1 行の修正で解消する。プログラム変更を伴わないドキュメント校正のため単体テストの追加・既存テストの修正なし（#1490）
- `docs/design/02_DB設計書.md` §4 マイグレーション履歴の末尾に、v2.8.0 で導入された冪等化ヘルパー `MigrationHelpers.AddColumnIfNotExists`（Issue #1285）の存在と参照先を追記。従来 `.claude/rules/migrations.md` のみで言及されていたため、設計書だけ読む読者にはヘルパーの存在が伝わらず、新規マイグレーション追加者が非冪等な `ALTER TABLE` を再導入するリスクがあった。併せて「新規マイグレーションを追加した際のチェックリスト」blockquote に「4. `Up()` は冪等パターン（`CREATE TABLE IF NOT EXISTS` / `MigrationHelpers.AddColumnIfNotExists` / `INSERT OR IGNORE` 等）で書く」項目を追加し、運用上の必須要件として明文化する。2026-05-08 のリポジトリ全体レビュー（ドキュメント観点エージェント、Docs M6）の指摘事項。プログラム変更を伴わないドキュメント校正のため単体テストの追加・既存テストの修正なし（#1477）
- `docs/manual/開発者ガイド.md` の「refactor 履歴（v2.5.0〜v2.7.0）」blockquote と §2.5「アーキテクチャの発展（v2.5.0〜v2.7.0）」が章タイトル・本文ともに v2.7.0 を上限としていたが、v2.8.0 では構造変更が複数導入されていたため反映漏れを解消した。修正内容: (a) §2.2 末尾の refactor 履歴 blockquote の上限を「v2.5.0〜v2.8.0」に伸長し、v2.8.0 の代表 Issue 4 件（#1283 / #1284 / #1285 / #1287）を列挙、(b) §2.5 章タイトル・本文導入を「v2.5.0〜v2.8.0」に更新、(c) §2.5 配下にサブセクションを 4 つ追加: §2.5.7 `LendingService` の internal ヘルパー分割（#1283、`LendAsync` 121→62 行・`ReturnAsync` 182→99 行、`LendingServiceHelperTests` 23 件追加）、§2.5.8 `CsvImportService` の再分割（#1284、`Ledger.cs` 1031→520 行・`Detail.cs` 1042→761 行、`LedgerCsvRowParser` / `LedgerDetailCsvRowParser` / `NewLedgerFromSegmentsBuilder` への責務分離と 21 件のテスト追加）、§2.5.9 Migration の冪等化（#1285、`MigrationHelpers.AddColumnIfNotExists` 新設、5 マイグレーション冪等化、`MigrationIdempotencyTests` 9 件＋`MigrationHelpers` 7 件追加）、§2.5.10 Service 層への `ConfigureAwait(false)` 一貫適用（#1287、`.claude/rules/async-configureawait.md` 規約文書化、`.editorconfig` で `CA2007` を Service 層のみ suggestion）。同時に再発防止として `.claude/skills/release/SKILL.md` に「開発者ガイド §2.5『アーキテクチャの発展』の更新（Issue #1472 対策）」セクションを新設し、リリース時に構造変更の有無を判定 → 該当時は §2.2 blockquote と §2.5 を同一バージョン PR で更新するチェック手順と文面の参考スタイルを記載した。プログラム変更を伴わないドキュメント校正のため単体テストの追加・既存テストの修正なし。出典: 2026-05-08 のリポジトリ全体レビュー（ドキュメント観点エージェント、Docs M1）（#1472）

**テスト強化**
- `docs/manual/開発者ガイド.md` 付録 C トラブルシューティング表の「テスト失敗 / DBマイグレーション / テスト前に `ClearOperationLogs()` を呼ぶ」行を v2.8.0 の現役テスト基盤に合わせて全面刷新。grep の結果 `ClearOperationLogs()` というメソッドは本体・テスト全体に **1 件も実在せず**（開発者ガイドの当該行のみ）、レガシーコード由来の参照と判明したため、新規開発者がこの対処を試そうとして混乱するリスクがあった。修正内容: (a) 旧 1 行を「テスト失敗（マイグレーション関連）」（`MigrationIdempotencyTests` を参照点に `:memory:` SQLite で個別検証する案内、規約は `.claude/rules/migrations.md`）と「テスト失敗（UI スレッド関連）」（`DbContextUiThreadGuardTests` を参照点に Service 層からの UI スレッド `DbContext` 操作を `Task.Run` でオフロードする案内、Issue #1281 経緯）の 2 行に拡張。(b) テーブル下に「テスト隔離の補足」段落を新設し、現代のテスト基盤は各テストクラスが xUnit の `IDisposable` パターンで `:memory:` SQLite 接続をコンストラクタで生成し `Dispose` で破棄するため、グローバル状態をクリアする手動メソッド（旧 `ClearOperationLogs()` 等）は不要であること、テスト間の状態共有は `xUnit.IClassFixture<T>` の明示的フィクスチャでのみ発生することを明示。`Migration_009` まで進んだ v2.8.0 のテストフレーバ大幅変更（`MigrationIdempotencyTests` の Issue #1285 導入、`DbContextUiThreadGuardTests` の Issue #1281 導入、各テストクラスが `:memory:` 接続を独自に持つ隔離パターン）を反映した。プログラム変更を伴わないドキュメント校正のため単体テスト追加・既存テスト修正なし（#1491）
- `docs/design/07_テスト設計書.md §8.1 テスト完了基準` の実数値を実測スナップショットに更新。単体テスト件数を「現在1,804件」から「現在3,208件」（+1,404件、`dotnet test --list-tests` 実測）、UIテスト件数を「現在15件」から「現在26件」（+11件、同実測）に同期。両数値は Issue #1455 / #1460 / #1461 / #1468 / #1483 / #1503 / #1509 / #1514 等の累積的な回帰防止テスト追加で増加していたが、§8.1 のスナップショットが更新されておらず本 Issue（#1498）の PR レビューで実態との乖離が判明したため当 PR でまとめて同期。今後は新規テスト追加を伴う Issue で同節の数値を同期更新する運用とする（#1498）
- 共有モード（UNC パス）での起動シナリオ手動テスト項目 `ST-SHARED-003`（テストケース 4 件: 正常起動 / SMB 一時切断中の起動 / SMB 復旧後の再起動 / 書き込み権限不足）を `docs/design/07_テスト設計書.md §7a.3` に新設。Issue #1455 / PR #1497 で `EnsureDirectoryWithPermissions`（Issue #1499 で `EnsureDirectoryExists` にリネーム済）の `try/catch (Exception)` ブロックを撤廃した際、副次的に SMB 一時切断時の `Directory.CreateDirectory` リトライとして機能していた可能性のある挙動が消失したため、共有モード環境での起動安定性を手動 fail-safe 検証する。各テストケースに「自動テストでの部分的な検証」表（既存 `DbContextFilePermissionsTests` / `DbContextConcurrentAccessTests` のメソッドマッピング）と詳細手順を併記し、SMB ネットワーク・実ファイルサーバ・複数 Windows ユーザーを要するためユニットテスト自動化が困難な領域を組合せ網羅型の手動テストで担保する。あわせて `docs/manual/管理者マニュアル.md §10.4` 共有フォルダモードの問題トラブル表に「起動時に SMB 一時切断でアプリが起動しない」症状の行を追加し、運用上の対処（Explorer で UNC パス確認 → 再起動でデータ欠損なく復旧）と「読み取り専用」権限では起動不能（SQLite 一時ファイル作成に書き込み権限必須）の補足を明記。プログラム変更を伴わないドキュメント追加のため単体テストの追加・既存テストの修正なし（#1498）
- `DialogAutomationPropertiesCoverageTests.Dynamic_text_blocks_should_not_have_AutomationProperties_Name` の `[InlineData]` に `OperationLogDialog` の動的 `TextBlock` 4 要素（`PageInfoText` / `CurrentPageNumberText` / `StatusMessageText` / `ProcessingOverlayText`）を追加し、ページ情報・現在ページ番号・検索ステータスメッセージ・処理中メッセージのいずれかに `AutomationProperties.Name` が誤って付与されたら静的解析で即検出できるようにした。PR #1500（Issue #1468）で同テストを導入した時点では `[InlineData]` が `StaffAuthDialog.xaml` の 3 要素（`OperationDescriptionText` / `StatusText` / `TimeoutText`）に限定されており、`CHANGELOG.md` と `docs/design/03_画面設計書.md` で対象として明示していた `OperationLogDialog` 側 4 要素が回帰テストでカバーされていなかった。回帰テストを動作させるために `OperationLogDialog.xaml` の該当 4 `TextBlock` に `x:Name="PageInfoText"` / `x:Name="CurrentPageNumberText"` / `x:Name="StatusMessageText"` / `x:Name="ProcessingOverlayText"` を付与する（既存テスト regex `x:Name="…"[^/]*?AutomationProperties\.Name=` が同一開始タグ内の `AutomationProperties.Name` 同居を検出する形式のため、起点となる `x:Name` 識別子が XAML 側に必要）。バインドや `LiveSetting` / `HelpText` などのアクセシビリティ属性は一切変更しないため動作・読み上げ挙動は不変。`docs/design/07_テスト設計書.md §2.45 UT-058a` の動的 `TextBlock` 一覧と対象件数（3 → 7）も同期更新（#1501）
- `DialogAutomationPropertiesCoverageTests` の `LiveSetting` 検査 regex 3 箇所（`StaffAuthDialog_status_text_should_have_assertive_live_setting` L116、`StaffAuthDialog_timeout_text_should_have_polite_live_setting` L128、`OperationLogDialog_status_message_should_announce_changes` L143）を `[\s\S]*?` から `[^>]*?` に変更し、起点と終点が**同一開始タグ内**にあることを保証する形に強化した。旧 regex は要素境界を考慮せず非貪欲マッチを許していたため、特に `OperationLogDialog.xaml` で `AutomationProperties.LiveSetting="Polite"` が 4 箇所（ステータスメッセージ TextBlock 以外にもページ番号・エクスポートステータス等）に出現することから、StatusMessage TextBlock から `LiveSetting` を外しても他要素の `Polite` を拾って regex が緑のまま通る誤検出脆弱性があった。`StaffAuthDialog` 側の 2 つは `x:Name="StatusText"` / `x:Name="TimeoutText"` で起点を絞っているため誤マッチ確率は実用上低いが、同じ原則で `[^>]*?` に統一。XAML 属性値は `"..."` で囲まれて開始タグ内に裸の `>` が出現しないため、`[^>]` は改行・空白を含みつつ同一開始タグ内に安全に閉じ込められる。回帰防止として 2 つのテストを追加: (a) `LiveSettingPattern_should_be_scoped_to_same_element`（Theory 5 件、同一要素内マッチ・マルチライン属性レイアウト・別要素を跨ぐ非マッチ・LiveSetting 欠落非マッチを合成 XAML で固定）、(b) `OldGreedyPattern_would_have_falsely_matched_cross_element_LiveSetting`（旧 `[\s\S]*?` が要素境界を跨いで誤検出していた事実と、新 `[^>]*?` が同じ合成 XAML で正しく検出しないことを対比して記録）。`docs/design/07_テスト設計書.md §2.45 UT-058a` の表に行 32-36（合成 XAML 5 ケース）と行 37（旧 regex 脆弱性の回帰記録）を追記（#1503）
- `DialogAutomationPropertiesCoverageTests.MinimumNameCounts` のコメントと閾値の不一致を解消。`OperationLogDialog.xaml` の `AutomationProperties.Name` 付与は実 XAML 上 23 個（Window / 検索条件入力 6 個 / 期間クイック 3 個 / 検索・クリア 2 個 / DataGrid / ページサイズ / ページネーション 4 個 / エクスポート 2 個 / 閉じる / 処理中 2 個）あるにもかかわらず、(a) コード上の閾値は 18、(b) コメント末尾の集計式は「= 19個以上」と、コメント・閾値・実態の三項全てがズレていた。閾値を実数 **23** に引き上げ、コメントの内訳を実 `AutomationProperties.Name=` 一覧から再構築して合計式と実数を一致させた。`MinimumNameCounts` の運用ルール「値を**増やす**変更は許可（カバレッジ向上）、**減らす**変更は要レビュー（カバレッジ後退）」に従い、閾値引き上げは適合。これにより回帰防止の感度が最大化され、1 件でもアクセシビリティ Name が削られると `Dialog_should_meet_minimum_AutomationProperties_Name_coverage` が即座に失敗する。`StaffAuthDialog.xaml` は真の付与数 4 と閾値 4 が一致しているため変更なし（regex がコメント中の `AutomationProperties.Name` 言及を 1 件拾うため `Regex.Matches` の生カウントは 5 になるが、それは別の品質ゲートの問題）。`docs/design/07_テスト設計書.md §2.45 UT-058a` の閾値記述（18 → 23）も同期更新（#1502）
- 操作ログダイアログのクイックフィルタボタン「今日 / 今月 / 先月」が、実 WPF 描画下で `BoundingRectangle.Width / Height > 0`・`IsOffscreen = false`・操作種別 ComboBox との `BoundingRectangle.IntersectsWith == false` であることを FlaUI 5.0 統合テストで機械検証する `OperationLogQuickFilterDisplayTests`（2 件）を追加。Issue #1505 / PR #1521 で導入した静的解析テスト `OperationLogDialogQuickFilterLayoutTests`（UT-058c）は XAML 構造の不変条件（Row 0/Row 1 行分離、`Width="Auto"` 等）を固定するが、Grid 星共有列の幅圧迫によるボタンクリップ再発を実描画下で検出することはできなかった。本テストがそのカバレッジ穴を埋める。経路は MainWindow → SystemManageDialog → OperationLogDialog の二段モーダルで、`systemManageDialog.ModalWindows` → `MainWindow.ModalWindows` → `App.GetAllTopLevelWindows` の 3 段フォールバックで OperationLogDialog を取得する。`OperationLogDialogPage` PageObject と `TestConstants` への定数追加（`OperationLogDialogName` / クイックフィルタ 3 ボタン名 / `OperationLogActionTypeComboBox` / `OperationLogDialogOpenTimeoutSeconds=30`）も同 PR で追加。Issue #1522 で既知制約として記載されていた WSL2 経由実行時の SystemManageDialog 早期消失症状は本実装の 3 段フォールバックで現環境では再現しないことを確認したが、将来再発した場合の安全網として `Xunit.SkippableFact` パッケージを追加し、環境変数 `SKIP_QUICK_FILTER_UITEST=1` または `WSL_DISTRO_NAME` 設定時は `TestConstants.ShouldSkipQuickFilterFlaUiTest` 経由で自動 Skip する。`tools/run-quickfilter-uitest.ps1`（Windows ローカル / CI 向け補助ランナー、本テストだけを `--filter FullyQualifiedName~OperationLogQuickFilterDisplayTests` で絞って実行）と `docs/design/07_テスト設計書.md §2.45` の UT-058c-FlaUI 節を同期追加（#1522）
- DEBUG 限定機能 `VirtualCardDialog`（Issue #640、仮想タッチ設定ダイアログ）の起動経路が Release ビルドから完全に除外されていることを継続的に保証する `ConditionalCompilationGuardTests`（5 件）を追加。現状調査の結果、起動経路（`MainViewModel.OpenVirtualCardAsync` / `ProcessVirtualTouchAsync` の 2 メソッド、`App.xaml.cs` の `VirtualCardViewModel` / `VirtualCardDialog` の 2 DI 登録、`MainWindow.xaml` の「仮想タッチ」ボタン）はすべて `#if DEBUG` または `App.IsDebugBuild` バインディングでガード済みであることが判明したため、本 PR ではコード変更は行わず、これらガード状態の継続検証テストのみを追加してリグレッションを防止する位置付け。検証ロジックは静的解析方式で、C# 側はソースを行単位で走査して `#if DEBUG` / `#if !DEBUG` / `#else` / `#elif` / `#endif` のプリプロセッサスタックを追跡し、対象識別子の出現行が DEBUG 評価を満たすブロック内（入れ子は AND 結合）にあることを判定する `IsLineInsideDebugBlock` ヘルパーを実装。XAML 側は対象 Button から遡って直近の `<StackPanel` 開始タグを特定し、開始タグから対象行までの間に `Visibility="{Binding Source={x:Static app:App.IsDebugBuild}, ...}"` 相当の属性が存在することを構文解析で確認する。テストは Debug ビルドで実行しても Release バイナリの状態を確認できるためテスト独立性が高い（CI/Release ビルド不要）。Issue 起票時に挙がっていた代替案「`dotnet publish -c Release` の出力をリフレクションで逆解析するテスト」は CI 基盤の整備が必要で過剰、「csproj レベルで XAML を Debug 限定登録するリファクタ」は WPF SDK の暗黙的ファイル登録と競合してビルドが壊れやすいため、いずれも採用しなかった。`docs/design/07_テスト設計書.md §2.49 UT-064`（5 ケース）と §8.1 単体テスト件数を 3,213 → 3,218 に同期更新。設計書 `docs/superpowers/specs/2026-05-17-issue-1487-virtualcarddialog-release-exclusion-design.md` に方針選定（Option A 採用）の根拠と現状調査結果（既にガード済みであった事実）を残す（#1487）

**テスト整理**
- テスト件数表（`07_テスト設計書.md` §1.1a）と `dotnet test --list-tests` 実測値の乖離を CI で自動検出する workflow (`.github/workflows/test-count-sync-check.yml`) と検証スクリプト (`tools/check-test-count-sync.py`) を追加。Issue #1475（PR #1545）で運用ルールとして整備された「件数の同期手順」を機械化し、テスト追加・削除のたびに §1.1a の表を更新し忘れた PR を CI 段階でブロックする。検証は単体テスト・UI テスト・合計の 3 値を 0 件差で厳密比較し、乖離時は exit 1 で修正手順を表示、表形式自体の異常時は exit 2 で保守者向けメッセージを表示する。Python 純粋関数（パーサ・比較）には `unittest` 7 件のテストを追加。なお、本 PR の初回 CI で §1.1a に 39 件の乖離（記載: 単体 3,266 / 合計 3,292、実測: 単体 3,227 / 合計 3,253）が検出されたため、同 PR 内で記載値を実測値に同期更新した。これは導入した自動検証が production で正しく機能していることの実証も兼ねる（#1546）
- `DbContextConnectionLeaseTests.cs` の `LeaseConnection_リエントラント呼び出しがデッドロックしないこと`（L132-148）を削除。同ファイル L362 の `LeaseConnection_同期版でリエントラントが動作すること` と**本体が完全に同一**（同じ `dbContext.LeaseConnection()` 同期 API を呼び、同じアサーション：State Open + 同一 Connection 検証）で、機能的に重複していた。削除した方は `#region LeaseConnectionAsync` 内に配置されていたが実装は同期 API を呼んでいる**配置不整合**もあり、`#region LeaseConnection（同期版）` 内の L362 のテストを保持するのが構造的にも整合する。`docs/design/07_テスト設計書.md §1.1a` と §8.1 のテスト件数スナップショットを 3,267 → 3,266 に同期更新。本作業は 3,267 件の単体テスト全件を 4 カテゴリ（Skip 属性付き / 意味のないアサーション / テスト名・意図重複 / 同一コードパス重複）で静的スキャンした結果見つかった唯一の真の重複で、他 392 ペア（構造類似ペア）は全て境界値テスト・同値分割テストであり削除対象外と確認した。スキャン手法と判断基準の詳細は `docs/superpowers/specs/2026-05-18-test-suite-cleanup-design.md` に記録（テストのみ修正、本体コード変更なし）

**新機能**
- Issue #1570 バス停名入力に往復ボタン追加（#1570）
- 共有DB接続状態（Connected/Reconnecting/Disconnected）をステータスバーに3状態表示 (Issue #1470)（#1470）
- テスト件数表の CI 自動検証 (Issue #1546)（#1546）
- 操作ログクイックフィルタ実描画リグレッションをFlaUIで機械検証 (Issue #1522)（#1522）

**バグ修正**
- Issue #1584 インストーラーのフォルダ指定時にマップトドライブを選択可能にする（#1584）
- Issue #1574 「(貸出中)」状態の履歴行を変更ボタンから削除可能にする（#1574）
- Issue #1580 ConsolidateRoutes が A→B→A→B チェーンを乗継として誤統合し情報を失う問題を修正（#1580）
- Issue #1577 仮想タッチからのバス停入力ダイアログ表示を共通化（#1577）
- Issue #1575 利用履歴を含む返却処理のデッドロックを修正（#1575）
- Issue #1465 Process.Start パス検証強化 (SafeFileLauncher 導入)（#1465）
- MigrationHelpers の column / typeAndConstraints を regex 検証 (Issue #1466)（#1466）
- 共有モード判定をUNC/マップドドライブ限定に修正 (Issue #1559)（#1559）
- LedgerRepository.InsertDetailsAsync をバッチ化 (Issue #1456)（#1456）
- PathValidator のエラーメッセージを3要素ガイドライン対応 (Issue #1471)（#1471）
- OperationLogDialog の動的 TextBlock で LiveRegionChanged を発火 (Issue #1548, #1507)（#1548）
- OperationLogDialog の動的 TextBlock で LiveRegionChanged を発火 (Issue #1548)（#1548）
- DebugDataService の DELETE 文を IN 句パラメータ化 (Issue #1485)（#1485）
- 操作ログダイアログの終了日 DatePicker クリップを期間 StackPanel の ColumnSpan=5 拡大で解消 (Issue #1523)（#1523）
- LedgerRepository に SQLiteTransaction 明示参加オーバーロード追加 (Issue #1481)（#1481）
- HandleLegacyDatabase の補填 INSERT を INSERT OR IGNORE 化 (Issue #1484)（#1484）
- 削除不可ダイアログ（貸出中レコード）に解決アクションを追加 (Issue #1486)（#1486）
- 共有モードヘルスチェック間隔をキャッシュ TTL に揃える (Issue #1493)（#1493）
- 操作ログダイアログのクイックフィルタ「今日/今月/先月」ボタンを独立行へ分離 (Issue #1505)（#1505）
- 共有モードのVACUUM競合を先勝ちCASロックで解消 (Issue #1482)（#1482）
- LedgerRepository.GetByIdAsync / GetLentRecordAsync を 1 RTT に集約 (Issue #1478)（#1478）
- PathValidator の冗長な三項演算子と重複コメント番号を修正 (Issue #1483)（#1483）
- UI カラーリテラル直書きを撤廃し AccessibilityStyles の SSOT に統一 (Issue #1461)（#1461）
- UI 文言の「ICカード」単独表記を「交通系ICカード」へ統一 (Issue #1460)（#1460）
- データインポートのカードタッチ待機中にメイン画面の OnCardRead を抑制 (Issue #1514)（#1514）
- ランタイムでの過剰 ACL 拡張を撤廃 (Issue #1455)（#1455）
- OperationLogDialog/StaffAuthDialog の AutomationProperties カバレッジを拡充 (Issue #1468)（#1468）

**リファクタリング**
- 同構造 Fact/Theory テスト群を Theory + InlineData に統合 (5 クラス、Issue #1550)（#1550）
- EnsureDirectoryWithPermissions を EnsureDirectoryExists にリネーム (Issue #1499)（#1499）

**ドキュメント**
- Issue #1571 UNCパス推奨記載を削除しマップドドライブと並列扱いに（#1571）
- Issue #1565 残り2枚のスクリーンショット追加 (error_no_reader / warning_network_disconnected)（#1565）
- マニュアル参照済みスクリーンショット 7 枚を追加 (Issue #1463)（#1463）
- マニュアル4ファイルを v2.8.1 に同期 + bump-version.ps1 改修 (#1462)（#1462）
- 開発者ガイド §2.5 を v2.8.0 まで伸長 (Issue #1472)（#1472）
- 設計書 4 ファイルの「ICカード」単独表記を「交通系ICカード」に統一 (Issue #1474)（#1474）
- テスト件数表の同期手順を明文化＆実測値に同期 (Issue #1475)（#1475）
- DB設計書 §4 にマイグレーション冪等化ヘルパーの存在を明記 (Issue #1477)（#1477）
- クラス設計書 §5.4 欠番を「意図的に欠番のまま維持」と確定 (Issue #1490)（#1490）
- 開発者ガイド付録 C トラブルシューティングを v2.8.0 のテスト基盤に同期 (Issue #1491)（#1491）
- 共有モード（UNC パス）の SMB 切断・再接続シナリオ手動テスト項目を追加 (Issue #1498)（#1498）
- CLAUDE.md のディレクトリ構成と参照ドキュメントを実体パスに整合 (Issue #1492)（#1492）

**テスト**
- VirtualCardDialog の DEBUG ガード継続検証テストを追加 (Issue #1487)（#1487）
- 動的TextBlock検証に OperationLogDialog 側 4 要素を追加 (Issue #1501)（#1501）
- MinimumNameCounts コメントと閾値の不一致を修正 (Issue #1502)（#1502）
- LiveSetting 検査 regex を要素境界で絞る (Issue #1503)（#1503）
- AutomationProperties.Name 検査を Regex 化して XAML 整形の空白挿入に耐性を持たせる (Issue #1504)（#1504）

**パフォーマンス**
- Issue #1458 OperationLogger を Ledger 操作と同一トランザクション化（#1458）
- Issue #1457 GetPagedAsync の detail_count を CTE+LEFT JOIN 化 (N+1 解消)（#1457）
- ExcelStyleFormatter の重複 Range 生成削減と空白行一括版を追加 (Issue #1480)（#1480）
- operation_log のページネーションを keyset 化 (Issue #1479)（#1479）

### v2.8.1 (2026-05-11)

**バグ修正**
- Service 層のリポジトリ呼び出しを直列化し SQLITE_MISUSE を防止 (Issue #1452)（#1452）
- 月次帳票の4月計と年度累計の受入金額に前年度繰越を加算 (Issue #1494)（#1494）

**ドキュメント**
- CHANGELOG.md の Unreleased を v2.8.0 セクションへ統合（#1451）

### v2.8.0 (2026-05-03)

**セキュリティ修正**
- 監査ログ（operation_log）への一括操作の記録範囲を拡張。従来 INSERT/UPDATE/DELETE/RESTORE/MERGE/SPLIT のみ記録されていたが、データ移行・災害復旧系統の CSV インポート / CSV・Excel エクスポート / 手動バックアップ取得 / DB リストアも `operation_log` に残すようにした。`OperationLogger.Actions` に `IMPORT` / `EXPORT` / `BACKUP` を新設（`RESTORE` は既存のレコード単位復元と `TargetTable` で区別）、`Tables` に `database` / `ledger_detail` を追加。4 つの新 API（`LogImportAsync` / `LogExportAsync` / `LogBackupAsync` / `LogRestoreAsync`）を `DataExportImportViewModel` / `SystemManageViewModel` から呼び出す。操作者は `ICurrentOperatorContext` から解決し、セッション失効時は `GuiOperator`（IDm=`0000000000000000` / Name=`GUI操作`）へフォールバック（#1265 の方針踏襲）。これにより個人情報持ち出しや履歴改変の事後追跡が可能となる。`AfterData` JSON にはファイルパス・件数（Inserted/Skipped/Error、Record 等）を格納。単体テスト 7 件追加（#1302）
- 監査ログ（operation_log）への操作者なりすましを防止。`OperationLogger` の operator_idm / operator_name は `ICurrentOperatorContext`（職員証タッチ成功時に `StaffAuthService` が自動設定）からのみ解決される。旧 API（`operatorIdm` 引数付き）は `[Obsolete]` となり、渡された引数は無視される（#1265）
- `felicalib.dll` の完全性検証を起動時に実行（DLL Hijacking 対策）。既知の SHA-256 ハッシュと不一致の場合はエラーダイアログを表示してアプリを終了する。内部者が偽造 DLL を配置して IDm を盗聴・改ざんする攻撃を防止（#1266）
- CSV/Excel 式インジェクション (CSV Injection / Formula Injection) 対策。セル先頭が `=` / `+` / `-` / `@` / タブ / CR で始まる文字列にシングルクォート `'` を付与してテキスト・リテラルとして扱わせる。CSV インポート時の `note` / `summary` / `entry_station` / `exit_station` / `bus_stops` と、CSV/Excel エクスポート時の全ユーザー入力由来テキスト列に適用（#1267）
- `PathValidator.ContainsPathTraversal` のパストラバーサル検出を強化。従来の `path.Contains("..")` 単純検査を多段階検出に置き換え、(1) URL エンコード (`%2E%2E`) のデコード再検査、(2) セグメント単位の `..` 判定（混合区切り `/` と `\` 対応）、(3) 末尾空白を考慮した `".. "` の正規化判定、(4) UNC パスで `Path.GetFullPath` 後に元の `\\server\share` プレフィクスが保持されるかの境界チェック、を実施。エラーメッセージもユーザー向けに明瞭化（#1268）
- `PathValidator.ValidateBackupPath` に UNC パス到達性チェック（5秒タイムアウト）を追加。`Directory.Exists` がネットワーク不安定時に数十秒ハングする問題を解決。到達不可時は「ネットワーク共有に到達できません。ネットワーク接続を確認してください」と明確なエラーを表示。非同期版 `ValidateBackupPathAsync` も提供し、設定画面等 UI スレッドからの呼び出しでブロックしないように `SettingsViewModel.SaveAsync` を更新（#1269）

**バグ修正**
- ReportDialog 作成結果セクション表示修正 + マニュアル/設計書/スクリーンショット更新 (Issue #1410)（#1410）
- 手動バックアップを作成しても、システム管理画面のステータス表示が完了通知 (「バックアップを作成しました: <ファイル名>」) ではなく直後の件数表示 (「○件のバックアップが見つかりました」) で上書きされてしまう問題を修正。`SystemManageViewModel.CreateBackupAsync` (L132) で完了メッセージを `SetStatus` した直後に `LoadBackupsAsync` (L138) を呼んでいたが、`LoadBackupsAsync` が内部で件数を `SetStatus` する仕様だったため、完了メッセージは UI に出る前に上書きされ、ユーザーは「バックアップが本当に成功したのか」を画面では確認できない状態だった (実際にはファイルは作成されているため動作自体は正しい)。Issue #1417 (管理者マニュアルへのバックアップ完了通知スクリーンショット追加) の作業中、ps1 撮影スクリプトの Instructions「ステータスバーに『バックアップを作成しました: <ファイル名>』と表示されたら」が**実際には永遠に再現できない状態**であることが発覚し、本 Issue のスコープに統合して修正した。(1) `LoadBackupsAsync` の内部実装を `LoadBackupsInternalAsync(bool announceCount)` に分離し、`announceCount=false` の場合は件数を `SetStatus` しないように分岐 (catch のエラー報告は維持)、`LoadBackupsAsync` (`[RelayCommand]`) は従来挙動の `announceCount: true` で委譲、(2) `CreateBackupAsync` の `SaveFileDialog` 後の本体処理を `internal Task CreateBackupCoreAsync(string backupFilePath)` に抽出し、本体内の `LoadBackupsAsync()` を `LoadBackupsInternalAsync(announceCount: false)` に置換。`SaveFileDialog` を直接呼ぶ `CreateBackupAsync` (RelayCommand) は xUnit 環境では実行不能なため、`internal` 抽出により `CreateBackupCoreAsync` を直接テスト可能化 (`InternalsVisibleTo` 既設、PR #1383 で `DataExportImportViewModel.ExportToFileAsync` 抽出と同パターン)。回帰防止として `SystemManageViewModelTests` に 7 件追加: (a) 成功時に StatusMessage が「バックアップを作成しました: ...」のまま件数表示で上書きされないこと、(b) 成功時に LastBackupFile が指定パスに更新されること、(c) 成功時に BackupFiles が更新されること、(d) BackupService が false を返すとき失敗メッセージが表示されること、(e) 例外発生時にエラーメッセージに例外文言が含まれること、(f) `LoadBackupsInternalAsync(announceCount: false)` が StatusMessage を上書きしないこと、(g) `LoadBackupsInternalAsync(announceCount: true)` が従来通り件数表示すること。`RestoreFromFileAsync` (L386) も `await LoadBackupsAsync()` で「リストアが完了しました」を上書きする同パターンを持つが、こちらは MessageBox によるモーダル完了通知 (L378-383) が補助しており完了伝達は維持されるため別 Issue で扱う方針 (#1417)
- 新規職員登録ダイアログで職員証をタッチした直後、氏名入力欄へキーボードフォーカスが移らずユーザーがマウスで氏名欄をクリックしないと文字入力できなかった問題を修正。`StaffManageDialog.xaml` には氏名 TextBox への明示的なフォーカス指定（`x:Name`／`FocusManager.FocusedElement`）が無く、`StaffManageViewModel.StartNewStaffWithIdmAsync` が `IsWaitingForCard = false` を立てた後も View 側にフォーカス指示が伝わらない構造だった。経路 A（F2 → 職員管理ダイアログ素開き → 「新規登録」ボタン → 職員証タッチ）の場合、ダイアログ起動時点では `IsEditing=false` で NameTextBox が Visibility=Collapsed のため、Window アクティベート時の XAML レベル `FocusManager.FocusedElement` 指定は評価対象外として扱われ、その後 Visible 化されても自動的にはフォーカスが当たらない問題があった。最終的に WPF "show-and-focus" 定番パターンとして **氏名 TextBox の `IsVisibleChanged` イベント** を主軸に採用し、Visible 化された瞬間に `Dispatcher.BeginInvoke(... ApplicationIdle)` で `Activate()` → `UpdateLayout()` → `FocusManager.SetFocusedElement` → `Keyboard.Focus` → `Focus()` の 5 段がけでフォーカスを確定する。経路 B（メイン画面で職員証タッチ → IDm 付きで起動）では Window アクティベート時に既に Visible になっているケースに備えて `Window` タグの `FocusManager.FocusedElement="{Binding ElementName=NameTextBox}"` を保険として併用、加えて Issue 推奨の「案 1（MVVM 原則準拠）」も多重防御として残置し、(1) `StaffManageViewModel` に `event EventHandler? RequestNameFocus` を追加して未登録職員証分岐の末尾で発火（既登録／削除済み分岐は `return true` でダイアログを閉じるため発火させない）、(2) `StaffManageDialog` コードビハインドが「`RequestNameFocus` 受信」と「`ContentRendered` 発火」の **AND 条件** を満たした時にフォーカスを再確定。当初実装の `DispatcherPriority.Input` → `ContextIdle` への変更／AND 条件化／Window レベル `FocusedElement` 指定のいずれも経路 A の Visibility=Collapsed 状態で起動するケースを救えず、`IsVisibleChanged` 経路への切替で解決。回帰防止として `StaffManageViewModelTests` に 2 件追加し、(a) 未登録職員証で `RequestNameFocus` が 1 回発火し `shouldClose==false`、(b) 既登録職員証では発火せず `shouldClose==true`（ダイアログを閉じる分岐）であることを検証。実フォーカス確定は WPF Dispatcher 動作が必要なため手動テストで確認。`07_テスト設計書.md §2.42a` に手動テスト項目を追記（#1429）
- PaSoRi カードリーダーが物理的に未接続のままアプリを起動するとステータスバー右下が「リーダー: 切断」ではなく「リーダー: 接続中」のまま表示されていた問題を修正。`FelicaCardReader.CheckFelicaLibAvailable()` が `DllNotFoundException` 以外の例外をすべて「DLL は存在する → 接続中」と判定していたため、PaSoRi 物理未接続でも `StartReadingAsync` が成功扱いとなり `Connected` 遷移していた。(1) `CheckFelicaLibAvailable()` を `IsFelicaLibLoaded()`（DLL ロード判定）と `IsReaderConnected()`（PaSoRi 物理接続判定）に二段化。後者は `FelicaUtility.GetIDm` の例外メッセージから "pasori" / "open" / "device" / "reader" キーワード（大文字小文字無視）を検出して未接続判定し、それ以外は「カードなし」扱いで接続中として扱う。判定ロジックは `IsReaderUnavailableException(Exception)` という静的純粋関数に切り出してテスト可能化、(2) `StartReadingAsync` 冒頭で DLL ロード成功後に PaSoRi 接続確認を追加し、未接続時はタイマーを起動せずに `Disconnected` を確実に通知、(3) 状態の初期確定通知（初期値 `Disconnected` のまま `Disconnected` を確定するパターン）が既存の差分通知ロジック (`SetConnectionState`) では抑止されてしまうため、初期化時のみ用の `SetConnectionStateForceNotify` を追加、(4) ヘルスチェック (`OnHealthCheckTimerElapsed`) も二段判定を使うようにし、起動後の PaSoRi 抜き差し検出も実態に即した形に修正、(5) `MainWindow.xaml` のステータスバー（接続状態アイコン・テキスト）のデフォルト Setter を「リーダー: 接続中」緑→「リーダー: 切断」赤に反転し、`Connected` を明示 DataTrigger 化（`Disconnected` トリガはデフォルトと一致するため削除）。これにより felicalib 例外メッセージの将来変化に対する fail-safe 表示として二重防御。再接続ボタンの可視性は変更なし（`Disconnected` 時表示の挙動を維持）。回帰防止として `FelicaCardReaderHelpersTests`（16件）を新規追加し、`IsReaderUnavailableException` の判定を網羅検証: `DllNotFoundException` の型判定、PaSoRi 未接続キーワード（pasori/open/device/reader）の小文字・大文字混在パターン、カードなし相当メッセージ（polling timeout / no card detected / system code not supported）の接続中扱い、空 / null メッセージ / null 例外の境界ケース。XAML の DataTrigger 評価は単体テスト困難のため実機検証で確認（#1428）
- データエクスポート時、「○○を保存しました」ダイアログが表示されたあとも、裏で動いていたプログレスバー（`IsBusy=true`）が消えない問題を修正。CSV エクスポート（`DataExportImportViewModel.ExportAsync`）と操作ログ Excel エクスポート（`OperationLogSearchViewModel.ExportToExcelAsync`）の両方で、`using (BeginBusy("エクスポート中..."))` スコープ内から `IDialogService.ShowInformation` / `ShowError`（内部は `MessageBox.Show`）を呼んでいた。`MessageBox.Show` は同期モーダルでユーザーが OK を押すまでブロックするため、`BusyScope.Dispose()` が走らず `IsBusy=true` のままとなり、ダイアログ背後にプログレスバーが残存していた。対応として、エクスポート本体を `ExportToFileAsync(string)` / `ExportToExcelFileAsync(string)` の内部メソッドに抽出し、`using` スコープ終了後（`IsBusy=false` 確定後）にダイアログを表示する構造へ変更。あわせて `CsvExportService` の Export 系メソッドと `OperationLogExcelExportService.ExportAsync` を `virtual` 化し、`SaveFileDialog` を介さずにエクスポート経路を直接起動できるようにして単体テストから検証可能とした。回帰防止として `DataExportImportViewModelTests` に 3 件（成功時・失敗時・例外時の `IsBusy` 状態）、`OperationLogSearchViewModelTests` に 2 件（成功時・失敗時）を追加し、`ShowInformation` / `ShowError` の `Callback` で呼び出し時点の `IsBusy` を捕捉して `false` であることをアサート（#1383）
- 共有フォルダモードで履歴画面を開いたまま30秒以上待っても、他 PC の貸出/返却/チャージ操作が表示中の履歴画面に反映されない問題を修正。カード一覧（`LentCards`）とダッシュボード（`CardBalanceDashboard`）は `SharedModeMonitor.HealthCheckCompleted` 経由の `RefreshSharedDataAsync` で 30 秒ごとに再読み込みされていたが、履歴画面（`HistoryLedgers`）の再読み込みだけが同経路から欠落していた。そのため、履歴画面を開いている利用者は「いったん別カードの履歴を表示してから戻る」操作でしか最新状態を得られず、他 PC 側の操作反映の把握に支障があった。`RefreshSharedDataAsync` に、貸出（L997）・返却（L1072）・チャージ（L2326）等のローカル操作完了ハンドラで Issue #526 から採用されている `if (IsHistoryVisible) await LoadHistoryLedgersAsync();` パターンをそのまま適用して整合性を取った。`CurrentState == AppState.Processing` のスキップガード（#1282 で明文化）は手前にあるため、カードタッチ対応中に不要な再描画が走ることはない。回帰防止として `MainViewModelSyncDisplayTests` に 3 件（履歴表示中は `GetPagedAsync` が対象カード IDm で呼ばれること / 履歴非表示時は呼ばれないこと / 処理中スキップ時は呼ばれないこと）を追加（#1381）
- 利用履歴詳細 CSV インポートのプレビューで表示される件数がインポート後の処理件数と一致しない問題を修正。プレビューは「Ledger グループ単位」（ledger_id ごとに +1）、インポート後は「CSV 行数単位」（`detailRows.Count` 加算）でカウントしていたため、例えば `ledger_id=1` に 3 行ある CSV だとプレビューは `UpdateCount=1`、インポート後は `ImportedCount=3` と表示され、利用者から「プレビュー 20 件 / 読み込み後 32 件」のような乖離として報告されていた。インポート側（`NewLedgerFromSegmentsBuilder.BuildAndInsertAsync` は `detailRows.Count` を返却、既存 Ledger 経路も `importedCount += detailRows.Count` / `skippedCount += detailRows.Count`）が既に行数ベースであることから、プレビュー側を行数ベースに揃える方針で `CsvImportService.Detail.cs` の `PreviewLedgerDetailsInternalAsync` を修正。`newCount` は新規 Ledger セグメント非分割時 `detailList.Count` を、セグメント分割時は各 `segment.Details.Count` を加算、`updateCount` / `skipCount` は `detailRows.Count` を加算する形に統一。プレビュー画面のアイテム件数（Items.Count）は従来どおり表示単位の Ledger グループ／セグメント単位を維持し、集計値（NewCount/UpdateCount/SkipCount）のみ CSV 行数ベースに変更。回帰防止として `LedgerDetails_既存更新_プレビューとインポートの件数が一致` / `LedgerDetails_既存スキップ_プレビューとインポートの件数が一致` / `LedgerDetails_新規作成_プレビューとインポートの件数が一致` の 3 件を `CsvImportServiceTests` に追加し、(a) プレビューとインポートの個別件数が CSV 行数で一致、(b) プレビュー合計（New+Update+Skip）とインポート合計（Imported+Skipped）が完全一致することを検証。既存テスト 3 件（`PreviewLedgerDetailsAsync_正常データ_プレビュー成功` / `PreviewLedgerDetailsAsync_異なる日付の空欄ID行_日付ごとに別プレビュー` / `PreviewLedgerDetailsAsync_利用履歴ID空欄_新規作成としてプレビュー`）も新仕様に合わせて期待値を更新（#1379）
- 履歴画面の「変更」ボタンを押した直後の編集ダイアログで利用者欄が空欄になる問題を修正。返却時に作成される利用 Ledger（鉄道・バス・残高不足統合）で `LenderIdm` カラムが設定されておらず（`StaffName` のみ設定）、編集ダイアログが `s.StaffIdm == ledger.LenderIdm` で職員照合していたため一致せず空欄表示となっていた。さらに `SelectedStaff = null` のまま備考等を修正して保存すると `StaffName` まで null で上書きされ、スナップショット情報も失われる二次被害があった。(1) `LendingService.CreateUsageLedgersAsync` のシグネチャに `staffIdm` を追加し、生成する全利用 Ledger（残高不足マージ・通常利用・既存統合補正）で `LenderIdm` を設定。呼び出し元 `PersistReturnAsync` は `lentRecord.LenderIdm` を、`RegisterCardWithUsageAsync` は `null` を渡す（カード登録時は利用者情報なしの既存仕様維持）。ポイント還元のみの Ledger は機械操作扱いで `LenderIdm`/`StaffName` とも `null` を維持。(2) 既存データの `LenderIdm = NULL` 行救済として `LedgerRowEditViewModel.InitializeForEditAsync` に `StaffName` フォールバックを追加。`LenderIdm` 一致が無い場合は同名アクティブ職員を選択（同名別職員を選ぶリスクはあるが物品出納簿は氏名表示のみで区別不可のため許容）。回帰防止として `LedgerRowEditViewModelTests` に 4 件、`LendingServiceTests` に 2 件のテストを追加。`02_DB設計書.md` の `lender_idm` 列説明と `07_テスト設計書.md` の UT-017a / UT-029a2 セクションも更新（#1303）
- カード/職員 CSV インポートのプレビューが備考欄の変更を検出しない問題を修正。`skipExisting=false`（既存データ上書きモード）で備考のみを書き換えた CSV をインポートした場合、プレビュー画面は「変更点なし → スキップ」と表示していたが、実インポートでは備考が更新されるためプレビュー表示と実挙動が乖離していた。原因は `CsvImportService.Card.cs` / `CsvImportService.Staff.cs` のプレビュー処理が備考フィールドをそもそも読み取っておらず、差分検出メソッド (`DetectCardChanges` / `DetectStaffChanges`) の比較対象に備考が含まれていなかったこと。(1) プレビューでも CSV の備考列を読み取って式インジェクション対策 (`FormulaInjectionSanitizer.Sanitize`) を適用、(2) 差分検出メソッドの引数に `newNote` を追加し既存 Note と比較、(3) 比較時はインポート本体と同じ `IsNullOrWhiteSpace` 正規化を適用して `null` と空文字を同一扱いに統一。これによりプレビューの `ImportAction.Update` / `Skip` 判定が実インポート結果と一致するようになった。回帰防止として `PreviewCardsAsync_NoteChanged_DetectsAsUpdate` / `PreviewCardsAsync_AllFieldsIdentical_DetectsAsSkip` / `PreviewCardsAsync_NoteNullVsEmpty_TreatedAsIdentical` / `PreviewStaffAsync_NoteChanged_DetectsAsUpdate` / `PreviewStaffAsync_AllFieldsIdentical_DetectsAsSkip` の5件を追加（#1370）
- カード/職員 CSV インポートで「既存データはスキップする」ON（既定）のときも備考を含む差分がある行は更新するように仕様変更（#1370 の続編）。`skipExisting=true` の従来仕様は「既存行（IDm 一致）は内容を問わず即スキップ」であり、備考だけを書き換えた CSV を再インポートしても反映されず、ユーザーは「既存データはスキップする」を OFF にして全項目上書き運用を強いられていた。本修正で「スキップ」の意味を **全項目（カード種別/管理番号/備考 または 氏名/職員番号/備考）が一致する場合のみスキップ** に再定義し、1 項目でも差分があれば更新対象とする。(1) `CsvImportService.Card.cs` / `CsvImportService.Staff.cs` のインポート本体とプレビューの両方で `DetectCardChanges` / `DetectStaffChanges` を常に呼び、差分 0 件かつ `skipExisting=true` のときのみスキップ（Import / Preview の判定ロジックを統一）、(2) `skipExisting=false` は従来どおり「常に更新」（no-op 更新も含む）として Ledger の既存挙動と整合。回帰防止として `ImportCardsAsync_NoteChanged_SkipExistingTrue_UpdatesInsteadOfSkip` / `ImportStaffAsync_NoteChanged_SkipExistingTrue_UpdatesInsteadOfSkip` / `PreviewCardsAsync_NoteChanged_SkipExistingTrue_DetectsAsUpdate` / `PreviewStaffAsync_NoteChanged_SkipExistingTrue_DetectsAsUpdate` の 4 件を追加。既存テスト 7 件（`ImportCardsAsync_ExistingCard_Skipped` / `ImportStaffAsync_ExistingStaff_Skipped` / `PreviewCardsAsync_ExistingCard_ShowsAsSkip` / `PreviewCardsAsync_AllFieldsIdentical_DetectsAsSkip` / `PreviewCardsAsync_NoteNullVsEmpty_TreatedAsIdentical` / `PreviewStaffAsync_AllFieldsIdentical_DetectsAsSkip` / `ImportCardsAsync_AlreadyRestoredCard_SkipExistingTrue_Skipped`）も新仕様に合わせて「CSV と既存レコードを完全一致」構成で保持（#1376）
- 自動バックアップと手動バックアップが起動直後から「DbContext.LeaseConnection() は WPF UI スレッドから呼び出せません」で失敗する問題を修正。Issue #1281 で追加した UI スレッドガードを回避するための Issue #1356 オフロード対応 (`InitializeDatabaseAsync` / `CleanupOldDataAsync` / `VacuumAsync`) の際、`BackupService` が対象外になっていた。(1) 自動バックアップ経路: `App.PerformStartupTasksAsync` (UI スレッド) から fire-and-forget される `BackupService.ExecuteAutoBackupAsync` は最初の `await _settingsRepository.GetAppSettingsAsync().ConfigureAwait(false)` がキャッシュヒット時（`App.ApplySavedSettings` で事前キャッシュ済み）に同期完了するため UI スレッドに留まり、続く `BackupDatabaseTo` → `LeaseConnection()` でガード発火。`BackupDatabaseTo` 呼び出しを `await Task.Run(() => BackupDatabaseTo(backupFilePath)).ConfigureAwait(false)` で包む。(2) 手動バックアップ経路: `SystemManageViewModel.CreateBackupAsync` が同期 `_backupService.CreateBackup(...)` を UI スレッドで呼んでおり同じくガード発火。`BackupService` に `public virtual Task<bool> CreateBackupAsync(string)` を追加（内部で `Task.Run` により既存 sync 実装へ委譲、sync 版はテスト経路で継続利用のため残置）、`SystemManageViewModel` の 3 呼び出し箇所（手動バックアップ + リストア前バックアップ DB/設定）を `await _backupService.CreateBackupAsync(...)` へ置換。回帰防止として `BackupServiceUiThreadGuardTests`（3件）を新設し、`DbContext.IsOnUiThread` を `ManagedThreadId` 判定フックに差し替えて (a) sync 版 `CreateBackup` が UI スレッド模擬時にガード発火して `false` を返すこと、(b) `CreateBackupAsync` が UI スレッド模擬時でも成功すること、(c) `ExecuteAutoBackupAsync` が UI スレッド模擬時でもバックアップファイルを生成することを検証（#1361）
- 共有フォルダモードの自動同期が起動直後から機能せず表示が「同期待ち...」のまま固定され、手動同期ボタンでのみ一時的に反映される問題を修正。Issue #1350 (Phase 2) で `SharedModeMonitor.ExecuteHealthCheckAsync` に `.ConfigureAwait(false)` を追加した際、30 秒タイマー経由の `HealthCheckCompleted` イベントが thread pool スレッドから発火されるようになり、購読側の `MainViewModel.OnSharedModeHealthCheckCompleted` が UI バインド済み `ObservableCollection` (`WarningMessages` / `LentCards` / `CardBalanceDashboard`) を非 UI スレッドから更新する形になっていた。実機 WPF では `NotSupportedException` が発生し `RefreshSharedDataAsync` の `try/catch` (LogDebug) で握り潰されるため `SharedModeMonitor.RecordRefresh()` が呼ばれず同期表示が更新されない。手動同期パス (`ManualRefreshAsync`) はボタンクリックから UI スレッドで完結するため正常に動くことで「一度は効くが止まる」症状として現れていた。`OnSharedModeHealthCheckCompleted` を `_dispatcherService.InvokeAsync(Func<Task>)` で UI スレッドへマーシャリングするよう修正（既存 `OnCardRead` と同一パターン、Service 層の `ConfigureAwait(false)` 規約は維持）。回帰防止として `MainViewModelIntegrationTests` に `SharedMode_HealthCheckCompleted_UI依存更新はIDispatcherServiceでmarshallingされること` テストを追加し、`SynchronousDispatcherService` に呼び出し回数カウンタ (`InvokeAsyncFuncCallCount` / `InvokeAsyncActionCallCount`) を追加して marshalling 経路の使用を検証（xUnit は `DispatcherSynchronizationContext` を持たず本バグは既存テストで検出不能だったため経路検証で固定化）（#1359）
- 起動時に `DbContext.LeaseConnection()` の UI スレッドガード (#1281) が発火して「起動エラーが発生しました。DbContext.LeaseConnection() は WPF UI スレッドから呼び出せません。」と表示され起動失敗する問題を修正。Issue #1281 (#1343) で UI スレッド検出ガードを入れた際、`App.OnStartup` から直接呼ばれる 3 つの同期 API (`DbContext.InitializeDatabase` / `CleanupOldData` / `Vacuum`) のオフロード対応が漏れていた。(1) `DbContext` に `InitializeDatabaseAsync` / `CleanupOldDataAsync` / `VacuumAsync` を追加（内部は `Task.Run` で既存 sync 実装へ委譲、sync 版はテスト経路で継続利用のため残置）、(2) `App.OnStartup` を `async void` 化、`App.InitializeDatabase` → `InitializeDatabaseAsync` / `PerformStartupTasks` → `PerformStartupTasksAsync` に改名して 3 呼び出しを await、(3) `PerformStartupTasksAsync` 内の `Task.Run(() => settingsRepository.GetAppSettings()).GetAwaiter().GetResult()` も素直な `await settingsRepository.GetAppSettingsAsync()` に置換。xUnit は `DispatcherSynchronizationContext` を持たないため既存テストでは検出不能で、実機起動のみで顕在化していた（#1356）
- `DbContext.LeaseConnection()`（同期版）を WPF UI スレッドから呼び出すとデッドロックするリスクを排除。(1) メソッド入り口に `Dispatcher.CheckAccess()` 相当の UI スレッド検出を追加し、該当時は `InvalidOperationException` を代替手段（`LeaseConnectionAsync()` / `Task.Run` オフロード）の案内付きでスロー。検出は `System.Windows` への直接依存を避けるため `SynchronizationContext.Current` の型名（`DispatcherSynchronizationContext`）で判定、テストで差し替え可能な静的フック `IsOnUiThread` を提供、(2) UI スレッドから同期版を呼んでいた既存箇所を修正: `App.xaml.cs` の DI SummaryGenerator ファクトリ・`ApplySavedSettings`・`PerformStartupTasks` は `Task.Run(...).GetAwaiter().GetResult()` でバックグラウンドスレッドにオフロード（起動時は競合する非同期操作がないため安全）、`LendingService.LendAsync` のチャージ摘要生成と `ReportService.CreateMonthlyReportAsync`/`CreateMonthlyReportsAsync` の設定取得は `GetAppSettingsAsync()` 版に置換。回帰防止として `DbContextUiThreadGuardTests`（6件）を追加し、(a) UI スレッド模擬時の例外発生、(b) エラーメッセージに代替 API 案内が含まれること、(c) 非 UI スレッド/`LeaseConnectionAsync`/`Task.Run` 経由では正常動作、(d) 既定実装が xUnit 環境で false を返すことを検証（#1281）
- タイムアウト系マジック定数を `Common/AppConstants.cs` に集約（`DefaultStaffCardTimeoutSeconds=60` / `DefaultCardRetouchTimeoutSeconds=30` / `DefaultCardLockTimeoutSeconds=5`）。`AppOptions` のデフォルト値は `AppConstants` を参照する形に統一。業務ルール由来のため各定数に `.claude/rules/business-logic.md` への参照コメントを追加。値は不変のため振る舞いに変化なし（appsettings.json オーバーライドも引き続き機能）（#1288）
- Service 層の async メソッドに `ConfigureAwait(false)` を一貫適用（Phase 2: 残り 11 ファイル、約 94 箇所）。Phase 1 (#1287) の残作業として、`DebugDataService` / `CsvExportService` / `LedgerMergeService` / `LedgerSplitService` / `DashboardService` / `PrintService` / `SharedModeMonitor` / `LedgerConsistencyChecker` / `NewLedgerFromSegmentsBuilder` / `OperationLogExcelExportService` / `WarningService` を対応。`(await task).ToDictionary` や括弧内 await など、Phase 1 で未対応だったパターンも個別に修正。UI 依存の `DialogService` / `StaffAuthService` は個別検討のため対象外（#1350）
- Service 層の async メソッドに `ConfigureAwait(false)` を一貫適用（Phase 1: 11 ファイル、約 130 箇所）。WPF UI スレッドへの不要な継続 dispatch を排除し、性能向上とデッドロック予防を図る。対象: `DbContext`, `LendingService`, `OperationLogger`, `CsvImportService` 全 partial, `BackupService`, `ReportService`, `ReportDataBuilder`。`.editorconfig` で `CA2007` を Service 層のみ suggestion レベルに有効化（ViewModels/Views/tests は無効化）。`.claude/rules/async-configureawait.md` に規約文書化。残りの Service は #1350 (Phase 2) で対応予定（#1287）
- `SharedModeMonitor` に `IDisposable` を実装し、`App.OnExit` で明示的に `Dispose()` を呼ぶようにした。従来は Singleton 登録された本サービスが `IDisposable` を実装していなかったため、アプリ終了時にタイマー（30秒ヘルスチェック + 1秒同期表示）が確実に停止される保証がなかった。`Dispose()` は `Stop()` を呼び冪等フラグを立てる実装で、二重 Dispose に対し冪等。Dispose 後の `Start()` は `ObjectDisposedException` をスロー。共有モードでネットワーク切断状態のまま終了するケースでのリソースリーク予防。単体テスト 5 件を追加（#1286）
- 例外処理の無言握りつぶしを解消し、トラブル時のデバッグ性・トレーサビリティを向上（5箇所）。(1) `DbContext.CheckConnection` の `catch (Exception)` で戻り値 false を返す処理に `LogDebug(ex, ...)` を追加（疎通失敗は頻繁に起きる想定のためログ肥大化回避で Debug レベル）、(2) `CardManageViewModel` のカード登録後の初期残額レコード作成失敗を `LogWarning` で記録（稀な想定のため Warning レベル、フィールド `_logger` を注入）、(3) `MainViewModel.RefreshSharedDataAsync` の共有モードリフレッシュ失敗を `LogDebug` で記録（タイマー起動で頻繁に呼ばれるため Debug）、(4)(5) `CsvImportService.Card`/`CsvImportService.Staff` の CSV インポートトランザクション内 `catch (SQLiteException)` と `catch (Exception)` 両方で `LogError(ex, ...)` を追加してロールバック理由をログに残してから再スロー。ViewModels の ILogger は optional constructor 引数（既存テストとの互換性維持）、CsvImportService は 6 引数オーバーロードで Moq プロキシ互換性を維持。`DbContextCheckConnectionLoggingTests`（3件）と `CsvImportServiceExceptionLoggingTests`（4件）を追加（#1282）

**アクセシビリティ改善**
- メイン画面の F1（帳票）/ F7（ヘルプ）ボタンが Windows 慣習（F1=ヘルプ）と異なる割当であることをユーザーへ明示。(1) `MainWindow.xaml` の F1 ボタン ToolTip に「※ヘルプは F7 です」、F7 ボタン ToolTip に「※F1 ではありません」を追記、(2) `AutomationProperties.HelpText` にも同旨の注記を追加しスクリーンリーダーで読み上げられるようにした、(3) ユーザーマニュアル「3.1 メイン画面」の機能ボタン表直下、管理者マニュアル「付録 A. ショートカットキー」直下に採用理由を含む注記を追加、(4) 画面設計書 `03_画面設計書.md §5.1` にメイン画面ファンクションキー表（F1〜F7）と Issue #1289 案2（現状維持＋明示責任）の設計判断を記載。案2 を採用した理由は、月次帳票が本アプリ最頻出操作であり F1 の優位性を維持すること、および既存ユーザーの操作記憶を守ること。回帰防止として `MainWindowKeyBindingTests`（4件）を追加し、KeyBinding の紐付け先コマンドと F1/F7 ボタンの ToolTip/HelpText に慣習差異の注記が含まれることを静的解析で検証（#1289）
- トースト通知が文字サイズ「大/特大」設定時に画面外へはみ出したり読み切れなくなる問題を修正。(1) ウィンドウサイズを固定 360px から `MinWidth`/`MaxWidth=520`/`MaxHeight` の動的制約に変更、(2) フォントサイズに応じて MinWidth と MaxHeight を線形スケール（Medium 360×220 → ExtraLarge 468×292）、(3) タイトルは `TextTrimming=CharacterEllipsis` で単一行に収め高さ暴走を防止、(4) 低残高警告文を簡潔化（「残額が少なくなっています（しきい値: 10,000円）」→「残額不足（<10,000円）」）。計算ロジックは `Common/ToastLayoutCalculator` に抽出して単体テスト可能化（#1273）
- 色覚多様性対応: 貸出/返却/払戻済状態を「色＋アイコン＋テキスト＋スクリーンリーダー用説明文」の4要素で統一。`Common/LendingStatusPresenter` 新規実装で、状態表示の三点セット（アイコン `📤`/`📥`/`🚫`、短ラベル、完全説明文）を一元管理。`CardBalanceDashboardItem` / `CardDto` に `LentStatusAccessibilityText` を追加し、MainWindow ダッシュボード・カード管理ダイアログの `AutomationProperties.Name` にバインド。カード管理ダイアログでは従来テキストのみだった状態列にアイコンを追加（#1274）
- 主要ダイアログ（LedgerRowEditDialog / SettingsDialog / DataExportImportDialog / BusStopInputDialog）のラベルを `TextBlock` から WPF `Label` + `Target` 属性へ移行。スクリーンリーダーがラベルと入力コントロールの関連付けを認識できるようにし（WCAG 2.1 SC 1.3.1 達成）、同時に `_X` 記法で Alt+キーのアクセスキーを付与（例: 日付=Alt+D、摘要=Alt+S）。`Target="{Binding ElementName=...}"` でラベルと対象コントロールを紐づけ、`AutomationProperties.Name` と併用してスクリーンリーダー/キーボード両方を改善。割り当て一覧は `03_画面設計書.md §5.3` に記載。ダイアログ内でアクセスキーは重複させない原則で統一（#1276）
- 全ダイアログ（CardManageDialog / SettingsDialog / DataExportImportDialog / LedgerRowEditDialog / BusStopInputDialog）に初期フォーカスを設定。Window ルート要素に `FocusManager.FocusedElement="{Binding ElementName=...}"` を追加し、起動時に最も操作頻度の高いコントロール（カード一覧 / トースト位置 / データ種別 / 日付 など）へフォーカスを当てるようにした。BusStopInputDialog は ListView 内の動的 TextBox を対象とするため、既存の `ContentRendered` + `VisualTreeHelper` 走査方式（Issue #1133）で対応済みであることを確認。回帰防止として `DialogInitialFocusTests` を新規追加し、XAML 上の属性存在とコードビハインドの `Focus()` 呼出しを静的解析で検証。割り当て一覧は `03_画面設計書.md §5.2` に記載（#1277）
- `AutomationProperties.Name` / `HelpText` を全面整備（WCAG 2.1 AA 達成基準 4.1.2 準拠）。(1) MainWindow の使い方ガイド Border に HelpText 追加、(2) CardManageDialog のカード件数 TextBlock に `CountToAccessibilityTextConverter` 経由で動的な読み上げテキスト（「登録カードは5件です」/「登録カードはまだありません。新規登録してください」）と `LiveSetting="Polite"` を付与、(3) ReportDialog の先月/今月トグルボタンに選択状態を反映した `Name`/`HelpText`（「先月（選択中）」など）を DataTrigger ベースで実装、(4) ReportDialog の行チェックボックス・ListBox・ProgressBar・ステータステキスト・キャンセルボタン等に欠落していた `Name`/`HelpText` を追加、ステータステキストは `LiveSetting="Assertive"` で即時読み上げ。件数表示用に `Common/CountToAccessibilityTextConverter` を新規実装し 0 件 / 1 件以上 / null / 非数値 / 主語未指定等の境界ケースを `CountToAccessibilityTextConverterTests`（14件）で検証（#1278）
- 入力検証エラー時の視覚的フィードバックとフォーカス移動を追加（WCAG 2.1 SC 3.3.1 エラーの特定）。(1) `Common/Validation/NumericRangeValidationRule` を新規実装し、SettingsDialog の残額警告しきい値（0〜20,000 円）と LedgerRowEditDialog の受入・払出金額（0 以上）の WPF Binding に組み込み、入力エラー時に `Validation.HasError=true` を発火、(2) `AccessibilityStyles.xaml` に全 TextBox 用の暗黙的 `Style` + `Validation.ErrorTemplate` を追加し、赤枠（2px、色覚多様性対応のため枠線太さと⚠アイコンも併用）と ToolTip でエラーメッセージを表示、⚠アイコンに `AutomationProperties.Name="入力エラーあり"` を付与、(3) ViewModel 側に `FirstErrorField` プロパティを追加、`LedgerRowEditViewModel.Validate()` と `SettingsViewModel.SaveAsync()` が最初のエラー発生プロパティ名を設定、(4) Dialog のコードビハインドが `ContentRendered` 時（初期表示で既にエラー）および保存ボタン押下で `CanSave=false` のとき該当 TextBox に `Focus()` + `SelectAll()` を実行。`NumericRangeValidationRuleTests`（10件）と `LedgerRowEditViewModelTests` に `FirstErrorField` 検証テスト（5件）を追加（#1279）
- 全ダイアログに `MinHeight` / `MinWidth` を設定し低解像度環境（1366×768 ノート PC 等）で OK/キャンセルボタンが画面外に隠れる問題を解消。(1) LedgerRowEditDialog は `ResizeMode="NoResize"` → `CanResize`、`MinHeight=400` / `MinWidth=500` を追加し、入力フォーム領域を `ScrollViewer` で囲んでリサイズ時に縦スクロール可能に、(2) SettingsDialog は初期高さ 820px → 680px（1366×768 で収まる値）、`MinHeight=500` / `MinWidth=480`、`CanResize` へ変更、(3) SystemManageDialog に `MinWidth=520` を追加、(4) CardRegistrationModeDialog / CardTypeSelectionDialog / StaffAuthDialog（`SizeToContent="Height"` の小ダイアログ）に `MinWidth=380` / `MinHeight=220〜260` を追加。回帰防止として `DialogMinimumSizeTests` を新規追加し、(a) 全 17 ダイアログに `MinHeight` / `MinWidth` 属性が存在すること、(b) `MinHeight ≤ 720`（1366×768 実用高さ）、(c) 入力フォーム型ダイアログは ScrollViewer で囲まれていること、(d) LedgerRowEditDialog/SettingsDialog が `CanResize` であることを静的解析で検証（73件）（#1280）

**ユーザー体験改善**
- エラーメッセージを「何が / なぜ / どうすれば」の3要素を含む具体的な文言に改善。`ValidationService` の全バリデーター（CardIdm/CardNumber/CardType/StaffIdm/StaffName/WarningBalance）と `LedgerRowEditViewModel.Validate` の7種のメッセージを、実際の入力値・期待値・解決アクションを明示する形に統一。例: 「カード種別を選択してください」→「カード種別が未選択です。ドロップダウンから「はやかけん」「nimoca」等を選択してください」。`.claude/rules/error-messages.md` に品質ガイドライン（3要素構成・禁止パターン・行動指示型語尾・最小文字数基準）を追加し、`ValidationServiceErrorMessageQualityTests` で自動検証（#1275）

**開発基盤**
- ビルド時に大量発生していたコンパイラ／アナライザ警告 272 件を 0 件に整理。(1) `OperationLogger` の Issue #1265 で `[Obsolete]` 化された旧オーバーロード（`operatorIdm` 引数付き）を呼ぶ 7 ファイル・18 箇所を新 API に移行（`CardManageViewModel` / `StaffManageViewModel` / `MainViewModel` / `LedgerRowEditViewModel` / `LedgerDetailViewModel` / `LedgerSplitService` / `LedgerMergeService`）。操作者解決は `ICurrentOperatorContext` 経由へ一本化され、呼び出し元が渡していた `null` / `authResult.Idm` / `_operatorIdm` / `operatorIdm` は記録に使われなくなる（#1265 の方針完結）、(2) `MigrationHelpers.AddColumnIfNotExists` に欠落していた `<param>` 4 件（`connection` / `transaction` / `table` / `column`）を追記して CS1573 を解消、(3) `PathValidator` の XML ドキュメント内 `<see cref="Task.Run"/>` がオーバーロード曖昧（CS0419）かつ具体シグネチャ指定すると cref 解決失敗（CS1574）する両ハマりだったため、意味を保ったまま `<c>Task.Run</c>` 表記へ変更、(4) テストプロジェクト (`ICCardManager.Tests.csproj`) に `<NoWarn>` を追加してテスト特有の警告 9 種を限定抑制: `CS0618`（`[Obsolete]` API の挙動互換性検証で旧オーバーロードを直接呼ぶ）、`CS8600` / `CS8602` / `CS8603` / `CS8620` / `CS8625`（`null` を意図的に渡す／受けるテストケース）、`xUnit1012`（`null` を non-nullable パラメータに渡すテスト）、`xUnit1030`（`.claude/rules/async-configureawait.md` 規約でテストでは `ConfigureAwait(false)` を付けない）、`xUnit1031`（既存テストの同期 `.Result` / `.Wait()` 使用）。プロダクションコード側では抑制せず警告を引き続き有効化
- UI スレッドガード系テスト (`DbContextUiThreadGuardTests` / `BackupServiceUiThreadGuardTests`) が並列実行時に CI でフレーキー失敗する問題を解消。両クラスは `DbContext.IsOnUiThread` 内部フック（Issue #1281 で `AsyncLocal<Func<bool>?>` 化済み）を書き換えるが、`BackupService.ExecuteAutoBackupAsync` 内の `Task.Run` が親 ExecutionContext の AsyncLocal 値を継承する特性と xUnit のテストクラス並列実行が相互作用し、稀に別テストクラスのフックが読み出されるレースが発生していた（PR #1371 の初回 CI で顕在化）。新設した xUnit Collection 定義 `DbContextUiThreadHookCollection` (`DisableParallelization = true`) に両テストクラスを所属させ、同フックを書き換えるテストをシリアル実行させることで解消。本番コードは変更なし（影響範囲をテストプロジェクトに限定）。回帰防止として `DbContextUiThreadHookCollectionConfigurationTests`（5件）を追加し、(a) Collection 定義の `CollectionDefinition` 属性と `DisableParallelization=true` の維持、(b) 対象 2 クラスへの `[Collection]` 属性付与、(c) 対象クラスが `IDisposable` を実装してフックを既定値へ戻す形であることを静的解析で検証。`docs/design/07_テスト設計書.md §2.46` に並列実行制御の運用ルールを追記（#1372）
- リポジトリルートに `.gitattributes` を新設し、`packages.lock.json` / `*.json` / `*.yml` / `*.yaml` / dotfile (`.editorconfig` / `.gitignore` / `.gitattributes`) / `*.sh` の改行コードを **LF に正規化**。WSL / Windows 併用開発時に `dotnet restore` が生成する lock file の CRLF/LF 混在が毎回 "modified" として git 差分に現れる「誤差分ループ」問題を解消（Issue #1361 調査中に顕在化）。`.cs` / `.xaml` / `.csproj` など Windows 主戦場ファイルは Visual Studio の保存設定との衝突を避けるため **`* text=auto` を意図的に指定せず**対象外（将来 Issue で別途検討）。また `.db` / `.xlsx` / `.docx` / `.png` / `.ico` / `.wav` / `.dll` / `.exe` 等のバイナリを明示的に `binary` 指定して誤検出を防止。既存ファイルで CRLF だった `ICCardManager/.gitignore` と `ICCardManager/tests/ICCardManager.UITests/packages.lock.json` の 2 件を `git add --renormalize .` で LF に一括正規化。開発者ガイド §5.8 に改行コードポリシーを追記（#1368）
- 依存パッケージの既知 CVE 継続監視の仕組みを導入。(1) GitHub Actions `vulnerability-scan.yml` が週次 + csproj 更新時に `dotnet list package --vulnerable --include-transitive` を自動実行し、検出時にジョブ失敗で通知、(2) Dependabot 設定で本体/テスト/UIテスト/github-actions の4エコシステムを週次監視・PR自動作成、(3) 開発者ガイド §5.7 に重大度別 SLA（Critical/High は24時間以内）と対応手順を明記、(4) リリーススキル (`/release`) に Phase 1 前のセキュリティチェック項目を追加（#1272）

**リファクタリング**
- `MainViewModel.SetState()` のデッドコードを除去。`backgroundColor` 引数 + 旧色リテラル (`#FFE0B2` / `#B3E5FC` / `#FFEBEE`) を case キーとする switch 式と、その代入先である未バインド 5 プロパティ (`StatusBackgroundColor` / `StatusBorderColor` / `StatusForegroundColor` / `StatusLabel` / `StatusIconDescription`) を削除。唯一の呼び出し元 `ResetState()` は `backgroundColor` 引数を省略しており switch 式は常にデフォルトケースに落ちる + XAML 側 Binding が一切存在しない（grep 0 件）状態で、Issue #1392 で UI 背景色を `LendingBackgroundBrush` 等のリソースキーへ集約した際に取り残された死骸だった。`SetInternalState()` 内の同 5 プロパティへのクリア処理も同時削除。回帰防止として `MainViewModelTests` に `MainViewModel_ShouldNotExposeDeadStatusStyleProperties` を追加（Reflection で削除済 5 プロパティの再導入を検出）。残った旧色リテラルは `CardBalanceDashboardItem.RowBackgroundColor` の `#FFEBEE`（残額警告行ハイライト用、別目的のため対象外）のみとなり、`MainViewModel` から旧色文字列は完全消滅（#1398）
- `LendingService.LendAsync` / `ReturnAsync` を責務ごとに internal ヘルパーメソッドへ分割し、可読性とテスト容易性を向上（`LendAsync` 121行 → 62行、`ReturnAsync` 182行 → 99行）。抽出したヘルパー: `ValidateLendPreconditionsAsync` / `ValidateReturnPreconditionsAsync` / `ResolveLentRecordAsync` / `ResolveInitialBalanceAsync` / `InsertLendLedgerAsync` / `FilterUsageSinceLent` / `ResolveReturnBalanceAsync` / `ApplyBalanceWarningAsync` / `PersistReturnAsync`。public API は一切変更せず、既存テストは全件 pass。抽出ヘルパー向けの `LendingServiceHelperTests`（23件）を追加（#1283）
- DB マイグレーションの冪等性（二重実行安全性）を担保。`MigrationHelpers.AddColumnIfNotExists` を新設し、非冪等だった 5 つの `ALTER TABLE ADD COLUMN` 型マイグレーション（#002/#003/#005/#006/#009）を冪等化。共有モードで複数 PC が初回起動時にマイグレーション競合した場合や、`schema_migrations` テーブル部分破損時の再適用エラーを防止。全 9 マイグレーションの二重実行テスト (`MigrationIdempotencyTests`, 9件) と `MigrationHelpers` 単体テスト (7件) を追加。`.claude/rules/migrations.md` に冪等性チェックリストを新設し、開発者ガイド §3.5 を自動検出ロジック (`DiscoverMigrations()`) に合わせて更新（#1285）
- `CsvImportService.Ledger.cs`（1031行 → 520行）と `Detail.cs`（1042行 → 761行）を責務分割。(1) Import/Preview 間で重複していた利用履歴行パース ~200 行を `LedgerCsvRowParser` に共通化、(2) 利用履歴詳細の 13 列パースを `LedgerDetailCsvRowParser` に抽出、(3) Detail の「履歴ID空欄→新規 Ledger 自動作成」ロジックを `NewLedgerFromSegmentsBuilder` に責務分離（Issue #906/#918/#1053 関連のロジック）、(4) 検証系 helper 4 メソッドを `CsvImportService.LedgerValidation.cs` partial に分離。public API は一切変更せず、既存 94 件のテストは全件 pass。抽出クラス向けの単体テスト 21 件を追加（`LedgerCsvRowParserTests` / `LedgerDetailCsvRowParserTests` / `NewLedgerFromSegmentsBuilderTests`）（#1284）

**ドキュメント**
- マニュアル参照済みスクリーンショット6点の取りこぼし補完（#1448）
- マニュアルバージョン記載とテスト統計の実装同期（#1446）
- 管理者マニュアル §5.5 / TakeScreenshots.ps1 の「論理削除」表記を「払戻済」状態に整合 (Issue #1439)（#1439）
- 管理者マニュアル §8.4 felicalib.dll 検証失敗エラーダイアログのスクリーンショット参照追加 (Issue #1418)（#1418）
- 管理者マニュアル §9.1 自動バックアップ頻度を実装に合わせて修正 (Issue #1407)（#1407）
- 管理者マニュアル §5.6.3 の手順番号 10 欠番を修正 (Issue #1406)（#1406）
- FAQ Q5 の管理者マニュアル参照リンクを実在節に修正 (Issue #1405)（#1405）
- ReturnBackgroundBrush の兼用方針を明記し色テーブルの重複を解消 (Issue #1399)（#1399）
- テスト設計書に「1.6 テストコードの読み方（非プログラマ向け）」節を追加。利用者（検収担当含む）が §2 以降のテストコード引用を参照する際に、テストファイル/メソッド命名の読み方、Arrange-Act-Assert 3 段構造、xUnit/FluentAssertions/Moq の主要記法を把握できるよう解説。実在テスト `DeleteOrClearFileTests` を引用し、C# やプログラミングの詳細な文法に詳しくなくても「何を確認しているテストか」が読み取れる構成とした。あわせて §0 の「対象読者」欄に利用者・検収担当を追加、§1.4 章マップ導入文に §1.6 への誘導注記を追加（#1385）
- 管理者マニュアル（1430 行・11 章構成）の冒頭に「こんなときはどこを見る？（作業別クイックガイド）」と「管理者の年間作業イメージ」の 2 セクションを新設。辞書型で網羅的に書かれた本マニュアルを、IT 操作に必ずしも詳しくない庶務担当者でも作業起点（「新しく職員が加わった」「アプリが起動しない」「残高不足で現金チャージした」等）で逆引きできるよう、6 カテゴリ（導入・職員・カード・データ・設定セキュリティ・トラブル）× 計 40 余りのタスクを既存章のアンカーにリンクする表として整備。あわせて発生頻度別（最初の導入時のみ／毎月／随時／年 1 回／日常）の早見表で「多くの作業は最初と随時に集中し、毎月の定例作業は帳票出力程度」という管理者の業務イメージを提示。§1.1 末尾にクイックガイドへの導線を追加、目次にも 2 項目と欠落していた §11・付録を追加。新規リンク 53 件は `#<番号>-<見出し>` の GFM 準拠スラッグで生成済みで、対応する見出しが本文に実在することを自動検証（#1387）
- 管理者マニュアル §2.1（複数 PC で利用する場合の事前準備）から、庶務担当者が読まなくてよい技術的な ACL 設定記述（SMB 共有権限／NTFS 権限／PowerShell `Grant-SmbShareAccess`・`icacls` コマンド・ドメイングループの付与手順）を削除し、脚注参照＋新規付録 C「アクセス権限の設定（IT 担当者向け）」へ移設。本文には「各 PC のログインアカウントでその共有フォルダが開けること（エクスプローラで開いて新規にファイルを保存できること）を確認」という作業起点の指示だけを残した。想定環境では組織内ファイルサーバの共有フォルダに読み取り／書き込み権限があらかじめ付与されているため、追加の権限設定は通常不要。新設フォルダや既定ドメイングループ外の利用など例外ケースは付録 C で案内。隣接する §2.3（2 台目以降のセットアップ）の前提注釈にも残っていた「ACL 設定」も同期的に「共有フォルダの準備」に平文化。目次に付録 C を追加。自動検証で本文側（付録 C 以外）から対象技術用語の残存がないことを確認（#1389）
- 自動バックアップで保持される世代数を「最新の数世代のみ」「古いバックアップは自動的に削除されます」という曖昧表現から、実装値である**最大 30 世代**へ具体化。ユーザーマニュアル §7.2「バックアップ先フォルダ」、管理者マニュアル §3.3「バックアップ設定」「バックアップの世代管理」、§6.1「自動バックアップ」の 3 箇所を更新し、(a) 保持件数（30）と削除タイミング（次回の自動バックアップ実行時）、(b) 設定画面から変更不可である旨、(c) それ以上の長期保管が必要な場合は手動バックアップで別の場所に退避する運用案内、(d) 1 日 1 回起動運用なら直近約 1 か月分が復元可能な目安、を明記。実装定数 `BackupService.MaxBackupGenerations` (= 30, `private const`) とマニュアル記載値の同期を `BackupServiceTests.MaxBackupGenerations_MatchesDocumentedValue` で固定化（Reflection で定数値を読み 30 と一致することを表明、定数変更時は同テストとマニュアルの両方を更新する必要があるため doc rot を構造的に防止）。既存の `_Over30Generations_DeletesOldBackups` / `_Exactly30Generations_DeletesOldest` テストは振る舞いを上限方向のみ検証していたため、本テストで「ちょうど 30」を双方向に固定（#1408）
- ユーザーマニュアル巻末に「付録 A 用語集」を新設。本文中で初出する専門用語（ピッすい／交通系ICカード／職員証／FeliCa／IDm／ICカードリーダー／PaSoRi／30秒ルール／貸出中／バス停名未入力／物品出納簿／共有フォルダモード）の 12 項目を Markdown テーブルで掲載し、後方から参照できる索引を提供。非 IT ユーザー（庶務担当者等）が初読時にわからない用語に出会うたびに本文を遡る手間を解消する。管理者マニュアル付録 B「用語集」と重複する `IDm` / `物品出納簿` の定義はユーザー文脈に合わせて補足を加えつつ意味は同期。目次にも 10 項目目として「付録 A 用語集」を追加（#1419）
- 管理者マニュアル付録 B「用語集」を 3 項目から 12 項目へ拡充。本文（特に §10 トラブルシューティング）で頻出する技術用語のうち、従来未掲載だった 9 項目（共有モード／繰越月／開始ページ／30秒ルール／バス利用／journal_mode=DELETE／busy_timeout／VACUUM／SQLITE_BUSY・SQLITE_LOCKED）を追加。共有フォルダモード・SQLite 関連用語には対応する解説節（§2.5 / §5.2 / §6.5 / §10.4）への内部リンクを併記し、用語集→詳細解説へ自己解決動線を構築。既存 3 項目（IDm / 論理削除 / 物品出納簿）はユーザーマニュアル付録 A の表現と意味を同期しつつ、`is_deleted` フラグ名や ledger / operation_log の物理削除方針など管理者文脈の補足を加味して再記述（#1420）
- ユーザーマニュアル §7.2「変更できる設定」に各設定項目のスクリーンショットを追加。従来は設定画面全体の `settings.png` 1 枚のみで、設定変更時の見え方がテキスト表だけでは伝わりにくかった。(a) トースト通知の表示位置 4 パターン（右上／左上／右下／左下を実画面で）、(b) 文字サイズ 4 段階（小／中／大／特大）の比較、合計 8 枚を追加し、各テーブル直下に「**右上（デフォルト）**」等の太字ラベルとともに配置。各画像の前に置いた太字ラベルでレンダリング時にもどの設定値の画像かが視認できるようにした（ALT 属性のみだとホバーしないと見えないため）。利用者が「文字サイズを大にすると画面がどう変わるか」「右下にトーストを出すとどの位置に表示されるか」を事前に把握できるようにし、設定変更を躊躇する状況を解消。なお音声設定・部署設定はテキスト表で十分意味が伝わるため画像追加対象から除外（#1409）
- ユーザーマニュアル §9.3「『エラー』と表示される」および §9.5「『ネットワーク接続が切断されました』と表示される」にエラー表示のスクリーンショット参照を追加。従来は §9.3 で `card_type_selection.png`（未登録カード時のカード種別選択ダイアログ）1 枚のみで、(a) カードリーダー未接続時のステータスバー表示と (b) 共有モードでのネットワーク切断警告バナーの実画面が無く、利用者が「マニュアルに書かれているこのケース」と視覚的に同定する手がかりが不足していた。(1) §9.3 の「カードリーダーが認識されないとき」サブセクション冒頭（「🔌 リーダー: 切断」表示の説明直後）に `error_no_reader.png` を、(2) §9.5 冒頭の警告バナー説明の直後に `warning_network_disconnected.png` を、それぞれ `{width=70%}` 指定で追加。「未登録のカードです / 未登録の職員証です」については専用エラーダイアログが現状未実装で `CardTypeSelectionDialog`（既存 `card_type_selection.png`）と同一画面のため流用方針を維持。撮影手順は `tools/TakeScreenshots.ps1` の Issue #1411 エントリ（行 422〜435）に既に整備済みで、画像本体はユーザー側で別コミットで追加（Issue #1410 と同じ運用）。本コミットはマニュアル本文と CHANGELOG の参照追加のみ（#1411）
- ユーザーマニュアル §3.3「カード一覧の見方」に状態混在のスクリーンショット `card_list_status_mixed.png` を追加。従来は「利用可（緑）／貸出中（オレンジ）／残額警告（赤背景）」の色分けをテキスト表で説明していたが、3 状態が同時に表示された実画面が無く、メイン画面 `main.png` 内では一覧領域が小さく色の対比が読み取りにくかった。「状態の色分け」表の直下に拡大スクリーンショットと図キャプション（▲ 利用可・貸出中・残額警告が同時に表示された例）を挿入し、`AccessibilityStyles.xaml` のブラシ（`LendingBackgroundBrush` / `ReturnBackgroundBrush` / `ErrorBackgroundBrush` 等）が DataGrid 行に適用された結果を視覚的に確認できるようにした。あわせて alt テキストと図キャプションで「色だけでなくアイコン・テキストでも判別可能」である旨を明記し、Issue #1274 の色覚多様性対応原則（色＋アイコン＋テキスト＋音の 4 要素）との一貫性を補強。撮影手順は `tools/TakeScreenshots.ps1` の Issue #1412 エントリ（行 437〜441）に既に整備済み（chore commit 2bb1bb2 で先行追加）。Issue 本文の追加候補のうち「並び替えメニュー展開時の見え方」（`card_list_sort_menu.png`）は §3.3.5 の機能説明で代替可能なため本コミットではスコープ外（#1412）
- 管理者マニュアル §4「職員管理」のダイアログスクリーンショットを追加。従来は §4.1 で職員管理画面（`staff.png`）1 枚のみで、§4.2「職員の登録」と §4.3「職員情報の編集」で開かれる `StaffManageDialog` の実画面が無く、新任管理者が初めて職員登録を行う際の入力項目の見え方を事前に確認できなかった（研修資料としても活用しにくい状態）。(1) §4.2「職員の登録」の手順 1 直下に `staff_register_before_touch.png`（職員証タッチ前: 右側フォームに「職員証をタッチしてください」プロンプト表示）と `staff_register_after_touch.png`（職員証タッチ後: 右側フォームに IDm 自動入力 + 「職員証を読み取りました」表示）を **上下スタック配置 / `width=80%`** で 2 段に並置、(2) §4.3「職員情報の編集」の手順 2 直下に編集ダイアログ（`staff_edit_dialog.png`）を `width=60%` で配置。配置形式は当初 §4.2 を 2 列テーブル（各 `width=100%`）としていたが、`StaffManageDialog` は **左列 = 職員一覧（`*` 可変）/ 右列 = 編集フォーム（`320px` 固定）** の 2 列レイアウトで、テーブル内に並べると各セル幅が約 50% に押し込まれて右側の編集フォームが切れて読めなくなるため、上下スタック方式へ変更。§4.5「職員証の再登録」へのスクリーンショット追加は本コミットでは見送り（理由: 「職員証をタッチしてください」プロンプト表示 Border は XAML L152〜170 で `Visibility="{Binding IsNewStaff, Converter=...}"` により **新規登録時（IsNewStaff=true）のみ表示** されるため、編集モードの再登録フローでは `staff_edit_dialog.png` がプロンプト無しの状態となり §4.5 本文の「編集モードに入ると『職員証をタッチしてください』と表示されます」と画像が乖離する）。§4.5 本文の記述自体が実装と一致しないことは別 Issue #1437 で扱う。撮影手順は `tools/TakeScreenshots.ps1` の Issue #1414 エントリ（行 455〜473）に整備済み（chore commit 2bb1bb2 で先行追加）。画像本体はユーザー側で別コミットで追加（Issue #1411 / #1412 と同じ運用）。本コミットはマニュアル本文・撮影スクリプト指示・CHANGELOG のみ（#1414）
- 管理者マニュアル §4.5「職員証の再登録」の手順 2 から、実装と一致しない記述「（編集モードに入ると「職員証をタッチしてください」と表示されます）」を削除し、実際の挙動に整合した説明へ差し替え。`StaffManageDialog.xaml` L152〜170 の「職員証読み取り待ち表示」Border は `Visibility="{Binding IsNewStaff, Converter=...}"` により **新規登録時のみ表示** される設計（コメントにも「（新規登録時のみ）」と明記）であり、編集モード（再登録フロー）では同プロンプトは表示されない。本文と実装が乖離していると、利用者は「マニュアルに書かれている表示が出ない＝動作不良」と誤判断するおそれがあった。Issue #1414（管理者マニュアル §4 スクリーンショット追加）作業中に発覚し、当該 Issue の §4.5 への画像追加見送りとあわせて別 Issue #1437 に切り出して対処。方針 A（マニュアル本文を実装に合わせて修正）を採用し、(a) 手順 2 を「『編集』をクリック（右側の編集フォームが入力可能になります）」に変更、(b) 手順 3 に「IDm欄が新しい値に更新されます」と明示することで、再登録時にユーザーが何を視覚的フィードバックとして確認すべきか（タッチ待ちプロンプトではなく IDm 欄の値変化）を明確化した。実装側変更（方針 B: 編集モードでもプロンプト表示）は採用せず、XAML L152 のコメント「（新規登録時のみ）」が示す既存設計意図を維持。本コミットはマニュアル本文（4 行）と CHANGELOG のみで、コード・テスト変更なし（#1437）
- 管理者マニュアル §5.3「交通系ICカード情報の編集」/ §5.5「交通系ICカードの払い戻し」のダイアログスクリーンショット参照を追加。従来は §5.1（カード管理画面 = `card.png`）と §5.2（カード登録方法選択 = `card_registration_mode.png`）にしか画像がなく、§5.3 の「編集」ボタン押下後の `CardManageDialog` 右ペイン編集モード、および §5.5 の「払い戻し」ボタン押下後の確認 MessageBox（残高表示・「払戻済」状態への遷移注意・黄色三角警告アイコン付き Yes/No）の実画面が無かった。特に §5.5 払い戻しは残高を払出として記録しカードを「払戻済」状態に遷移させる不可逆操作のため、実行前の確認 UI を視覚的に提示することは管理者の誤操作防止に直結する。(1) §5.3 step 4 直下に `card_edit_dialog.png` を `{width=80%}` で、(2) §5.5 step 3 直下・「重要」quote の前に `card_refund_dialog.png` を `{width=70%}` で挿入。撮影手順は `tools/TakeScreenshots.ps1` の Issue #1415 エントリ（行 474〜486、chore commit 2bb1bb2 で先行追加）に整備済み。`card_edit_dialog` エントリ Instructions は本コミットで「右側の編集フォームに種別／管理番号／備考の編集欄が表示された状態（CardManageDialog の右ペイン編集モード）」と精緻化し、PR #1436 で `staff_edit_dialog` Instructions に施した「右側の編集フォーム」明記と一貫させた。`card_refund_dialog` エントリ Instructions（行 484「論理削除警告」表記）と §5.5 本文の「カードは論理削除されます」表記は実装乖離（実装は Issue #530 の「払戻済」状態 = `IsRefunded` フラグ、`IsDeleted` の論理削除とは別概念）のため本コミットでは触れず、別 Issue #1439 で対応予定。画像本体はユーザー側で別コミットで追加（Issue #1411 / #1412 / #1414 と同じ運用）。本コミットはマニュアル本文・撮影スクリプト指示・CHANGELOG のみ（#1415）
- 管理者マニュアル §5.6.5「月途中からの履歴入力（CSVインポート）」/ §6.4「データインポート」に CSV インポートのプレビュー画面のスクリーンショット参照を追加。両節とも、プレビュー機能を「追加（緑）/修正（オレンジ）/スキップ（灰）/復元（青）」の色分けアクション付きの強力な機能として説明していたにもかかわらず、画面そのものの実画像が無く、初期導入時の CSV 取り込み（§5.6.5）における変更内容確認・失敗のリカバリ判断が文章だけでは難しい状態だった。実装側のプレビュー UI は別ダイアログを開く構造ではなく `Views/Dialogs/DataExportImportDialog.xaml` の `Grid.Row="4"` に**インライン展開**される（XAML L39 / L423〜489、`HasPreview` バインディングで可視化制御）ため、1 枚の `import_preview.png` でデータ種別選択 + プレビュー結果 DataGrid + 「プレビュー」「インポート実行」「直接インポート」ボタンが同時に映り、Issue 概要の「プレビューダイアログの全体スクリーンショット」と「『インポート実行』『キャンセル』ボタンの位置関係がわかる構図」要件を 1 枚で満たせる。(1) §5.6.5 step 3 末尾（「変更点プレビューが表示されます」直下）と (2) §6.4 step 4 直下に同一の `import_preview.png` を `{width=85%}` で 2 箇所参照する構成を採用。§6.4 は §5.6.5 への参照リンクで詳細委譲する文書構造のため、別構図の追加撮影は YAGNI と判断し 1 枚共通参照とした。画像幅 85% は Issue #1415 の `card_edit_dialog`（80%）よりわずかに広めだが、プレビュー DataGrid の列数（日時 / IDm / 管理番号 / 摘要 / 金額 / 利用者 / 備考 / アクション 等）と色分け列の判読性を確保するための合理的調整で、§5.6.5 / §6.4 で同一幅とすることで章をまたいだ一貫性も確保。撮影手順は `tools/TakeScreenshots.ps1` の `import_preview` エントリ（行 330、PR #1427 で先行追加）に既に整備済みで、行 487 にも `# Issue #1416: CSV インポートプレビューは既存 import_preview.png を流用するため新規エントリなし` と流用宣言コメントが置かれているため本コミットでは ps1 を変更しない。画像本体はユーザー側で別コミットで追加（Issue #1411 / #1412 / #1414 / #1415 と同じ運用）。本コミットはマニュアル本文と CHANGELOG のみで、コード・テスト・撮影スクリプト変更なし（#1416）
- 管理者マニュアル §6.1「手動バックアップ」/ §6.2「リストア（復元）」にバックアップ完了ステータス・リストア用バックアップ一覧・ファイル選択ダイアログのスクリーンショット参照を追加。従来は §6.1 冒頭の `system.png`（システム管理画面全体）1 枚のみで、(a) 「バックアップを作成」ボタン押下後の完了通知の見え方、(b) リストア時に表示されるバックアップファイル一覧、(c) 「ファイルを指定してリストア」のファイル選択ダイアログという、リストアという**他のすべての PC でアプリ終了を要する不可逆操作**の事前確認画像が欠けていた。Issue 概要は「完了通知**ダイアログ**」を要求していたが、`SystemManageViewModel.CreateBackupAsync` は `SetStatus($"バックアップを作成しました: {ファイル名}", false)` でシステム管理画面下部のメッセージバナーに完了メッセージを表示する実装で、専用ダイアログは存在しない。当初は文書を実装に整合させる方針 (A 案) で進めたが、レビュー段階で**「完了メッセージが直後の `LoadBackupsAsync` で件数表示に上書きされて UI に映らない」実装バグ**が発覚 (本 Unreleased の「バグ修正」セクションで併せて修正) したため、文書 + 画像 + 実装の 3 点を整合させる方針 (α) に切り替えた。実装バグ修正後は ps1 撮影スクリプトの Instructions「ステータスバーに『バックアップを作成しました: <ファイル名>』と表示されたら」が**実際に再現可能**となる。マニュアル側は §6.1 step 3 の文言を「バックアップが設定済みの保存先に作成されます」→「バックアップが設定済みの保存先に作成され、ステータスバーに『バックアップを作成しました: <ファイル名>』と表示されます」に書き換えた上で `backup_completed_status.png` を `{width=50%}` で参照（既存 `system.png` と同幅で章内一貫性を維持）。リストア節は文言修正なしで step 2 直下に `restore_list.png` を `{width=50%}`、「ファイルを指定してリストア」段落直後に `restore_file_dialog.png` を `{width=60%}`（OS 標準ファイル選択ダイアログは横長で左ペイン+ファイルリスト+パス欄の可視性のため僅かに広く）で挿入。撮影手順は `tools/TakeScreenshots.ps1` 行 488〜506 のエントリ (PR #1427 で先行追加) を流用。画像本体はユーザー側で別コミットで追加（Issue #1411 / #1412 / #1414 / #1415 / #1416 と同じ運用）（#1417）
- 管理者マニュアル §2.6「アンインストールについて」に、InnoSetup の「データの削除」3 択ダイアログ（タイトル: 「データの削除」/ ボタン: すべて削除・データのみ残す・何も削除しない）のスクリーンショット参照（`uninstall_data_choice.png`、`{width=70%}`）と、各選択肢で削除/保持される対象を `C:\ProgramData\ICCardManager` 配下のフォルダ単位で示す詳細表を新設追加。従来は 3 択の動作概要表のみで、「すべて削除」を誤選択した場合の影響範囲（ユーザーデータ・バックアップ・ログがすべて消失して履歴復元不能）を視覚的に確認できる画面が無く、Issue 概要が指摘する「データ消失リスクのある重要操作の事前確認」要件を満たせていなかった。実装側の `installer/ICCardManager.iss` `CurUninstallStepChanged`（`usPostUninstall` 段階、L533〜607）の挙動は (a) `IDYES`=`DelTree(AppDataPath)` で `\ICCardManager\` 配下を全削除、(b) `IDNO`=`\backup\` と `\Logs\` のみ `DelTree` し DB・設定ファイルは保持、(c) `IDCANCEL`=何も削除しない、で確定しており、新設の詳細表は実装と 1 対 1 対応する形で「対象パス × 3 選択肢」の 3×3 マトリクスとして整理（既存の「動作概要」表は読みやすさ維持のため据え置き、相補的に併記）。あわせて (1) ダイアログ表示条件補足として「ユーザーデータが存在しないクリーン環境ではダイアログは表示されず、プログラムのみがそのまま削除される」（実装 L555〜557 の `if not UserDataExists then Exit` に対応）、(2) 既存「注意」quote に「誤って『すべて削除』を選んだ場合でも、別の PC や共有フォルダにバックアップが残っていれば §6.2 の手順で復元できます」と復旧経路（§6.2 リストアへの内部リンク）を追記、を実施し、誤操作後のリカバリ動線も明示した。撮影手順は `tools/TakeScreenshots.ps1` の Issue #1413 エントリ（`uninstall_data_choice.png`、`ManualOnly = $true`）に既に整備済み（chore commit 2bb1bb2 で先行追加）。InnoSetup のアンインストーラーは Setup/Uninstall 用の別プロセス（`Uninstall_ICCardManager.exe`）で WPF 本体外のため自動撮影不可、PrtSc 等での手動取得運用となる点も `installer_options.png` / `installer_database_path.png` と同じ。画像本体はユーザー側で別コミットで追加（Issue #1411 / #1412 / #1414 / #1415 / #1416 / #1417 と同じ運用）。本コミットはマニュアル本文と CHANGELOG のみで、コード・テスト・撮影スクリプト変更なし（#1413）
- ユーザー向けマニュアル群の冒頭バージョン記載を実装の最新版（v2.7.0 / 2026年4月）に同期。`docs/manual/はじめに.md` の `バージョン: 1.0 / 最終更新日: 2026年2月` がリリース v2.7.0 まで更新されず、同フォルダの `ユーザーマニュアル.md` / `管理者マニュアル.md` / `開発者ガイド.md` 冒頭の v2.7.0 表記と乖離していた。あわせて、従来バージョン記載自体が無かった `ユーザーマニュアル概要版.md` と `かんたん導入ガイド.md` のタイトル直下にも `**バージョン**: 2.7.0 / **最終更新日**: 2026年4月` を追加し、配布マニュアル 6 本すべての冒頭メタ情報フォーマットを統一。実装とドキュメントの版数同期監査で発見した既存乖離（実装 v2.7.0 / マニュアル 3 本のみ未同期）への一括対応で、コード・テスト変更なし
- テスト設計書 §1.1a「テスト規模（現状）」の統計を実測値に同期。記載は単体 2,500 件 / UI 15 件 / 合計 2,509 件のままだったが、実測（`tests/ICCardManager.Tests/` および `tests/ICCardManager.UITests/` 配下の `[Fact]` / `[Theory]` メソッド数の `grep` カウント）は単体 2,579 件 / UI 19 件 / 合計 2,598 件で、設計書の記載と +89 件の乖離があった。Issue #1263 のダイアログ追加 6 件以降に蓄積されたアクセシビリティ・新機能対応のテスト増分（Issue #1273〜#1372 等）が表に反映されないまま放置されていた状態を解消。数値表 3 セルと UI テスト備考コメント（「Issue #1263 でダイアログ追加6件」→「Issue #1263 でダイアログ追加6件（その後アクセシビリティ・新機能対応で増加）」）を更新。実装とドキュメントの同期監査で発見した既存乖離への対応で、コード・テスト変更なし
- 管理者マニュアル §2.2「インストーラーを使用する場合」に、PR #1235 で導入済みだった「帳票出力先」インストーラーページの説明が欠落していた問題を解消。インストール手順リスト（手順 7 として新規追加）と、データベース保存先サブセクション直下に新設した「帳票出力先フォルダ」サブセクション（スクリーンショット `installer_report_output.png` の参照、デフォルト値の挙動説明、書き込み権限の必要性の重要注記、変更動線の補足の 4 要素を網羅）の 2 か所を追記。デフォルト値の挙動は実装（`installer/ICCardManager.iss` L342〜346）に整合させ、(a) 新規インストール時はマイドキュメント（`{userdocs}` 展開）が初期表示、(b) アップグレード／再インストール時は既存 `C:\ProgramData\ICCardManager\report_output_config.txt` を読み込んで現在値を初期表示、と明示。変更動線は実装（`ReportViewModel.cs` L160〜166 の `OutputFolder` setter で `SettingsViewModel.SaveReportOutputConfigToFile` を同期更新）に整合させ「帳票作成画面（メイン画面の『帳票』ボタンまたは F1 キー）の『出力先フォルダ』欄で変更可能、変更すると `report_output_config.txt` も自動更新されて次回インストーラーのデフォルト値にも反映される」旨と、誤誘導を防ぐため「『設定』画面（F5）には帳票出力先の項目はない」を併記（Issue 概要は F5 設定画面からの変更を求めていたが、実装上 F5 設定画面側には UI バインディングが無く帳票作成画面が唯一の変更動線のため）。撮影スクリプト `tools/TakeScreenshots.ps1` には `installer_report_output.png` の手動撮影エントリを `installer_options.png` の直後に追加（Inno Setup ベースのインストーラーは WPF 本体外のため `ManualOnly` 運用、`installer_options.png` / `installer_database_path.png` と同じ方針）。画像本体はユーザー側で別コミットで追加（Issue #1411 / #1413 / #1414 / #1415 / #1416 / #1417 と同じ運用）。本コミットはマニュアル本文・撮影スクリプト指示・CHANGELOG のみで、コード・テスト変更なし（#1247）

### v2.7.0 (2026-04-15)

**新機能**
- インストーラーが既存設定をデフォルト表示＆帳票出力先ページ追加（#1235）
- Value ObjectとModel層Rich化によるドメイン知識の型安全な表現（#1221）

**バグ修正**
- CsvImportServiceのXMLドキュメントコメント警告を修正（#1232）

**リファクタリング**
- MainViewModelSyncDisplayTests のリフレクション依存を ISystemClock に移行 (#1228)（#1228）
- IntToVisibilityConverter の重複クラスを統合 (#1227)（#1227）
- インターフェース階層分離でCQRS風の責務分離（#1224）
- partial classで責務分割（3,311行→5ファイル）（#1223）
- サービス抽出でDbContext直接依存を排除し責務を分離（#1222）

**ドキュメント**
- 技術スタック用語集を新設し初学者向けに解説を充実（#1231）

**テスト**
- テストカバレッジ補強(DashboardService等3サービス + 既存テスト追加)（#1226）


### v2.6.1 (2026-04-10)

**バグ修正**
- 年度途中繰越ledgerの受入欄が空欄となるように修正（#1219）


### v2.6.0 (2026-04-09)

**新機能**
- 年度途中導入でも累計受入・払出を適切に設定 (#1215)（#1215）

**バグ修正**
- 持ち替え時に新しい職員名で認識トーストを再表示 (#1211)（#1211）
- ICカード待ち中に別の職員証で上書きできるよう修正 (#1211)（#1211）
- GetConnection() を LeaseConnection() に移行 (#1209)（#1209）

**ドキュメント**
- 設計書全体を見直してわかりやすさを向上 (#1214)（#1214）


### v2.5.0 (2026-04-08)

**新機能**
- 既存値が★のみの場合は空欄として初期化 (#1205)（#1205）
- バス停名入力のUX改善（直近利用優先・類似検出）（#1143）

**バグ修正**
- 返却時の複数バス利用を1つのダイアログでまとめて入力 (#1203)（#1203）
- GetStartingPageNumberForMonth の L2 空シートスキップを明示化 (#1197)（#1197）
- RepairLentStatusConsistencyAsync の暗黙契約を解消 (#1196)（#1196）
- LogLedgerDeleteAsync の operatorIdm を nullable に統一 (#1188)（#1188）
- DbContextの並行アクセスをConnectionLeaseパターンで保護 (#1165)（#1165）
- FileLoggerProviderのログキュー溢れによるサイレントドロップを修正 (#1173)（#1173）
- ジャーナルモード設定失敗時のUI警告を追加 (#1172)（#1172）
- CardLockManagerのクリーンアップ競合によるObjectDisposedExceptionを修正 (#1171)（#1171）
- CleanupOldDataのトランザクション欠如によるテーブル間不整合を修正 (#1170)（#1170）
- ReadHistoryAsyncの例外飲み込みによりリーダーエラーと履歴ゼロ件が区別不能な問題を修正 (#1169)（#1169）
- ICカードリーダー再接続10回上限後の永続停止を低頻度リトライで回復可能に修正 (#1168)（#1168）
- 共有モードでのキャッシュ競合と古いデータによる二重貸出リスクを修正 (#1167)（#1167）
- リストア中のバックグラウンド接続再オープンによるDBファイル消失リスクを修正 (#1166)（#1166）
- 返却時に孤立した貸出中レコードが残る問題を修正（#1164）

**リファクタリング**
- ParseHistoryData を FelicaHistoryBlockDecoder への委譲に統一 (#1198)（#1198）
- テーブル行データ構築を純粋関数に抽出（#1194）
- ページネーション計算メソッドを internal static に変更（#1192）
- 履歴ブロック解析を純粋関数に抽出（#1185）

**ドキュメント**
- 設計書・マニュアルと実装の整合性を修正（#1183）

**テスト**
- BuildPrintTableRows の単体テストを追加（#1195）
- ページネーション計算の単体テストを追加（#1193）
- ページ番号計算メソッドの直接単体テストを追加（#1191）
- RepairLentStatusConsistencyAsync の境界条件を追加（#1190）
- 全メソッドのカバレッジ拡充（#1189）
- 全LogXxxAsyncメソッドのカバレッジ拡充（#1187）
- 履歴ブロック解析の単体テストを追加（#1186）
- 残高不足パターン検出・チャージ境界分割等のテストを追加（#1184）


### v2.4.2 (2026-04-06)

**新機能**
- メイン画面の履歴表示に繰越行を追加 (#1155)（#1155）

**バグ修正**
- システム管理画面のバックアップ一覧が表示されない問題を修正 (#1153)（#1153）
- リストア完了ダイアログ表示時にプログレスバーが残る問題を修正 (#1154)（#1154）
- バス停名入力画面でスキップ時に入力済みの内容が反映される問題を修正 (#1156)（#1156）


### v2.4.1 (2026-04-03)

**バグ修正**
- 同一日に複数職員が同じカードを利用すると履歴が統合されるバグ（#1148）

**ドキュメント**
- バージョンアップ時の注意点を管理者マニュアルに追記（#1150）
- 借りた人と返した人が違う場合のFAQを追加（#1151）


### v2.4.0 (2026-04-02)

**新機能**
- 履歴編集ワークフローの簡略化（#1144）
- 残高警告メッセージにしきい値を表示する（#1142）

**テスト**
- Infrastructure層のテストカバレッジ拡充 (#1135)（#1135）


### v2.3.0 (2026-04-01)

**新機能**
- 共有モードでデータの最終同期経過時間を表示 (#1131)（#1131）

**バグ修正**
- 返却時のトースト通知で残額が0と表示される場合がある (#1139)（#1139）
- operation_logも6年経過後に自動削除する (#1123)（#1123）
- 近年開業した4路線13駅を駅マスターデータに追加 (#1120)（#1120）
- 七隈線の櫛田神社前駅（233-033）を駅マスターデータに追加 (#1120)（#1120）

**ドキュメント**
- ユーザーマニュアルにカードタッチ図を追加（#1137）
- TODO.mdを実際の開発状況に合わせて更新 (#1128)（#1128）


### v2.2.0 (2026-03-31)

**新機能**
- 共有モードのテストカバレッジ拡充とドキュメント整備 (#1111)（#1111）
- 共有モード時のUX改善（再接続・エラー表示・更新時刻）（#1116）

**バグ修正**
- レビュー指摘の修正 - テストアサーション・ドキュメント精度改善 (#1111)（#1111）
- DeleteAsync/SetRefundedAsync の失敗原因をユーザーに通知（#1115）
- 共有モードでのリストア操作の安全性を確保 (#1108)（#1108）
- 共有モードのSQLite耐障害性を改善 (#1107)（#1107）
- カード番号採番の競合状態を防止するUNIQUE制約を追加 (#1106)（#1106）
- 日本語を含む共有フォルダパスで起動エラーになる問題を修正（#1104）


### v2.1.3 (2026-03-30)

**ドキュメント**
- 管理者マニュアルの共有フォルダ設定セクションを再構成 (#1100)（#1100）
- 管理者マニュアルからPaSoRiドライバインストール手順を削除 (#1098)（#1098）
- 管理者マニュアルの共有フォルダパス記載例を修正 (#1096)（#1096）


### v2.1.2 (2026-03-29)

**バグ修正**
- 共有モード時にDB権限設定をスキップ（#1093）
- 共有モード時にダッシュボードと貸出中カードを定期リフレッシュ


### v2.1.1 (2026-03-28)

**バグ修正**
- インストーラーDB保存先ページの文字切れを修正（#1091）


### v2.1.0 (2026-03-28)

**バグ修正**
- DB保存先設定の書き込み権限エラーを修正・インストーラーに保存先選択を追加（#1089）

**ドキュメント**
- かんたん導入ガイドを追加（#1088）


### v2.0.0 (2026-03-28)

**新機能**
- 共有フォルダDB方式による複数PC同時利用機能を追加（#1086）


### v1.27.4 (2026-03-26)

**バグ修正**
- インストーラーでProgramDataディレクトリにUsers権限を設定（#1084）


### v1.27.3 (2026-03-26)

**バグ修正**
- ProgramData配下のディレクトリ権限設定とログローテーションの耐障害性を改善（#1082）
- 部署設定ファイルの削除失敗時に内容クリアで代替する (#1080)（#1080）
- DBファイルのアクセス権限をUsersグループに拡大（#1079）
- テストデータの残高整合性を保証する (#1075)（#1075）
- DEBUGビルド時のテストデータ生成を単一トランザクションで高速化 (#1074)（#1074）


### v1.27.2 (2026-03-25)

**バグ修正**
- 月計・累計行の金額列に「縮小して全体を表示する」を設定 (#1071)（#1071）


### v1.27.1 (2026-03-24)

**バグ修正**
- データインポート後のカード一覧の最終利用日が正しく表示されない問題を修正 (#1068)（#1068）
- WSL経由のgh pr createでPR本文がbashに解釈される問題を修正（#1067）


### v1.27.0 (2026-03-24)

**新機能**
- 詳細レベルの残高チェーン整合性チェックを追加 (#1059)（#1059）
- 残高不整合警告クリック時に該当行をハイライト表示 (#1052)（#1052）

**バグ修正**
- データエクスポート/インポート画面のプレビュー領域スクロールを修正 (#1063)（#1063）
- データインポート/エクスポート時のプログレスバー表示タイミングを修正 (#1062)（#1062）
- インポート後に全カード対象の残高整合性チェックを実行するよう修正 (#1058)（#1058）
- 履歴詳細インポート時のチャージ/ポイント還元分割と受入金額を修正 (#1053)（#1053）

**リファクタリング**
- DailySegmentをLendingHistoryAnalyzerに移動し循環参照を解消 (#1047)（#1047）

**ドキュメント**
- サードパーティライブラリのライセンス一覧を作成 (#1054)（#1054）


### v1.26.0 (2026-03-23)

**新機能**
- メンテナンス性・テスト・UXの品質向上（10項目）

**バグ修正**
- 徹底レビューで発見された問題の修正
- コードレビュー指摘事項の修正
- 帳票作成時のプログレスバーが表示されない問題を修正
- プログレスバーの視認性改善とUIスレッド問題修正

**リファクタリング**
- StationMasterServiceの静的シングルトンを完全排除（#1046）
- MainViewModelテストのTask.Delay(100)を決定論的待機に置換（#1045）


### v1.25.1 (2026-03-20)

**バグ修正**
- Ledger.Summaryがnullの場合に発生する潜在的NullReferenceExceptionを修正（#1042）
  - LedgerOrderHelper、MainViewModel、IncompleteBusStopViewModel、PrintService、IncompleteBusStopDialogの5箇所

**テスト**
- テストカバレッジギャップを検出するエッジケーステスト101件を追加（#1041）

### v1.25.0 (2026-03-19)

**新機能**
- リリース自動化スクリプトの追加（#1038）


### v1.24.0 (2026-03-19)

**新機能**
- 操作ログExcelエクスポートで変更部分をハイライト表示（#1027）
- フォルダパスの直接入力を可能に（#1026）

**バグ修正**
- バックアップファイルの更新日時をバックアップ実行時の現在時刻に設定（#1028）
- データエクスポート/インポート画面の二重スクロールバーを解消（#1021）
- 帳票出力画面の出力先フォルダを永続化（#1029）

**リファクタリング**
- 表示フォーマット・ソート・年度計算のユーティリティ共通化（#1024）
- 帳票行変換パイプラインとRouteDisplayロジックの共通化（#1023）
- SummaryGeneratorの鉄道・バス共通パイプラインを抽出（#1022）

### v1.23.0 (2026-03-18)

**新機能**
- バックアップパスのUNCパス対応（#1018）
  - `\\server\share\backup` のようなネットワーク共有パスをバックアップ先に指定可能に

**バグ修正**
- バス停名入力ダイアログを閉じた後に履歴画面が即時反映されない問題を修正（#1010）
- バス利用の摘要順序を時系列順に修正（#1012）
- 操作ログ・統合履歴のタイムスタンプをローカル時刻で保存するよう修正（#1014）
  - MigrationRunnerのoperation_logタイムスタンプがUTCで記録されていた問題を修正
  - 履歴統合時のタイムスタンプがUTCで記録されていた問題を修正

**テスト・ドキュメント**
- テスト設計書の修正（#1017）
  - TC004の期待結果を残高チェーンによる時系列判定に修正
  - 不要テスト3件（TC034/TC035/TC037）を削除
  - 千早/西鉄千早の乗り継ぎテスト（TC049）を追加
  - 暗黙還元・明示還元の用語定義をFeliCa生データとの違いを含めて追加
  - UT-005/UT-018の入力データ・金額根拠を明記

### v1.22.3 (2026-03-17)

**バグ修正**
- CSVエクスポート時の同一日内の並び順を残高チェーン順に修正（#1004）
  - ポイント還元と利用が同日にある場合、ID順ではなく残高チェーン順で出力
  - LedgerDetailChronologicalSorterのポイント還元対応を修正
  - LedgerConsistencyCheckerも残高チェーン順で整合性チェックするよう修正
  - CalculateGroupFinancials・GetGroupDateのカスタムソートを残高チェーン順に統一

**ドキュメント**
- 機能設計書・クラス設計書を残高チェーン順ソートの実装に合わせて更新（#1007）
- テスト設計書を実コード（1,796件）に基づいて大幅に詳細化（#1008）

### v1.22.2 (2026-03-16)

**バグ修正**
- 通常チャージが残高不足パターンとして誤検出される問題を修正（#1001）
  - 利用後残高の閾値チェックを追加して誤検出をさらに防止

**コード整理**
- 使われていないコードを削除（#998）

**テスト**
- WPFコンバーターとDTO表示プロパティの単体テストを追加（#1000）

**ドキュメント**
- 実装済みだが未ドキュメントの機能をドキュメントに反映（#999）

### v1.22.1 (2026-03-16)

**バグ修正**
- バス停名入力画面で複数履歴がある場合にTABキーで次の入力欄にフォーカスが移動しない問題を修正（#991）
- 利用履歴画面で列幅を広げても横スクロールバーが表示されない問題を修正（#993）

**コード整理**
- 使われていない旧HistoryDialog（別ウィンドウ方式）を削除（#995）

### v1.22.0 (2026-03-16)

**新機能**
- バスの往復利用を検出して「バス（A～B 往復）」と表示（#985）
- バスの乗り継ぎ（連続区間）を統合して「バス（A～C）」と表示（#985）
- 組織固有のハードコード値（摘要テキスト・往復/乗継ルール・エリア優先順位・帳票レイアウト等）をappsettings.jsonで設定可能に（#974）

**バグ修正**
- バス停名を編集後にレコードを統合すると修正前の摘要に戻る問題を修正（#983）

**ドキュメント**
- 管理者マニュアルにセクション7.4「組織固有設定（OrganizationOptions）」を追加
- クラス設計書にOrganizationOptionsクラス図を追加

### v1.21.0 (2026-03-13)

**新機能**
- 返却時のバス停名入力ダイアログを自動スキップする設定を追加（#975）
- 物品出納簿の備考欄フォントサイズを文字列長に応じて自動調整（#980）

**バグ修正**
- 残高不足パターン（不足分を現金でチャージした場合）の処理を修正（#978）
  - チャージ詳細をDBに保存して重複チェック漏れを防止
  - 端数チャージ（10円単位）にも対応するよう検出条件を緩和
  - 会計処理を修正（払出額=運賃-チャージ額、不足額=チャージ額、残額=利用後の実残高）

### v1.20.0 (2026-03-12)

**新機能**
- インポートプレビュー画面で追加・スキップ行もクリックで詳細データを表示（#969）
- インストーラーに「はじめに」マニュアルを追加（#967）

**改善**
- 音声（男性・女性）をより元気よい声に再生成（#971）
  - 話速・ピッチ・抑揚パラメータを調整
  - 音声生成スクリプトにパラメータ指定機能を追加

### v1.19.2 (2026-03-11)

**バグ修正**
- 利用履歴詳細CSVエクスポート時に一部の順序が逆になる問題を残高チェーンベースのソートで修正（#964）

### v1.19.1 (2026-03-11)

**バグ修正**
- データエクスポート/インポート画面の初期サイズを調整し、スクロールなしで設定が表示されるよう修正（#961）

### v1.19.0 (2026-03-11)

**新機能**
- 履歴画面の表示期間テキストをクリックで月選択ポップアップを表示（#945）
- 物品出納簿の摘要欄フォントサイズを文字列長に応じて自動調整（#946）
- 物品出納簿の金額列（受入・払出・残額）のフォントサイズを16ptに変更（#947）
- カード登録画面で無効欄クリック時にモード自動切替（#944）
- データエクスポート/インポート画面のプレビュー結果とエラー詳細の高さをリサイズ可能に（#958）

**バグ修正**
- 暗黙のポイント還元が鉄道利用とまとめられる問題を修正（#942）
- データエクスポート/インポート画面で閉じるボタンが画面外にはみ出す問題を修正（#948）

**ドキュメント**
- カード登録方法選択画面のヒント欄に管理者マニュアルへの案内を追記（#943）
- 月途中からの導入時の履歴について詳細な説明を追加（#950）

### v1.18.2 (2026-03-10)

**機能改善**
- 利用履歴インポートのプレビューでカードIDmに加えてカード名（種別＋管理番号）を表示（#937）
- 利用履歴詳細インポートのプレビューでカード名を表示（#937）
- 利用履歴詳細インポートのプレビューで「追加」行の詳細内容を確認可能に（#938）

**バグ修正**
- テストコードのnullable警告（CS8625等）を修正（#935）
- UIテストをdotnet testのソリューション全体実行から除外（#935）
- UIテストのタイムアウトをdotnet run + Application.Attach方式で修正（#935）

### v1.18.1 (2026-03-09)

**メンテナンス**
- 不要・重複テストを削除してテストスイートを整理（#928）
- CIワークフローでUIテストプロジェクトを明示的に除外（#927）

### v1.18.0 (2026-03-08)

**新機能**
- デバッグ用データビューアにFeliCaブロック番号(Seq)列を追加（#922）
- デバッグ用データビューアのDBデータタブにSQLiteのrowidを表示（#924）

**バグ修正**
- 利用履歴詳細インポート時に異なる日付のデータが1つのLedgerに統合されていた問題を修正（#918）
- 利用履歴詳細インポート時に親Ledgerの金額が再計算されない問題を修正（#918）
- 履歴を分割後に再統合すると摘要の経路順序がおかしくなる問題を修正（#920）

**ドキュメント**
- 管理者マニュアル：交通系ICカード新規登録時の最古履歴の利用金額に関する注意書きを追記（#916）

### v1.17.0 (2026-03-06)

**新機能**
- 利用履歴詳細インポート時に利用履歴IDが空欄の場合、カードIDmからLedgerレコードを自動作成する機能を追加（#906）

**バグ修正**
- 利用履歴画面の受入・払出列から不要な太字を除去（#901）
- データインポート時に他プロセスがファイルを使用中でも読み込み可能に（#902）
- データインポート時「既存データはスキップする」チェックが無視されていた問題を修正（#903）
- 利用履歴詳細エクスポート時のSequenceNumber順序を修正し残高整合性を確保（#904）
- 利用履歴詳細インポートプレビューの列ヘッダーをデータ種別に応じて動的表示（#905）
- 利用履歴インポート時にCSV最初の行もDB上の直前残高と整合性チェックするよう改善（#907）

### v1.16.7 (2026-03-06)

**ドキュメント**
- マニュアルのスクリーンショットにサイズ指定を追加（#897）
- マニュアルの章区切りで自動改ページするようreference.docxを更新（#897）

### v1.16.6 (2026-03-06)

**バグ修正**
- 利用履歴詳細の挿入順をFeliCa互換に修正し表示順逆転を解消（#888）
- 返却時に履歴表示を即座に更新するよう修正（#889）
- 新規カード登録時のデフォルトカード種別をnimocaに修正（#890）

**リファクタリング**
- 残高チェーンソートで表示順をrowid非依存にし統合テストを追加（#892）

**ドキュメント**
- 設計書と実装の整合性を修正（#891）

### v1.16.5 (2026-03-05)

**バグ修正**
- 同一日にチャージが利用の間に挟まる場合の残高チェーン不整合を修正（#886）

### v1.16.4 (2026-03-04)

**バグ修正**
- 履歴分割後のrowid再採番による表示順逆転を修正（#880）
- SummaryGeneratorのSequenceNumberソート順をFeliCa互換に修正（#880）

### v1.16.3 (2026-03-04)

**バグ修正**
- 利用履歴詳細画面の表示順を時系列順（古い順）に修正（#876）
- 天神駅と西鉄福岡(天神)駅を同一駅として認識するよう修正（#878）
- 履歴分割時の残高計算でDBの時系列順と一致するソート順を使用するよう修正（#880）

**ドキュメント**
- 庁内掲示板プロモーション記事を追加
- .gitignoreにPythonの__pycache__/を追加

### v1.16.2 (2026-03-03)

**バグ修正**
- リリースワークフローでUIテストを除外し、exeパス解決を構成対応に修正（#873）

**ドキュメント**
- 庁内プロモーション用チラシ（A4縦PDF）を追加（#871）
- プロモーション動画生成スクリプトと動画を追加・改善（#863）
- CLAUDE.md の冗長・不正確な記述を整理（#870）

### v1.16.1 (2026-03-02)

**ドキュメント**
- マニュアルにスクリーンショットを追加（#849）

### v1.16.0 (2026-03-02)

**新機能**
- システム表示名に愛称「ピッすい」を追加（#864）

**バグ修正**
- 物品出納簿の最初のシートで摘要・氏名・備考欄のフォントサイズが小さい問題を修正（#858）
- 最初のワークシートの表示倍率を100%に統一（#858）
- 会社名を「Your Organization」から正しい値に修正（#857）
- StaffAuthDialogのタイムアウトとXAML初期表示を設定値に合わせる（#854）
- 愛称追加に伴いウィンドウのデフォルト幅・最小幅を拡大

**リファクタリング**
- ハードコードされた設定値をappsettings.jsonに外部化（#854）
- INavigationServiceを導入してダイアログ管理を統一（#853）
- 静的フラグをWeakReferenceMessengerに置換（#852）

**ドキュメント**
- テスト設計書の誤り修正と記載漏れ補完（#850）
- スクリーンショット取得スクリプトにIssue #849の6画面を追加

### v1.15.1 (2026-02-25)

**バグ修正**
- 同一日の履歴を複数回返却時に統合するよう修正（#837）
- 4月の物品出納簿で累計行を省略し月計行に残額を表示（#813）
- プレビューの月計・累計行で受入・払出が0の場合も表示（#842）

**リファクタリング**
- 物品出納簿のデータ準備ロジックをReportDataBuilderに統合（#841）

**テスト**
- 未テストのViewModel・例外・ロガーに単体テスト215件を追加（#845, #846）

**ドキュメント**
- クラス設計書・機能設計書・シーケンス図・テスト設計書を実装に合わせて更新（#847）

**メンテナンス**
- StationCode.csv統合・重複除去・駅名更新（#838）

### v1.15.0 (2026-02-24)

**新機能**
- 帳票作成画面の「先月」「今月」ボタンを選択中はハイライト表示する（#825）
- VOICEVOXで生成した貸出・返却の音声WAVファイルに置き換え（#830）
  - 女性音声: 四国めたん、男性音声: 玄野武宏
  - 設定ダイアログに音声クレジット表記を追加

**バグ修正**
- DataGridがフォントサイズ設定の影響を受けない問題を修正（#823）
- 職員証タッチ時に音声モードでもビープ音を再生するよう修正（#832）
- テストプロジェクトのnullable参照型警告を全て解消（#834）

**テスト**
- UIテストのメインウィンドウ名判定をStartWithに変更しUIA Name不安定性に対応（#831）

**ドキュメント**
- READMEの更新履歴をCHANGELOG.mdに分離（#826）

### v1.14.0 (2026-02-21)

**バグ修正**
- 新規登録時の繰越額が履歴逆算値で上書きされるバグを修正（#819）

**新機能**
- FlaUIによるWPF UIテスト自動化基盤を追加（起動テスト6件・ダイアログ遷移テスト3件）
- CIでUIテストを自動除外する設定を追加

### v1.13.2 (2026-02-20)

**改善**
- インストーラービルドスクリプト（.bat）にDebugDataViewerのクリーンビルドを追加（#816）

### v1.13.1 (2026-02-20)

**バグ修正**
- チャージ・新規購入・ポイント還元・払戻しの氏名欄を空欄にする（#807）
- 物品出納簿の頁番号が翌月以降で正しく連続しない問題を修正（#809）
- エクスポート後にインポート側のデータ種別が見えなくなる問題を修正（#805）
- 帳票作成ボタン押下時に前回のStatusMessageをクリアする（#812）
- 残高不整合の自動修正を削除し、警告表示のみに変更（#785）

### v1.13.0 (2026-02-19)

**新機能**
- 操作ログエクスポートをCSVからExcel形式に変更（#786）

**バグ修正**
- 同一日のチャージ・利用の表示順を残高チェーンで正しく決定（#784）
- メイン画面・履歴画面のDataGridにも残高チェーン再構築を適用（#784）
- 起動時にic_card.is_lentとledger.is_lent_recordの整合性を修復（#790）
- 操作ログの表示順を古い順に変更し、初期表示で最新ログを表示（#787）
- DEBUGテストデータの残高チェーン不整合を修正（#803）

**ドキュメント**
- 管理者マニュアルの職員証再登録手順を実装に合わせて修正（#783）
- 管理者マニュアルのカード登録設定テーブルに「繰越額」を追記（#782）
- 管理者マニュアルのインポート記載を実装と一致させる（#781）
- ユーザー・管理者マニュアルの設定画面に「部署設定」を追記（#780）
- 管理者マニュアルに「利用履歴詳細」のエクスポート/インポートを追記（#779）
- ユーザーマニュアルのCSVインポート記載に管理者マニュアルへの参照を追加（#788）

**その他**
- リリースビルド時にマニュアルへバージョンを自動注入する仕組みを追加

### v1.12.0 (2026-02-18)

**新機能**
- データ入出力に利用履歴詳細のエクスポート/インポートを追加（#751）
- 削除ボタンを履歴一覧から編集ダイアログへ移動（#750）
- 部署選択をアプリ初回起動時からインストーラーに移動（#742）

**バグ修正**
- チェック済み行の背景色を水色でハイライト表示（#772）
- AlternatingRowBackground/DataGrid選択ハイライトの優先度問題を修正（#772）
- 利用履歴詳細インポートで変更なしデータをスキップ表示に修正（#751）
- ハードコードされた色コードをDynamicResourceに統一（#721）
- カード登録時の繰越額をユーザーが手入力できるように変更（#756）
- 物品出納簿のドキュメントプロパティ日時を出力時に更新（#752）
- インポート時の残高整合性チェックでスキップ行が除外される問題を修正（#754）
- インポート後に履歴一覧・ダッシュボードを即座に更新（#744）
- 履歴一覧の受入金額を緑色から黒色に変更（#749）
- 月次繰越行の受入金額欄を空欄に修正（#753）

**アクセシビリティ**
- Popupコントロール・DatePickerのアクセシビリティを改善（#720）
- ハードコードされたフォントサイズをDynamicResourceに統一（#719）

**ドキュメント修正**
- ユーザーマニュアルに職員証認識後の画面スクリーンショットを追加（#747）
- 物品出納簿のExcel出力スクリーンショットをユーザーマニュアルに追加（#746）
- スクリーンショットREADMEを現状の18枚構成に更新（#776）
- ユーザーマニュアルに「別々の履歴に分割」と「摘要のみ更新」の説明を追記（#755）
- 管理者マニュアルのCSVインポート手順から実画面にない記述を修正（#757）

**その他**
- 企業会計部局テンプレートから記載例ワークシートを削除（#745）

### v1.11.1 (2026-02-17)

**バグ修正**
- 起動時の CS4014 警告メッセージを修正（#736）

**コード品質改善**
- 使われていないメソッド12個を削除（#733）

### v1.11.0 (2026-02-17)

**新機能**
- 市長事務部局／企業会計部局の選択機能を追加（#659）
  - チャージ時の摘要を部署種別に応じて自動切替（役務費／旅費）
  - 帳票テンプレートを部署種別に応じて自動切替
  - 初回起動時に部署選択ダイアログを表示
  - 設定画面から部署種別を変更可能

**バグ修正**
- Releaseビルドで起動エラー時のスタックトレースを非表示にする（#716）
- Su-001のライフサイクル整合性とデポジット除外を修正（#731）

**パフォーマンス改善**
- .Result呼び出しをasync/awaitに置換してデッドロックリスクを解消（#717）

**アクセシビリティ**
- VirtualCardDialogにAutomationPropertiesを追加（#718）

**ドキュメント修正**
- スクリーンショット16枚を追加・更新しマニュアルの画像参照を修正（#729）
- 画面設計書のダイアログ名修正と不足ダイアログ追加（#714）
- システム概要設計書のOS要件・CPU要件を実装に合わせて修正（#713）

### v1.10.0 (2026-02-16)

**新機能**
- 職員一覧・カード一覧で新規登録・更新・復元後に該当行をハイライト表示（#707）
- バス停名入力後にハイライト表示してから一覧削除するよう改善（#709）

### v1.9.0 (2026-02-15)

**新機能**
- 履歴画面に行の挿入・削除・修正機能を追加（#635）
- システム警告をクリック可能にし対象カード履歴へ遷移（#672）
- デバッグモードで職員証認証ダイアログに仮想タッチボタンを追加（#688）

**バグ修正**
- 貸出時にカード残高が0になる問題を修正（#656）
- バス停名入力後・履歴変更後に警告メッセージの件数を更新する（#660）
- 設定の残額警告閾値変更後にシステム警告メッセージを更新する（#661）
- カード一覧のデフォルトソートを「カード名順」に変更（#662）
- デフォルトのウィンドウサイズを拡大しボタンが一行に収まるよう修正（#663）
- 繰越登録時の履歴不完全メッセージに実際の最古月を表示（#664）
- カード管理画面の新規登録時に履歴を事前読み取りする（#665）
- 履歴画面の払出金額の文字色を赤から黒に変更（#671）
- XMLドキュメントの不足paramタグを追加しCS1573警告を解消（#701）
- バス停名未入力一覧に利用日フィルタを追加し、フィルタ条件を維持するよう改善（#703）
- 「変更」と「修正」ボタンを統合し一本化（#705）

**ドキュメント修正**
- ユーザーマニュアルにヘルプボタン（F7）の説明を追加（#668）
- 管理者マニュアルの「管理」メニュー参照を修正し、カード登録手順を補完（#669）
- 管理者マニュアルのシステム設定セクションをappsettings.jsonの実際の内容に合わせて修正（#670）
- ユーザーマニュアルに乗車履歴の自動統合の説明を追加（#691）
- マニュアルのページ方向をA4横に変更（#692）
- 管理者マニュアルに手動バックアップは通常不要である旨を追記（#693）
- マニュアル「はじめに」を追加（#694）
- FAQ Q5に20件超の履歴追加方法を追記（#644）

### v1.8.0 (2026-02-13)

**新機能**
- 開発ビルド用の仮想ICカードタッチ機能を追加（#640）
- 新規購入カードに購入日を指定可能にする（#658）

**バグ修正**
- 新規購入カードの登録日が月初めになるバグを修正（#657）

**ドキュメント修正**
- ユーザーマニュアルにシステム警告エリアの説明を追加（#666）

### v1.7.0 (2026-02-12)

**新機能**
- メインウィンドウにヘルプボタンを追加し、マニュアルを直接開けるようにする（#641）
- 履歴詳細画面の分割で「摘要のみ更新」か「別々の履歴に分割」を選択可能にする（#634）
- 利用履歴の変更で利用者を空欄にできるようにする（#636）
- マニュアルのPDF変換スクリプトを追加（#642）
- インストーラー作成時にdocx/PDFマニュアルを自動生成（#643）

**バグ修正**
- 認証ダイアログのサイズが文字サイズに追従しない問題を修正（#631）
- 履歴詳細画面の分割操作が摘要に反映されない問題を修正（#633）
- 物品出納簿の上書き時に備考欄が消える問題を修正（#637）
- データインポートのプレビューが表示されない問題を修正（#638）
- 履歴インポートで金額変更が検出されずスキップされる問題を修正（#639）

### v1.6.0 (2026-02-10)

**新機能**
- カード登録時に当月履歴を自動読み取りしてledgerに登録する機能を追加（#596）
- カード返却時に今月の履歴完全性をチェックし、不足の可能性がある場合に警告を表示（#596）
- CSVインポート実行後に完了ダイアログを表示（#598）
- CSVインポートプレビューを表形式で直接表示するよう改善（#597）

**バグ修正**
- カード種別選択ダイアログのサイズが文字サイズに追従しない問題を修正（#628）
- プレビュー表示とExcelファイルの内容が一致しない問題を修正（#603）
- 履歴エクスポート時に同一カードの履歴をまとめて出力するよう修正（#592）
- 帳票上書き時にデータ行の太字書式をリセットするよう修正（#591）
- 設定変更後にカード一覧の残額警告表示が即時更新されない問題を修正（#604）
- 認証ダイアログのメッセージに操作理由と読点を追加（#602）
- 「ICカード」表記を「交通系ICカード」に統一（#594）
- 繰越レコードの日付を繰越月の翌月1日に修正（#599）
- バス停名入力画面でバス停名の入力ができないバグを修正（#593）
- 新規購入が同一日のチャージより後に表示されるバグを修正（#590）
- マニュアルの表が印刷時に罫線が消えるバグを修正（#600）

**ドキュメント修正**
- DB設計書・画面設計書・クラス設計書を実装に合わせて更新（#570-#575）
- ユーザーマニュアル・管理者マニュアルの記載を修正（#568, #601）
- READMEのpublish出力パス・テスト数等を修正（#559, #561）

### v1.5.1 (2026-02-09)

**機能改善**
- カード一覧部のタイトルを「カード残高」から「カード一覧」に変更（#552）

**ドキュメント修正**
- 管理者マニュアルに利用開始時の交通系ICカード登録手順を追加（#553）
- 管理者マニュアルにデータインポートのセクションを追加（#554）
- README.mdのプラットフォームアーキテクチャ記載を修正（#558）
- ユーザーマニュアルの存在しないメニュー参照を修正（#562）
- ユーザーマニュアルの残額警告デフォルト値と範囲を修正（#563）
- ユーザーマニュアルのカード管理ボタンラベルを修正（#564）
- ユーザーマニュアルの音声設定を4モード対応に拡充（#565）
- ユーザーマニュアルにトースト通知位置・バックアップ設定を追記（#566）
- 管理者マニュアルのバックアップファイル名を修正（#567）
- DB設計書のDBファイル保存場所を修正（#569）

### v1.5.0 (2026-02-06)

**新機能**
- 履歴一覧から複数エントリの統合機能を追加（チェックボックスで選択→統合ボタン）
- 統合の取り消し機能を追加（DB永続化された履歴から任意の統合を選んで元に戻せる）
- 統合履歴選択ダイアログを追加（一覧から取り消したい統合を選択）
- 履歴統合のユニットテスト31件を追加（LedgerMergeServiceTests）
- 払戻済カード状態の追加
- 操作ログに変更内容の詳細表示を追加
- 履歴編集画面で利用者の変更を可能に

**改善**
- 統合対象の選択をCtrl+クリックからチェックボックス方式に変更（初心者にもわかりやすく）
- 統合完了・取消完了の通知をトースト通知からMessageBoxに変更（操作中の画面で確認しやすく）
- 元に戻せる統合履歴がない場合は「統合を元に戻す」ボタンをグレーアウト
- カード一覧の幅を広げて金額・貸出者を表示
- カード一覧とシステム警告の高さ比率を7:3に調整
- ヘッダーボタンをWrapPanelで折り返し対応に変更

**バグ修正**
- チャージと利用の履歴を統合できないようバリデーションを追加
- SummaryGenerator: グループID付き往復検出の修正（「A～A」→「A～B 往復」）
- 統合→分割後の履歴ソート順を修正（balance DESCをタイブレーカーに追加）
- System.Text.Json 4.7でDictionary&lt;int,int&gt;がシリアライズできない問題を修正
- 統合説明テキストにシャローコピー由来のバグがあった問題を修正
- 物品出納簿の上書き時にフッターが消える問題を修正
- 貸出処理時にカード残高を読み取るよう修正
- 西鉄バス利用時のバス判定を修正
- カード登録モード選択ダイアログのUI修正
- サイドバー幅をフォントサイズに応じて動的調整

### v1.4.0 (2026-02-06)

**新機能**
- 乗り継ぎ駅のエイリアスマッピング機能を追加（博多↔筑紫口等の同一乗り継ぎ駅を認識）
- 利用履歴CSVインポート時にカードIDm空欄を許可
- データエクスポート完了時に保存完了メッセージを表示
- デバッグ用データビューアにDBファイル選択機能を追加
- 年度途中に本アプリを導入した場合の新規登録に対応
- 物品出納簿に12行ごとの改ページ機能を追加（ヘッダー・備考欄も各ページにコピー）

**改善**
- 起動時間を短縮
- 乗車履歴統合・分割UIを大幅改善（分割線クリック方式、アルファベットラベル追加）

**バグ修正**
- 空港グループを乗り継ぎ同一視から除外
- 物品出納簿の金額セルの表示形式を会計形式に修正
- バックアップからのリストアが失敗する問題を修正
- アンインストーラーのファイル名をわかりやすく変更
- 新規購入より前の月の物品出納簿を作成しないよう修正

### v1.3.0 (2026-02-05)

**新機能**
- 物品出納簿を年度ファイル・月別シート構成に変更（年度ごとに1ファイル、月ごとにシート）
- 乗車履歴の統合・分割機能を追加（複数の乗車履歴を1つの行程にまとめる）

**改善**
- 利用履歴詳細画面の金額表示を正の値に変更（符号なし表示）
- 同一日ではチャージを利用より先に表示
- 月次繰越の受入欄を空欄に変更
- 新規購入カードの繰越行を非表示にする
- インストーラービルド時にマニュアルを自動変換

**バグ修正**
- 貸出タッチ忘れ時の履歴が記録されない問題を修正
- カード登録後にダッシュボードを更新するよう修正
- カード種別選択前に残高を読み取るように修正

### v1.2.0 (2026-02-04)

**新機能**
- 重要な操作（履歴編集・職員削除・カード削除）の前に職員証タッチによる認証を必須化
- ステータスバーにアプリケーションのバージョン番号を表示
- インストーラーにWindows起動時の常駐オプションを追加

**バグ修正**
- 履歴詳細の表示順を正しい時系列順に修正（チャージを含む場合も対応）

### v1.1.0 (2026-02-03)

**新機能**
- 残高整合性チェック機能を追加（交通系ICカードの実残高と記録の不一致を検出）
- 開発者向けスクリーンショット撮影ツールを追加

**改善**
- ユーザーマニュアル・管理者マニュアルの内容を充実

### v1.0.8 (2026-02-01)

**ドキュメント**
- 配布手順書をインストーラー対応に更新
- 機能設計書の誤記を修正

### v1.0.7 (2026-01-31)

**新機能**
- アンインストール時にユーザーデータ・バックアップ・ログの削除を選択可能に
- インストーラーのバージョンをアプリ本体と自動同期

### v1.0.6 (2026-01-31)

**改善**
- ビルド時の警告を解消

### v1.0.5 (2026-01-31)

**改善**
- デフォルトの文字サイズですべてのボタンが画面内に収まるように調整

### v1.0.4 (2026-01-31)

**バグ修正**
- マイグレーションテストの不具合を修正

### v1.0.3 (2026-01-31)

**改善**
- 履歴の表示順を物品出納簿に合わせて古い順（昇順）に変更

### v1.0.2 (2026-01-31)

**新機能**
- 残高不足時の現金補填に対応（備考に不足額を自動記録）

### v1.0.1 (2026-01-30)

**新機能**
- ポイント還元によるチャージに対応
- 交通系ICカードの払い戻し処理に対応

### v1.0.0 (2025-12)

- 初版リリース
