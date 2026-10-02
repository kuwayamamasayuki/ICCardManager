using System;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// MainWindow.xaml 等で定義されている AutomationProperties.Name の定数。
    /// XAML 側の値と一致させること。
    /// </summary>
    /// <remarks>
    /// Issue #2018: 一致は人手の突き合わせに頼らず、<see cref="UiaNameAttribute"/> /
    /// <see cref="UiaNamePrefixAttribute"/> / <see cref="UiaHelpTextAttribute"/> を付けて宣言する。
    /// CI で走る <c>UiTestAutomationNameConventionTests</c> が本ファイルをソーステキストとして走査し、
    /// 値が実在する XAML の属性値と一致することを検証する。
    /// <para>
    /// マーカーを付けない定数は静的検査の対象外になる。UIA Name ではないもの
    /// （TextBlock の本文で検索する値、タイムアウト秒数など）にだけ許される。
    /// </para>
    /// </remarks>
    internal static class TestConstants
    {
        /// <summary>
        /// Issue #1522 関連のクイックフィルタ FlaUI テストをスキップすべきかを判定する。
        /// <list type="bullet">
        ///   <item>環境変数 <c>SKIP_QUICK_FILTER_UITEST=1</c> が設定されている場合（明示 opt-out）</item>
        ///   <item>環境変数 <c>WSL_DISTRO_NAME</c> が設定されている場合（参考: Win32 子プロセスでは継承されないため通常は機能しないが、bash → wslenv 経由で渡された場合の opt-out として残す）</item>
        /// </list>
        /// Issue #1522 では「WSL2 経由実行時に二段モーダル取得が不安定」との既知制約が記載されたが、
        /// 現環境では再現しないことを確認済み。将来再発した際の安全網として残す。
        /// </summary>
        public static bool ShouldSkipQuickFilterFlaUiTest =>
            string.Equals(Environment.GetEnvironmentVariable("SKIP_QUICK_FILTER_UITEST"), "1",
                StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"));


        // ── メインウィンドウ ──────────────────────────────
        // WPF Window の UIA Name は Title と AutomationProperties.Name のどちらかが返る場合がある。
        // テストでは StartsWith で前方一致させるため、共通プレフィックス（= Title）を使う。
        [UiaNamePrefix]
        public const string MainWindowName = "交通系ICカード管理システム：ピッすい";

        // ── ツールバーボタン ──────────────────────────────
        [UiaName]
        public const string OpenReportButton = "帳票ダイアログを開く";
        [UiaName]
        public const string OpenStaffManageButton = "職員管理ダイアログを開く";
        [UiaName]
        public const string OpenCardManageButton = "交通系ICカード管理ダイアログを開く";
        [UiaName]
        public const string OpenDataExportImportButton = "データエクスポート/インポートダイアログを開く";
        [UiaName]
        public const string OpenSettingsButton = "設定ダイアログを開く";
        [UiaName]
        public const string OpenSystemManageButton = "システム管理ダイアログを開く";
        [UiaName]
        public const string OpenHelpButton = "ヘルプを開く";
        [UiaName]
        public const string ExitButton = "アプリケーションを終了";

        // ── ダイアログ名 ─────────────────────────────────
        [UiaName]
        public const string ReportDialogName = "帳票作成ダイアログ";
        [UiaName]
        public const string StaffManageDialogName = "職員管理ダイアログ";
        [UiaName]
        public const string CardManageDialogName = "交通系ICカード管理ダイアログ";
        [UiaName]
        public const string DataExportImportDialogName = "データエクスポート/インポートダイアログ";
        [UiaName]
        public const string SettingsDialogName = "設定ダイアログ";
        [UiaName]
        public const string SystemManageDialogName = "システム管理ダイアログ";

        // ── ステータスバー ────────────────────────────────
        // StatusBarItem は UIA ツリーに公開されないため、
        // 内部の TextBlock のテキスト内容で検索する。
        // TextBlock は AutomationProperties.Name が無ければ UIA Name が Text へフォールバックするため、
        // これらは AutomationProperties ではなく画面に出る文字列そのもの（前方一致で検索する）。
        [NotUiaName]
        public const string CardReaderStatusTextPrefix = "リーダー:";
        [NotUiaName]
        public const string AppVersionTextPrefix = "Ver.";

        // ── コンテンツエリア ──────────────────────────────
        // Border は UIA ツリーに公開されないため、
        // 内部の TextBlock のテキスト内容で検索する。
        // 使い方ガイドの見出し TextBlock の Text。囲む Border には
        // AutomationProperties.Name="使い方ガイド" が付いているが、テストが探しているのは見出しの文字列。
        [NotUiaName]
        public const string UsageGuideText = "📖 使い方";

        // カード一覧（ListView）と履歴表示エリア（Border）は AutomationProperties.Name を持つ。
        [UiaName]
        public const string CardList = "カード一覧";
        [UiaName]
        public const string HistoryArea = "利用履歴表示エリア";

        /// <summary>
        /// 履歴表示エリア内の「履歴を閉じる」ボタン。エリア自体は Border で UIA に公開されないため、
        /// 履歴が開いたことの判定にはこのボタンの出現を使う（Issue #2016）。
        /// </summary>
        [UiaName]
        public const string CloseHistoryButton = "履歴を閉じる";
        [UiaName]
        public const string DashboardSortOrder = "ダッシュボードの並び順";

        // ── StaffAuthDialog ───────────────────────────────
        /// <summary>
        /// StaffAuthDialog のウィンドウの AutomationProperties.Name。
        /// </summary>
        /// <remarks>
        /// Issue #2018: WPF の Window は <c>AutomationProperties.Name</c> が付いていれば
        /// そちらが UIA Name になり、無いときだけ <c>Title</c>（"職員証による認証"）が使われる。
        /// StaffAuthDialog.xaml は両方を持つため、UIA から見える名前は Title ではなく
        /// <c>AutomationProperties.Name="職員証認証ダイアログ"</c> のほう。
        /// </remarks>
        [UiaName]
        public const string StaffAuthDialogName = "職員証認証ダイアログ";

        /// <summary>
        /// StaffAuthDialog の StatusText に付与される AutomationProperties.HelpText の値。
        /// AutomationId が無い場合は HelpText で要素を識別する。
        /// </summary>
        [UiaHelpText]
        public const string StaffAuthStatusHelpText = "認証処理の現在の状態（成功・失敗・進行中など）";

        /// <summary>
        /// デバッグ用仮想タッチボタンの AutomationProperties.Name。
        /// DEBUG ビルド時のみ表示される（Issue #688）。
        /// </summary>
        [UiaName]
        public const string DebugVirtualTouchButtonName = "職員証仮想タッチ（デバッグ用）";

        /// <summary>
        /// StaffManageDialog の削除ボタンの AutomationProperties.Name。
        /// </summary>
        /// <remarks>
        /// Issue #2018: ここは追加当初（#1500）から Content の文字列 "削除" のままで、
        /// XAML の <c>AutomationProperties.Name="職員削除"</c>（#1404 で付与）とは
        /// 一度も一致していなかった。<c>ByName("削除")</c> はボタン内側の Text 要素に一致し、
        /// Text は Invoke パターンを持たないため <c>PatternNotSupportedException</c> になる。
        /// </remarks>
        [UiaName]
        public const string StaffManageDeleteButtonName = "職員削除";

        /// <summary>
        /// StaffAuthDialog のキャンセルボタンの AutomationProperties.Name。
        /// </summary>
        [UiaName]
        public const string StaffAuthCancelButtonName = "キャンセル";

        // ── OperationLogDialog（Issue #1522） ────────────
        /// <summary>
        /// SystemManageDialog 内の「操作ログを表示」ボタン。
        /// </summary>
        [UiaName]
        public const string OpenOperationLogButton = "操作ログを表示";

        /// <summary>
        /// OperationLogDialog の AutomationProperties.Name。
        /// </summary>
        [UiaName]
        public const string OperationLogDialogName = "操作ログダイアログ";

        /// <summary>
        /// クイックフィルタ「今日」ボタン。
        /// </summary>
        [UiaName]
        public const string OperationLogQuickFilterToday = "今日の期間に設定";

        /// <summary>
        /// クイックフィルタ「今月」ボタン。
        /// </summary>
        [UiaName]
        public const string OperationLogQuickFilterThisMonth = "今月の期間に設定";

        /// <summary>
        /// クイックフィルタ「先月」ボタン。
        /// </summary>
        [UiaName]
        public const string OperationLogQuickFilterLastMonth = "先月の期間に設定";

        /// <summary>
        /// 操作種別 ComboBox。クイックフィルタとの矩形衝突検証で隣接基準として使用。
        /// </summary>
        [UiaName]
        public const string OperationLogActionTypeComboBox = "操作種別";

        // ── 仮想タッチ操作パネル・トースト（Issue #2019。DEBUG ビルドのみ） ────────
        // DEBUG パネルの 3 ボタンは AutomationProperties.Name を持たず、Content の文字列が
        // そのまま UIA Name になる（MainWindow.xaml の <Button Content="…"/>）。撮影は開発時にしか
        // 使わないパネルで、読み上げ対象でもないため属性を足していない。Content 由来の Name は
        // 内側の Text 要素とも一致し得るので、探索側は ControlType.Button に限定すること
        // （TouchOperations.InvokeDebugPanelButton）。
        /// <summary>メイン画面下部の DEBUG パネルの「職員証」ボタン（IDm FFFF000000000001 のタッチを模擬）。</summary>
        [NotUiaName]
        public const string DebugPanelStaffButton = "職員証";

        /// <summary>同「交通系ICカード」ボタン（IDm 07FE112233445566 のタッチを模擬）。</summary>
        [NotUiaName]
        public const string DebugPanelIcCardButton = "交通系ICカード";

        /// <summary>同「仮想タッチ」ボタン（履歴を指定できる仮想タッチダイアログを開く。Issue #640）。</summary>
        [NotUiaName]
        public const string DebugPanelVirtualTouchButton = "仮想タッチ";

        /// <summary>仮想タッチダイアログの AutomationProperties.Name。</summary>
        [UiaName]
        public const string VirtualCardDialogName = "仮想交通系ICカード設定ダイアログ";
        [UiaName]
        public const string VirtualCardHistoryGrid = "利用履歴一覧";
        [UiaName]
        public const string VirtualCardAddEntryButton = "履歴追加";
        [UiaName]
        public const string VirtualCardExecuteButton = "タッチ実行";

        /// <summary>
        /// トースト通知ウィンドウの AutomationProperties.HelpText（メイン画面とは別のトップレベルウィンドウ）。
        /// </summary>
        /// <remarks>
        /// Issue #2142: Name は通知の内容（タイトル・本文）を読み上げるため表示のたびに変わる。
        /// 固定の「通知ウィンドウ」は HelpText へ移したので、トーストの識別も HelpText で行う。
        /// </remarks>
        [UiaHelpText]
        public const string ToastWindowHelpText = "通知ウィンドウ";

        /// <summary>返却後に開くバス停名入力ダイアログ。</summary>
        [UiaName]
        public const string BusStopInputDialogName = "バス停名入力ダイアログ";

        // ── 貸出・返却フローと MessageBox のオーナーの回帰テスト（Issue #2190） ────────

        /// <summary>トーストのタイトル・本文の TextBlock の AutomationProperties.HelpText（ToastNotificationWindow.xaml）。</summary>
        [UiaHelpText]
        public const string ToastTitleHelpText = "通知タイトル";
        [UiaHelpText]
        public const string ToastMessageHelpText = "通知メッセージ";

        // トーストのタイトルは AutomationProperties ではなく、表示のたびに ToastNotificationWindow.ShowLend / ShowReturn が
        // 設定する文字列そのもの（Name は TextBlock の Text へフォールバックする）。
        /// <summary>貸出完了トーストのタイトル（<c>ToastNotificationWindow.ShowLend</c>）。</summary>
        [NotUiaName]
        public const string LendToastTitle = "いってらっしゃい！";

        /// <summary>返却完了トーストのタイトル（<c>ToastNotificationWindow.ShowReturn</c>）。</summary>
        [NotUiaName]
        public const string ReturnToastTitle = "おかえりなさい！";

        // 次の操作ガイドの文言は AutomationProperties.Name="{Binding NextActionMessage}" のバインディングで、
        // XAML にリテラルとしては現れない（値は MainViewModel.NextActionMessage）。
        /// <summary>職員証タッチ待ちの「次の操作ガイド」（<c>MainViewModel.NextActionMessage</c> の既定分岐）。</summary>
        [NotUiaName]
        public const string NextActionWaitingForStaffCard = "貸出・返却は職員証を、履歴の確認は交通系ICカードをタッチしてください";

        /// <summary>交通系ICカードタッチ待ちの「次の操作ガイド」の末尾（前に「○○さん、」が付く）。</summary>
        [NotUiaName]
        public const string NextActionWaitingForIcCardSuffix = "さん、交通系ICカードをタッチしてください";

        /// <summary>バス停名入力ダイアログの入力欄・保存ボタン。</summary>
        [UiaName]
        public const string BusStopNameInput = "バス停名";
        [UiaName]
        public const string BusStopSaveButton = "バス停名を保存";

        // 終了の確認（Issue #2143）は Win32 の MessageBox で、タイトルは XAML ではなく
        // MainViewModel.ConfirmExit が DialogService.ShowConfirmation へ渡す文字列そのもの。
        /// <summary>終了の確認 MessageBox のタイトル。</summary>
        [NotUiaName]
        public const string ExitConfirmationTitle = "ピッすいの終了";

        /// <summary>交通系ICカード管理ダイアログの「削除」ボタン（MessageBox のオーナーの回帰テストで確認ダイアログを出すために使う）。</summary>
        [UiaName]
        public const string CardDeleteButton = "カード削除";

        // ── マニュアル用スクリーンショット 第 3 段階（Issue #2011） ──────

        /// <summary>メイン画面のツールバーから管理者ダッシュボード（#1692）を開くボタン。</summary>
        [UiaName]
        public const string OpenAdminDashboardButton = "管理者ダッシュボードを開く";
        [UiaName]
        public const string AdminDashboardDialogName = "管理者ダッシュボード";

        /// <summary>管理者ダッシュボードの「運用状況」タブの一覧。非同期の集計が終わったことの目印に使う。</summary>
        [UiaName]
        public const string AdminDashboardCardOperationList = "カードごとの運用状況一覧";

        /// <summary>システム管理ダイアログから接続診断（#1690）を開くボタン。</summary>
        [UiaName]
        public const string OpenConnectionDiagnosticsButton = "接続診断を開く";
        [UiaName]
        public const string ConnectionDiagnosticsDialogName = "接続診断ダイアログ";

        /// <summary>接続診断の結果一覧。診断が終わったことの目印に使う。</summary>
        [UiaName]
        public const string ConnectionDiagnosticsItemList = "診断項目一覧";

        /// <summary>操作ログの一覧。読み込みが終わったことの目印に使う。</summary>
        [UiaName]
        public const string OperationLogList = "操作ログ一覧";

        /// <summary>システム管理ダイアログから同一とみなす駅・バス停の設定（#1905）を開くボタン。</summary>
        [UiaName]
        public const string OpenTransferStationGroupButton = "同一とみなす駅・バス停を設定";
        [UiaName]
        public const string TransferStationGroupDialogName = "同一とみなす駅・バス停の設定ダイアログ";

        /// <summary>システム管理ダイアログのリストア用バックアップ一覧。</summary>
        [UiaName]
        public const string BackupFileList = "バックアップファイル一覧";

        /// <summary>帳票作成ダイアログの「すべてのカードを選択」ボタン（#1691）。</summary>
        [UiaName]
        public const string ReportSelectAllCardsButton = "すべてのカードを選択";

        /// <summary>帳票作成ダイアログの事前チェック（#1688）を実行するボタン。</summary>
        [UiaName]
        public const string ReportPreflightButton = "帳票の事前チェック";
        [UiaName]
        public const string ReportPreflightDialogName = "帳票の事前チェック結果ダイアログ";

        /// <summary>交通系ICカード管理ダイアログの「貸出記録の作成」ボタン（#1909）。</summary>
        [UiaName]
        public const string SystemLendButton = "貸出記録の作成";
        [UiaName]
        public const string SystemLendDialogName = "貸出記録の作成ダイアログ";

        /// <summary>履歴表示エリアの「履歴行を追加」ボタン。開く先は追加・修正で共通のダイアログ。</summary>
        [UiaName]
        public const string AddLedgerRowButton = "履歴行を追加";
        [UiaName]
        public const string LedgerRowEditDialogName = "履歴行の追加・修正ダイアログ";

        /// <summary>履歴一覧の各行にある統合対象のチェックボックス（#837 の同日統合ではなく履歴一覧の統合）。</summary>
        [UiaName]
        public const string MergeTargetCheckBox = "統合対象として選択";

        /// <summary>返却後に開く同行者数入力ダイアログ（#1906 / #2009）。</summary>
        [UiaName]
        public const string CompanionCountInputDialogName = "同行者数入力ダイアログ";

        /// <summary>
        /// ステータスバーの「再接続」ボタン（カードリーダー切断時だけ表示される）。
        /// </summary>
        /// <remarks>
        /// 接続状態を囲む <c>StatusBarItem</c> は <c>AutomationProperties.Name="カードリーダー接続状態"</c> を
        /// 持つが、WPF は <c>StatusBarItem</c> を UIA ツリーへ公開しないため要素としては取れない（実測）。
        /// 撮影では、状態の文字列（<see cref="CardReaderStatusTextPrefix"/> で探す TextBlock）と
        /// このボタンの合併矩形をステータスバーの該当部分として扱う。
        /// </remarks>
        [UiaName]
        public const string CardReaderReconnectButton = "カードリーダーに再接続";

        /// <summary>
        /// カードリーダーが切断されているときにステータスバーへ出る文字列（MainWindow.xaml の Setter の値）。
        /// AutomationProperties.Name ではなく画面に出る文字列そのもの。
        /// </summary>
        [NotUiaName]
        public const string CardReaderDisconnectedText = "リーダー: 切断";

        /// <summary>
        /// システム警告エリアの見出し TextBlock の Text。囲む要素に AutomationProperties.Name が無いため、
        /// 警告が出たことの判定にはこの文字列を使う（<see cref="UsageGuideText"/> と同じ扱い）。
        /// </summary>
        [NotUiaName]
        public const string SystemWarningHeaderText = "⚠ システム警告";

        /// <summary>
        /// 残額不足の警告だけを見分ける文字列（<c>WarningService</c> の LowBalance の
        /// <c>DisplayText</c> にだけ現れる）。警告行は <c>DisplayText</c> をそのまま表示する
        /// TextBlock なので、UIA Name が Text へフォールバックして一致する。
        /// </summary>
        /// <remarks>
        /// 警告エリアには投入データで作れない環境由来の警告（更新の案内・journal_mode の低下）も並ぶため、
        /// 「警告エリアが無いこと」は投入データの正しさの表明にならない。投入データが支配する
        /// 残額不足だけを見る（実測でこの形を踏んだ）。
        /// </remarks>
        [NotUiaName]
        public const string LowBalanceWarningMarker = "（しきい値:";

        // ── タイムアウト（秒） ────────────────────────────
        public const int AppLaunchTimeoutSeconds = 30;
        public const int DialogOpenTimeoutSeconds = 10;

        /// <summary>
        /// OperationLogDialog は初回起動時に DB クエリで遅延する可能性があるため、
        /// 二段モーダルの取得には長めの待ち時間を確保する（Issue #1522）。
        /// </summary>
        public const int OperationLogDialogOpenTimeoutSeconds = 30;

        // ── ダイアログの閉じる前の確認・Esc・履歴の月送りの回帰テスト（Issue #2192） ────────

        /// <summary>利用履歴詳細ダイアログ（#1743 の閉じる前の確認の対象）。</summary>
        [UiaName]
        public const string LedgerDetailDialogName = "利用履歴詳細ダイアログ";
        [UiaName]
        public const string LedgerDetailMergeAllButton = "すべて統合";
        [UiaName]
        public const string LedgerDetailSaveButton = "保存";
        [UiaName]
        public const string LedgerDetailCloseButton = "閉じる";
        [UiaName]
        public const string LedgerDetailSplitAllButton = "すべて分割";
        [UiaName]
        public const string LedgerDetailSummaryOnlyButton = "摘要のみ更新";
        [UiaName]
        public const string LedgerDetailFullSplitButton = "別々の履歴に分割";

        // 保存完了の表示は StatusMessage へのバインドで、XAML にリテラルとしては現れない（LedgerDetailViewModel.SaveAsync）。
        /// <summary>利用履歴詳細ダイアログで保存が終わったときの表示。</summary>
        [NotUiaName]
        public const string LedgerDetailSavedMessage = "保存しました";

        // 履歴一覧の各行の「詳細」ボタンは AutomationProperties.Name を持たず、Content の文字列が UIA Name になる。
        // 明細を持つ行でだけ有効（IsEnabled="{Binding HasDetails}"）。
        /// <summary>履歴一覧の行の「詳細」ボタン。</summary>
        [NotUiaName]
        public const string HistoryRowDetailButton = "詳細";

        // 未保存の変更の破棄確認（LedgerDetailDialog.ConfirmDiscardChanges）は Win32 の MessageBox で、
        // タイトルは XAML ではなくコードビハインドが MessageBox.Show へ渡す文字列そのもの。
        /// <summary>未保存の変更を破棄してよいかの確認 MessageBox のタイトル。</summary>
        [NotUiaName]
        public const string DiscardConfirmationTitle = "確認";

        /// <summary>履歴の表示期間を前後の月へ動かす矢印（#2030）。</summary>
        [UiaName]
        public const string HistoryPreviousMonthButton = "前の月";
        [UiaName]
        public const string HistoryNextMonthButton = "次の月";

        /// <summary>
        /// 月選択ポップアップの「適用」ボタン。ポップアップが開いていることの目印に使う。
        /// </summary>
        /// <remarks>
        /// ポップアップ自体の名前（XAML の <c>Popup</c> に付いた「月選択ポップアップ」）では探せない。<c>Popup</c> は
        /// UIA ツリーに現れず、現れるのは中身だけなので、開いていても見つからない（実測。この名前で探した初版は、
        /// 「開かないこと」の表明が常に緑になっていた）。中身にある要素で判定する。
        /// </remarks>
        [UiaName]
        public const string HistoryMonthSelectorApplyButton = "選択した月を適用";

        /// <summary>交通系ICカード管理ダイアログの「編集」ボタンと、編集フォームの管理番号の入力欄。</summary>
        [UiaName]
        public const string CardEditButton = "カード情報編集";
        [UiaName]
        public const string CardNumberInput = "管理番号";

        /// <summary>設定ダイアログの残額警告しきい値の入力欄（IsCancel のダイアログで TextBox にフォーカスがある状態を作る）。</summary>
        [UiaName]
        public const string SettingsWarningBalanceInput = "残額警告しきい値";

        // ── メイン画面とダイアログの基本操作の回帰テスト（Issue #2194） ────────

        /// <summary>設定ダイアログの「保存」ボタン。保存に成功するとダイアログは閉じる。</summary>
        [UiaName]
        public const string SettingsSaveButton = "設定を保存";

        // 初期フォーカスの期待値（UT-054。静的検査 DialogInitialFocusTests が XAML の FocusManager.FocusedElement を固定する）
        /// <summary>設定ダイアログの初期フォーカス（<c>ToastPositionComboBox</c>）。</summary>
        [UiaName]
        public const string SettingsToastPositionComboBox = "トースト通知位置選択";

        /// <summary>データ入出力ダイアログの初期フォーカス（<c>ExportDataTypeComboBox</c>）。</summary>
        [UiaName]
        public const string ExportDataTypeComboBox = "エクスポートするデータ種別";

        /// <summary>履歴行の追加・修正ダイアログの初期フォーカス（<c>EditDatePicker</c>）。</summary>
        [UiaName]
        public const string LedgerRowEditDateInput = "出納日付";

        /// <summary>履歴行の追加・修正ダイアログの摘要の入力欄（追加モードでは最初の入力エラーとして初期フォーカスが来る。#1279）。</summary>
        [UiaName]
        public const string LedgerRowEditSummaryInput = "摘要";

        /// <summary>履歴行の追加・修正ダイアログの「削除」ボタン（Issue #750）。</summary>
        [UiaName]
        public const string LedgerRowEditDeleteButton = "この履歴を削除";

        /// <summary>メイン画面の利用履歴の一覧（DataGrid）。</summary>
        [UiaName]
        public const string HistoryLedgerGrid = "利用履歴一覧";

        // 履歴一覧の各行の「変更」ボタンは AutomationProperties.Name を持たず、Content の文字列が UIA Name になる。
        /// <summary>履歴一覧の行の「変更」ボタン（職員証認証の後、履歴行の追加・修正ダイアログを開く）。</summary>
        [NotUiaName]
        public const string HistoryRowEditButton = "変更";

        // 以下は XAML の AutomationProperties ではなく、本体のコードが表示する文字列そのもの。
        /// <summary>貸出中レコードの摘要（<c>SummaryGenerator.GetLendingSummary</c> の既定）。</summary>
        [NotUiaName]
        public const string LentRecordSummary = "（貸出中）";

        /// <summary>払戻済カードをタッチしたときのエラートーストのタイトル（<c>MainViewModel</c>。Issue #530）。</summary>
        [NotUiaName]
        public const string RefundedCardToastTitle = "払戻済カード";

        /// <summary>履歴の削除の確認 MessageBox のタイトル（<c>LedgerRowEditViewModel.RequestDelete</c>）。</summary>
        [NotUiaName]
        public const string LedgerDeleteConfirmationTitle = "履歴の削除";

        /// <summary>操作ログダイアログの期間の終了日（UT-058c。最小幅で見切れないこと）。</summary>
        [UiaName]
        public const string OperationLogToDate = "終了日";

        // ── 処理中のガード・カード読み取りからの復元・接続診断のコピー・管理者ダッシュボード（Issue #2196） ────────

        // 処理中オーバーレイの文言は BusyMessage へのバインドで、XAML にリテラルとしては現れない
        // （ConnectionDiagnosticsViewModel.RunDiagnosticsAsync）。
        /// <summary>接続診断の実行中に処理中オーバーレイへ出る文言（UT-130 の処理中の状態を作るのに使う）。</summary>
        [NotUiaName]
        public const string ConnectionDiagnosticsBusyMessage = "接続診断を実行中...";

        // 処理中オーバーレイの文言（SettingsViewModel.SaveAsync の BeginBusy）。XAML にリテラルとしては現れない。
        /// <summary>設定の保存中に処理中オーバーレイへ出る文言（Issue #2197。保存中も UI が応答することの目印）。</summary>
        [NotUiaName]
        public const string SettingsSavingBusyMessage = "保存中...";

        /// <summary>交通系ICカード管理ダイアログの「新規カード登録」ボタン（押すと交通系ICカードのタッチ待ちになる）。</summary>
        [UiaName]
        public const string CardNewRegistrationButton = "新規カード登録";

        // 削除済みカードの復元の確認は Win32 の MessageBox で、タイトルは CardManageViewModel が渡す文字列そのもの。
        /// <summary>削除済みのカードを読み取ったときの復元の確認 MessageBox のタイトル（#284）。</summary>
        [NotUiaName]
        public const string DeletedCardRestoreTitle = "削除済みカード";

        /// <summary>職員管理ダイアログの「新規職員登録」ボタン（押すと職員証のタッチ待ちになる）。</summary>
        [UiaName]
        public const string StaffNewRegistrationButton = "新規職員登録";

        // 削除済み職員の復元の確認も Win32 の MessageBox で、タイトルは StaffManageViewModel が渡す文字列そのもの。
        /// <summary>削除済みの職員証を読み取ったときの復元の確認 MessageBox のタイトル（#284）。</summary>
        [NotUiaName]
        public const string DeletedStaffRestoreTitle = "削除済み職員";

        /// <summary>接続診断ダイアログの「結果をコピー」ボタン。</summary>
        [UiaName]
        public const string ConnectionDiagnosticsCopyButton = "診断結果をコピー";

        /// <summary>管理者ダッシュボードのサマリータイル（押すと「カードごとの運用状況一覧」を絞り込む）。</summary>
        [UiaName]
        public const string AdminDashboardLentTile = "貸出中のカード枚数。押すと一覧を貸出中に絞り込みます。";
        [UiaName]
        public const string AdminDashboardLongTermTile = "長期未返却のカード枚数。押すと一覧を督促対象に絞り込みます。";
        [UiaName]
        public const string AdminDashboardLowBalanceTile = "残額不足のカード枚数。押すと一覧を残額不足に絞り込みます。";
        [UiaName]
        public const string AdminDashboardAllTile = "集計対象のカード枚数。押すと絞り込みを解除します。";

        /// <summary>長期未返却とみなす日数（7・14・30 から選ぶ）と、選んだ日数で再集計するボタン。</summary>
        [UiaName]
        public const string AdminDashboardLongTermDaysComboBox = "長期未返却とみなす日数";
        [UiaName]
        public const string AdminDashboardRefreshButton = "運用状況を更新";

        /// <summary>管理者ダッシュボードの「稼働状況」「利用推移」タブと、各タブの一覧。</summary>
        [UiaName]
        public const string AdminDashboardUtilizationTab = "稼働状況タブ";
        [UiaName]
        public const string AdminDashboardUtilizationList = "カード別の稼働率一覧";
        [UiaName]
        public const string AdminDashboardTrendTab = "利用推移タブ";
        [UiaName]
        public const string AdminDashboardStaffUsageList = "職員別の月次利用額一覧";
    }
}
