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
        public const string CardReaderStatusTextPrefix = "リーダー:";
        public const string AppVersionTextPrefix = "Ver.";

        // ── コンテンツエリア ──────────────────────────────
        // Border は UIA ツリーに公開されないため、
        // 内部の TextBlock のテキスト内容で検索する。
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
