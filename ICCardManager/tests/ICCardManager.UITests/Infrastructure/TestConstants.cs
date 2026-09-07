using System;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// MainWindow.xaml 等で定義されている AutomationProperties.Name の定数。
    /// XAML 側の値と一致させること。
    /// </summary>
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
        public const string MainWindowName = "交通系ICカード管理システム：ピッすい";

        // ── ツールバーボタン ──────────────────────────────
        public const string OpenReportButton = "帳票ダイアログを開く";
        public const string OpenStaffManageButton = "職員管理ダイアログを開く";
        public const string OpenCardManageButton = "交通系ICカード管理ダイアログを開く";
        public const string OpenDataExportImportButton = "データエクスポート/インポートダイアログを開く";
        public const string OpenSettingsButton = "設定ダイアログを開く";
        public const string OpenSystemManageButton = "システム管理ダイアログを開く";
        public const string OpenHelpButton = "ヘルプを開く";
        public const string ExitButton = "アプリケーションを終了";

        // ── ダイアログ名 ─────────────────────────────────
        public const string ReportDialogName = "帳票作成ダイアログ";
        public const string StaffManageDialogName = "職員管理ダイアログ";
        public const string CardManageDialogName = "交通系ICカード管理ダイアログ";
        public const string DataExportImportDialogName = "データエクスポート/インポートダイアログ";
        public const string SettingsDialogName = "設定ダイアログ";
        public const string SystemManageDialogName = "システム管理ダイアログ";

        // ── ステータスバー ────────────────────────────────
        // StatusBarItem は UIA ツリーに公開されないため、
        // 内部の TextBlock のテキスト内容で検索する。
        public const string CardReaderStatusTextPrefix = "リーダー:";
        public const string AppVersionTextPrefix = "Ver.";

        // ── コンテンツエリア ──────────────────────────────
        // Border は UIA ツリーに公開されないため、
        // 内部の TextBlock のテキスト内容で検索する。
        public const string UsageGuideText = "📖 使い方";
        public const string CardList = "カード一覧";
        public const string HistoryArea = "利用履歴表示エリア";

        /// <summary>
        /// 履歴表示エリア内の「履歴を閉じる」ボタン。エリア自体は Border で UIA に公開されないため、
        /// 履歴が開いたことの判定にはこのボタンの出現を使う（Issue #2016）。
        /// </summary>
        public const string CloseHistoryButton = "履歴を閉じる";
        public const string DashboardSortOrder = "ダッシュボードの並び順";

        // ── StaffAuthDialog ───────────────────────────────
        /// <summary>
        /// StaffAuthDialog のウィンドウタイトル（Title="職員証による認証"）。
        /// </summary>
        public const string StaffAuthDialogName = "職員証による認証";

        /// <summary>
        /// StaffAuthDialog の StatusText に付与される AutomationProperties.HelpText の値。
        /// AutomationId が無い場合は HelpText で要素を識別する。
        /// </summary>
        public const string StaffAuthStatusHelpText = "認証処理の現在の状態（成功・失敗・進行中など）";

        /// <summary>
        /// デバッグ用仮想タッチボタンの AutomationProperties.Name。
        /// DEBUG ビルド時のみ表示される（Issue #688）。
        /// </summary>
        public const string DebugVirtualTouchButtonName = "職員証仮想タッチ（デバッグ用）";

        /// <summary>
        /// StaffManageDialog の削除ボタンの AutomationProperties.Name。
        /// </summary>
        public const string StaffManageDeleteButtonName = "削除";

        /// <summary>
        /// StaffAuthDialog のキャンセルボタン名。
        /// </summary>
        public const string StaffAuthCancelButtonName = "キャンセル";

        // ── OperationLogDialog（Issue #1522） ────────────
        /// <summary>
        /// SystemManageDialog 内の「操作ログを表示」ボタン。
        /// </summary>
        public const string OpenOperationLogButton = "操作ログを表示";

        /// <summary>
        /// OperationLogDialog の AutomationProperties.Name。
        /// </summary>
        public const string OperationLogDialogName = "操作ログダイアログ";

        /// <summary>
        /// クイックフィルタ「今日」ボタン。
        /// </summary>
        public const string OperationLogQuickFilterToday = "今日の期間に設定";

        /// <summary>
        /// クイックフィルタ「今月」ボタン。
        /// </summary>
        public const string OperationLogQuickFilterThisMonth = "今月の期間に設定";

        /// <summary>
        /// クイックフィルタ「先月」ボタン。
        /// </summary>
        public const string OperationLogQuickFilterLastMonth = "先月の期間に設定";

        /// <summary>
        /// 操作種別 ComboBox。クイックフィルタとの矩形衝突検証で隣接基準として使用。
        /// </summary>
        public const string OperationLogActionTypeComboBox = "操作種別";

        // ── 仮想タッチ操作パネル・トースト（Issue #2019。DEBUG ビルドのみ） ────────
        /// <summary>メイン画面下部の DEBUG パネルの「職員証」ボタン（IDm FFFF000000000001 のタッチを模擬）。</summary>
        public const string DebugPanelStaffButton = "職員証";

        /// <summary>同「交通系ICカード」ボタン（IDm 07FE112233445566 のタッチを模擬）。</summary>
        public const string DebugPanelIcCardButton = "交通系ICカード";

        /// <summary>同「仮想タッチ」ボタン（履歴を指定できる仮想タッチダイアログを開く。Issue #640）。</summary>
        public const string DebugPanelVirtualTouchButton = "仮想タッチ";

        /// <summary>仮想タッチダイアログの AutomationProperties.Name。</summary>
        public const string VirtualCardDialogName = "仮想交通系ICカード設定ダイアログ";
        public const string VirtualCardHistoryGrid = "利用履歴一覧";
        public const string VirtualCardAddEntryButton = "履歴追加";
        public const string VirtualCardExecuteButton = "タッチ実行";

        /// <summary>トースト通知ウィンドウの AutomationProperties.Name（メイン画面とは別のトップレベルウィンドウ）。</summary>
        public const string ToastWindowName = "通知ウィンドウ";

        /// <summary>返却後に開くバス停名入力ダイアログ。</summary>
        public const string BusStopInputDialogName = "バス停名入力ダイアログ";

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
