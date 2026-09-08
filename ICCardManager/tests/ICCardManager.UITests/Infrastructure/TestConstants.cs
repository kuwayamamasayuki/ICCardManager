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
        // （TouchScreenshotTests.InvokeDebugPanelButton）。
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

        /// <summary>トースト通知ウィンドウの AutomationProperties.Name（メイン画面とは別のトップレベルウィンドウ）。</summary>
        [UiaName]
        public const string ToastWindowName = "通知ウィンドウ";

        /// <summary>返却後に開くバス停名入力ダイアログ。</summary>
        [UiaName]
        public const string BusStopInputDialogName = "バス停名入力ダイアログ";

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
    }
}
