namespace ICCardManager.Common
{
    /// <summary>
    /// アプリケーション全体で使用する定数を定義するクラス。
    /// </summary>
    internal static class AppConstants
    {
        /// <summary>
        /// システム表示名。ウィンドウタイトル、ヘッダー、スプラッシュ画面等で使用。
        /// </summary>
        public const string SystemName = "交通系ICカード管理システム：ピッすい";

        // --- タイムアウト系デフォルト値（Issue #1288 で集約） ---
        // 業務ルール由来のため、.claude/rules/business-logic.md を参照のこと。
        // 実行時は AppOptions 経由で appsettings.json によるオーバーライドが可能。

        /// <summary>
        /// 30 秒再タッチルール: 同一カードが再タッチされた場合に逆処理を実行する猶予時間（秒）。
        /// <see href=".claude/rules/business-logic.md">「状態遷移」参照</see>。
        /// </summary>
        public const int DefaultCardRetouchTimeoutSeconds = 30;

        /// <summary>
        /// 職員証タッチ後のタイムアウト（秒）。この時間を経過すると職員証タッチ待ちに戻る。
        /// <see href=".claude/rules/business-logic.md">「状態遷移」参照</see>。
        /// </summary>
        public const int DefaultStaffCardTimeoutSeconds = 60;

        /// <summary>
        /// 同一カードへの同時アクセスを防ぐ排他ロック取得のタイムアウト（秒）。
        /// <see href=".claude/rules/business-logic.md">「排他制御」参照</see>。
        /// </summary>
        public const int DefaultCardLockTimeoutSeconds = 5;

        // --- バックアップ健全性（Issue #1689） ---

        /// <summary>
        /// バックアップファイルの保持世代数の上限。
        /// 起動時に1世代ずつ増えるため、およそ1か月分の履歴が残る。
        /// </summary>
        public const int MaxBackupGenerations = 30;

        /// <summary>
        /// 最終バックアップ成功からこの日数を超えて経過した場合、システム警告を表示する。
        /// 起動頻度に依存しない「最終成功からの経過日数」で判定することで、
        /// 長期休暇などでアプリを起動しなかった期間も検知できる。
        /// </summary>
        public const int BackupStaleWarningDays = 7;

        // --- 接続診断（Issue #1690） ---

        /// <summary>
        /// バックアップ保存先の空き容量がこの値を下回った場合、接続診断で警告する（バイト）。
        /// </summary>
        /// <remarks>
        /// DB 本体は数十 MB 規模だが、保持世代数の上限（<see cref="MaxBackupGenerations"/>）分の
        /// バックアップが同居するため、当面の書き込みに余裕がある水準として 1 GB を採る。
        /// 空き容量不足はバックアップが失敗し始める予兆であり本体動作を即座に妨げるものではないため、
        /// 判定は「異常」ではなく「警告」とする。
        /// </remarks>
        public const long DiagnosticsLowDiskSpaceWarningBytes = 1024L * 1024L * 1024L;

        // --- DB 疎通確認（Issue #1716） ---

        /// <summary>
        /// DB 接続の疎通確認（<c>DbContext.CheckConnection</c>）をこの秒数まで待ち、
        /// 完了しなければ「接続なし」とみなす。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 疎通確認は「接続リース取得 → 接続オープン（切断後は再オープン）→ クエリ →
        /// ファイル到達確認」と複数のネットワーク待ちが直列に並ぶ。**どの区間も下位の TCP/SMB
        /// タイムアウトまで戻ってこない**ため、区間ごとに上限を設けても塞ぎ残しが生じる。
        /// 実機ログでは接続オープン〜クエリの区間だけで <b>82.3 秒</b>ブロックし、
        /// 切断トーストが 90 秒以上遅れた（その後の確認は 3ms / 0ms。SMB セッションが
        /// 完全に切れた後は即座に失敗するため、「初回だけ極端に遅い」形になる）。
        /// そのため確認<b>全体</b>に上限を設け、無制限にブロックする区間を残さない。
        /// </para>
        /// <para>
        /// 10 秒とするのは、正常時の疎通確認が数ミリ秒で完了する一方、共有モードの
        /// <c>busy_timeout</c>（15 秒）で他 PC の書き込み完了を待つ場合があり、短すぎると
        /// 「混雑しているだけ」を切断と誤判定するため。誤判定しても次回の確認で
        /// 復旧として扱われるだけでデータへの影響はない。この値により切断検知は最悪でも
        /// <c>SharedModeMonitor.HealthCheckIntervalSeconds</c>（15 秒）＋ 10 秒 = 25 秒に収まる。
        /// </para>
        /// </remarks>
        public const int DatabaseConnectionCheckTimeoutSeconds = 10;

        // --- 管理者ダッシュボード（Issue #1692） ---

        /// <summary>
        /// 貸出からこの日数以上返却されていないカードを「長期未返却（督促対象）」として扱う既定値。
        /// </summary>
        /// <remarks>
        /// 出張・研修などで数日間の貸出が常態のため 7 日では誤検知が多く督促リストとして機能しない。
        /// 2 週間を超える貸出は返し忘れがほぼ確実であるため 14 日を既定とする。
        /// 運用差を吸収できるよう管理者ダッシュボード画面上で
        /// <see cref="LongTermUnreturnedDayOptions"/> から切り替えられる。
        /// </remarks>
        public const int LongTermUnreturnedDays = 14;

        /// <summary>
        /// 長期未返却しきい値として画面上で選択できる日数の選択肢。
        /// </summary>
        /// <remarks>
        /// 恒久設定（<c>AppSettings</c>）には持たせず画面上の表示フィルタに留めている。
        /// 設定項目化は settings テーブル・設定画面・移行処理へ波及するため別 Issue とする。
        /// </remarks>
        public static readonly int[] LongTermUnreturnedDayOptions = { 7, 14, 30 };

        /// <summary>
        /// 管理者ダッシュボードの利用分析における既定の集計期間（か月）。
        /// </summary>
        /// <remarks>
        /// 台帳は 6 年保持されるが、既定で全期間を集計すると初回表示が重くなるうえ
        /// 「いま何枚必要か」の判断には直近 1 年で足りるため 12 か月を既定とする。
        /// </remarks>
        public const int AdminDashboardDefaultMonths = 12;

        /// <summary>
        /// 管理者ダッシュボードのグラフに同時に描画する系列数の上限。
        /// </summary>
        /// <remarks>
        /// これを超える系列は「その他」に集約する。色数を増やすと色覚多様性への配慮
        /// （色相差の確保）が破綻し、凡例も読み取れなくなるため。
        /// </remarks>
        public const int AdminDashboardMaxSeries = 5;

        /// <summary>
        /// 管理者ダッシュボードの稼働状況グラフに表示するカード数の上限（稼働率の低い順）。
        /// </summary>
        public const int AdminDashboardUtilizationChartMaxCards = 15;
    }
}
