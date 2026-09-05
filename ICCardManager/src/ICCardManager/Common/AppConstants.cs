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

        /// <summary>
        /// 返却時の同行者数入力ダイアログを「外0名」として自動的に閉じるまでの秒数（Issue #2009）。
        /// 0 は「自動的に閉じない（必ず入力を待つ）」を意味する。
        /// </summary>
        public const int DefaultCompanionCountInputTimeoutSeconds = 30;

        /// <summary>
        /// 同行者数入力の自動クローズ秒数として設定できる下限（0 を除く）。
        /// ダイアログが描画される前に閉じてしまわない値にする。
        /// </summary>
        public const int MinCompanionCountInputTimeoutSeconds = 5;

        /// <summary>
        /// 同行者数入力の自動クローズ秒数として設定できる上限（5 分）。
        /// これを超える値は「自動的に閉じない」（0）と実質同じで、設定の意図が読めなくなる。
        /// </summary>
        public const int MaxCompanionCountInputTimeoutSeconds = 300;

        // --- バックアップ健全性（Issue #1689） ---

        /// <summary>
        /// 自動バックアップを保持する日数（Issue #1813）。
        /// 世代削除は「バックアップのある日」を新しい順にこの日数だけ残し、各日は最新の1世代だけを残す。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「起動1回＝1世代」で数えてはならない。<c>StartupTaskRunner</c> は起動のたびに無条件で
        /// 自動バックアップを実行し、保存先は共有フォルダーになり得るため、最大20台運用では
        /// 1日あたり20世代前後が生まれる。ファイル数で30を上限にすると実効保持期間は約1.5日にしかならず、
        /// 「金曜に混入した破損に月曜気付く」運用で遡れる世代がすべて破損後のものに入れ替わっていた。
        /// </para>
        /// <para>
        /// 日単位へ切り替えることで、単一PCでも共有モードでも、また1日に何回起動しても
        /// 実効保持期間は同じ「直近30日分」になる。ファイル数（＝ディスク使用量）も従来と同水準に収まる。
        /// </para>
        /// <para>
        /// 「直近30日」は暦日ではなく<b>バックアップが存在する日</b>を新しい順に30日分である。
        /// 長期休暇などで起動しなかった期間があっても、最後の30稼働日分が残る。
        /// </para>
        /// </remarks>
        public const int BackupRetentionDays = 30;

        /// <summary>
        /// 手動バックアップ・リストア前バックアップの保持件数の上限（Issue #1813）。
        /// </summary>
        /// <remarks>
        /// これらは職員が明示的に作ったもの（<c>backup_manual_*</c>）、あるいはリストア直前の
        /// 唯一の退避（<c>backup_pre_restore_*</c>）であり、自動バックアップと同じ「日ごとに1世代」で
        /// 間引くと、リストア→再起動→自動バックアップの流れで同日中に消えてしまう。
        /// そのため日単位の間引きの対象外とし、件数だけを上限で抑える。
        /// <para>
        /// この件数上限は「<c>backup_yyyyMMdd_HHmmss.db</c> に完全一致しない <c>backup_*.db</c>」の
        /// <b>すべて</b>に掛かる。手動・リストア前バックアップだけでなく、管理者が手で付けた名前
        /// （例: <c>backup_2026年度上期.db</c>）も同じ枠を共有し、古い側から削除される。
        /// 長期保管したいファイルはバックアップ保存先フォルダーの外へ退避すること
        /// （管理者マニュアル 付録 A / ユーザーマニュアル §7.2 に同じ案内がある）。
        /// </para>
        /// </remarks>
        public const int MaxManualBackupGenerations = 10;

        /// <summary>
        /// 最終バックアップ成功からこの日数を超えて経過した場合、システム警告を表示する。
        /// 起動頻度に依存しない「最終成功からの経過日数」で判定することで、
        /// 長期休暇などでアプリを起動しなかった期間も検知できる。
        /// </summary>
        public const int BackupStaleWarningDays = 7;

        /// <summary>
        /// 繰越情報消失警告（Issue #1758）でカード名を列挙する最大枚数
        /// </summary>
        /// <remarks>
        /// 全部並べると文字サイズ「特大」で警告エリアが何行にも折り返し、他の警告を押し出す。
        /// 一方で件数だけでは「自分の担当カードが含まれるか」を判断できないため、先頭は名前で示し
        /// 残りは「ほか○枚」で補う。
        /// </remarks>
        public const int CarryoverDataLossWarningMaxListedCards = 3;

        /// <summary>
        /// 中断されたバックアップの一時ファイルを削除するまでの経過時間（時間、Issue #1748）。
        /// </summary>
        /// <remarks>
        /// 一時ファイルは失敗時に <c>BackupService</c> 自身が削除するが、コピー中にプロセスが
        /// 強制終了した場合（電源断・タスクマネージャーからの終了）は削除経路を通らずに残る。
        /// DB 本体と同じサイズのファイルが溜まると保存先を圧迫するため定期的に掃除する。
        /// <para>
        /// 「十分に古いものだけ」に限るのは、共有モードで**他 PC が書き込み中の一時ファイル**を
        /// 消さないため。1 回のバックアップは長くても分オーダーで終わるので、24 時間あれば
        /// 進行中のものを誤削除する余地はない。
        /// </para>
        /// </remarks>
        public const int BackupTempFileStaleHours = 24;

        // --- 接続診断（Issue #1690） ---

        /// <summary>
        /// バックアップ保存先の空き容量がこの値を下回った場合、接続診断で警告する（バイト）。
        /// </summary>
        /// <remarks>
        /// DB 本体は数十 MB 規模だが、保持世代（自動は <see cref="BackupRetentionDays"/> 日分、
        /// 手動は <see cref="MaxManualBackupGenerations"/> 件）分の
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
        /// 管理者ダッシュボードのグラフが既定で扱う系列数の上限。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 月別利用額グラフでは「これを超える系列を『その他』に集約する境界」として働く。
        /// 色数を増やすと色覚多様性への配慮（色相差の確保）が破綻し、凡例も読み取れなくなるため。
        /// </para>
        /// <para>
        /// 残高推移グラフでは「初期状態でチェックを入れるカードの枚数」として働く（根拠は同じく色数）。
        /// <b>描画の上限ではない</b> — 利用者が明示的にチェックしたカードはこの枚数を超えても
        /// すべて描く（Issue #1921）。
        /// </para>
        /// </remarks>
        public const int AdminDashboardMaxSeries = 5;

        /// <summary>
        /// 管理者ダッシュボードの稼働状況グラフに表示するカード数の上限（稼働率の低い順）。
        /// </summary>
        public const int AdminDashboardUtilizationChartMaxCards = 15;

        /// <summary>
        /// 残高チェーンの開始点（シード）を求めるために遡る「稼働日」の上限（Issue #1999）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 「その日の最終残高」は、同額のポイント還元と利用で残高が循環する日（Issue #1004 形状）だと
        /// 当日の行だけからは確定できず、前日以前の最終残高をシードとして必要とする。その前日自身も
        /// 同日統合（Issue #837）で id 順と時系列が食い違っていれば <c>ORDER BY … id DESC LIMIT 1</c> では
        /// 求まらないため、前日もチェーン解決する。前々日以降も同じことが起こり得るので上限を設ける。
        /// </para>
        /// <para>
        /// 遡るのは <see cref="Services.LedgerOrderHelper.RequiresSeed"/> が真である間だけなので、
        /// 通常のデータでは 1 日分（1 クエリ）で止まる。上限に達した場合は、それまでに積んだ日を
        /// シード無しで古い順に解決する（最も古い日だけが除外法・id 順フォールバックに委ねられる形で、
        /// 1 行だけを id 順で取っていた従来より悪化することはない）。
        /// </para>
        /// </remarks>
        public const int MaxBalanceChainSeedLookbackDays = 5;

        /// <summary>
        /// 二重起動防止に用いる名前付きミューテックスの名前（Issue #1910）。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Global\</c> 接頭辞を付けて<b>端末全体</b>で一意にする。
        /// <c>Local\</c>（既定）はターミナルサービスのセッションごとに別物になるため、
        /// ユーザーの簡易切り替えで 2 つのピッすいが同時に 1 台のカードリーダーを
        /// 取り合う形が残ってしまう。
        /// </para>
        /// <para>
        /// <b>インストール先のパスやデータベースのパスを名前に含めない。</b>
        /// 含めると「別フォルダーへインストールした 2 つ目のピッすい」や
        /// 「別データベースを見る 2 つ目のピッすい」が同時に起動でき、
        /// 1 台しかないカードリーダーを取り合うという本 Issue の欠陥がそのまま残る。
        /// 排他したい資源は台帳ではなくカードリーダー（＝端末）である。
        /// </para>
        /// <para>
        /// 末尾の GUID は他社製ソフトウェアとの名前衝突を避けるためのもので、意味は持たない。
        /// <b>変更すると、変更前のバージョンとの間で二重起動できてしまう</b>ため固定する。
        /// </para>
        /// </remarks>
        public const string SingleInstanceMutexName =
            @"Global\ICCardManager-SingleInstance-{7B4C1B2E-3F5A-4C0D-9E71-2A6D8F0B5C31}";
    }
}
