namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// MainWindow.xaml 等で定義されている AutomationProperties.Name の定数。
    /// XAML 側の値と一致させること。
    /// </summary>
    internal static class TestConstants
    {
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

        // ── タイムアウト（秒） ────────────────────────────
        public const int AppLaunchTimeoutSeconds = 30;
        public const int DialogOpenTimeoutSeconds = 10;
    }
}
