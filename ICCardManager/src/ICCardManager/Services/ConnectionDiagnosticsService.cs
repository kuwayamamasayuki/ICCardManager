using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Timing;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// アプリが依存する外部リソースの状態を一括診断するサービス（Issue #1690）
    /// </summary>
    /// <remarks>
    /// <para>
    /// インターネット非接続の官公庁環境では IT 担当が物理的に遠く、障害時の切り分けが
    /// 電話越しになりコストが高い。本サービスは庶務担当が自力で原因を切り分けられるよう、
    /// DB・ICカードリーダー・バックアップ保存先・ディスクの状態をまとめて確認し、
    /// 「何が・なぜ・どうすれば」の 3 要素で提示する。
    /// </para>
    /// <para>
    /// 各項目は独立して失敗し得る（ネットワーク断・権限不足・デバイス未接続）。
    /// 1 項目の例外で診断そのものが使えなくなっては本末転倒なので、
    /// 項目ごとに例外を捕捉して「その項目だけ異常」として続行する
    /// （<see cref="BackupHealthService"/> と同じ方針）。
    /// </para>
    /// <para>
    /// 診断は読み取り専用である。唯一の副作用はバックアップ保存先への一時ファイル作成だが、
    /// <see cref="FolderWriteAccessProbe"/> が OS の削除保証付きで作成・破棄する。
    /// </para>
    /// </remarks>
    public class ConnectionDiagnosticsService : IConnectionDiagnosticsService
    {
        private readonly IDatabaseInfo _databaseInfo;
        private readonly ICardReader _cardReader;
        private readonly BackupService _backupService;
        private readonly IBackupHealthService _backupHealthService;
        private readonly ISharedDbConnectionStateProvider _connectionStateProvider;
        private readonly ISystemClock _clock;
        private readonly ILogger<ConnectionDiagnosticsService> _logger;

        public ConnectionDiagnosticsService(
            IDatabaseInfo databaseInfo,
            ICardReader cardReader,
            BackupService backupService,
            IBackupHealthService backupHealthService,
            ISharedDbConnectionStateProvider connectionStateProvider,
            ISystemClock clock,
            ILogger<ConnectionDiagnosticsService> logger)
        {
            _databaseInfo = databaseInfo;
            _cardReader = cardReader;
            _backupService = backupService;
            _backupHealthService = backupHealthService;
            _connectionStateProvider = connectionStateProvider;
            _clock = clock;
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task<DiagnosticReport> RunDiagnosticsAsync()
        {
            var items = new List<DiagnosticItem>();

            // 到達できない DB に対して書込確認まで走らせると、同じ原因で 2 件の異常が出て
            // 「原因が 2 つある」と誤解させる。到達性の結果を後段へ引き継ぐ。
            var isReachable = AddItem(items,
                DiagnosticItemKind.DatabaseReachability, DatabaseReachabilityTitle,
                CheckDatabaseReachability);

            AddItem(items,
                DiagnosticItemKind.DatabaseWritable, DatabaseWritableTitle,
                () => CheckDatabaseWritable(isReachable));

            AddItem(items,
                DiagnosticItemKind.JournalMode, JournalModeTitle,
                CheckJournalMode);

            AddItem(items,
                DiagnosticItemKind.SharedFolderConnection, SharedFolderConnectionTitle,
                () => CheckSharedFolderConnection(isReachable));

            AddItem(items,
                DiagnosticItemKind.CardReader, CardReaderTitle,
                CheckCardReader);

            // 保存先の解決は項目 6・7 で共有する。解決自体が失敗した場合は
            // null を引き継ぎ、両項目がそれぞれの文言で「特定できない」と報告する。
            var backupFolder = await ResolveBackupFolderOrNullAsync().ConfigureAwait(false);

            AddItem(items,
                DiagnosticItemKind.BackupFolderWritable, BackupFolderWritableTitle,
                () => CheckBackupFolderWritable(backupFolder));

            AddItem(items,
                DiagnosticItemKind.DiskFreeSpace, DiskFreeSpaceTitle,
                () => CheckDiskFreeSpace(backupFolder));

            items.Add(await CheckBackupHealthAsync().ConfigureAwait(false));

            return new DiagnosticReport
            {
                DiagnosedAt = _clock.Now,
                Items = items,
                AppVersion = AppVersionInfo.CurrentString,
                MachineName = Environment.MachineName,
                OsDescription = Environment.OSVersion.VersionString,
                DatabasePath = SafePath(() => _databaseInfo.DatabasePath),
                IsSharedMode = SafeBool(() => _databaseInfo.IsSharedMode)
            };
        }

        #region 項目 1・2: データベース

        private const string DatabaseReachabilityTitle = "データベース到達性";
        private const string DatabaseWritableTitle = "データベース書込権限";

        /// <summary>
        /// データベースファイルそのものへ到達できるかを実測する
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="IDatabaseInfo.CheckConnection"/> だけでは切断を検知できなかった。
        /// <c>DbContext</c> は接続を開きっぱなしで保持しており、疎通確認に使う
        /// <c>SELECT COUNT(*) FROM sqlite_master</c> は SQLite のページキャッシュから
        /// 応答され得るため、ネットワークが切れた後も成功し続けることがある
        /// （実機で確認。書き込みだけが失敗し「到達性 正常／書込権限 異常」という
        /// 誤った組み合わせが表示された）。
        /// </para>
        /// <para>
        /// そのためファイルシステムへ実際に問い合わせ、SMB のラウンドトリップを強制する。
        /// 到達できない理由（切断・削除・権限）は区別せず、いずれも「到達できない」として扱う。
        /// テストから差し替えられるよう <c>protected virtual</c> の継ぎ目にしている。
        /// </para>
        /// <para>
        /// Issue #1716 で <c>DbContext.CheckConnection</c> 自体が同じファイル到達確認を行うようになったが、
        /// 本プローブは defense-in-depth として残す。
        /// <see cref="IDatabaseInfo"/> は interface であり、<c>DbContext</c> 以外の実装
        /// （将来の別実装やテストダブル）が到達確認を伴う保証はないため、
        /// 診断結果の正しさを上流実装に依存させない。
        /// </para>
        /// </remarks>
        protected virtual bool ProbeDatabaseFileReachable(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                return false;

            // File.Exists は例外を投げず、到達不能・権限なしのいずれでも false を返す
            return File.Exists(databasePath);
        }

        private DiagnosticItem CheckDatabaseReachability()
        {
            var path = SafePath(() => _databaseInfo.DatabasePath);

            // クエリが通ることと、ファイルへ実際に届くことの両方を要求する。
            // 前者だけではキャッシュ応答で切断を見逃す（ProbeDatabaseFileReachable の remarks 参照）。
            if (_databaseInfo.CheckConnection() && ProbeDatabaseFileReachable(path))
            {
                return Ok(DiagnosticItemKind.DatabaseReachability, DatabaseReachabilityTitle,
                    "接続できます",
                    $"データベース（{path}）を読み取れています。");
            }

            // 共有モードとローカルモードでは原因も対処も異なるため文言を分ける。
            var detail = SafeBool(() => _databaseInfo.IsSharedMode)
                ? $"データベース（{path}）に接続できません。" +
                  "共有フォルダへのネットワーク接続が切れているか、共有フォルダ名やアクセス権が変更された可能性があります。" +
                  "ネットワーク接続と共有フォルダの状態を確認し、改善しない場合はこの診断結果をIT担当へ送ってください。"
                : $"データベース（{path}）に接続できません。" +
                  "ファイルが削除・移動されたか、ディスクに障害が発生している可能性があります。" +
                  "バックアップからの復元が必要になるため、この診断結果をIT担当へ送ってください。";

            return Error(DiagnosticItemKind.DatabaseReachability, DatabaseReachabilityTitle,
                "接続できません", detail);
        }

        private DiagnosticItem CheckDatabaseWritable(bool isReachable)
        {
            if (!isReachable)
            {
                return NotApplicable(DiagnosticItemKind.DatabaseWritable, DatabaseWritableTitle,
                    "未確認",
                    "データベースへ到達できないため、書き込みの確認は行っていません。" +
                    "先に「データベース到達性」の対処を行ってください。");
            }

            var path = SafePath(() => _databaseInfo.DatabasePath);

            if (_databaseInfo.CheckWritable())
            {
                return Ok(DiagnosticItemKind.DatabaseWritable, DatabaseWritableTitle,
                    "書き込めます",
                    $"データベース（{path}）へ書き込めています。");
            }

            // 共有モードでは「ネットワークが切れかけている」が最も起こりやすい原因だが、
            // 到達性の確認がキャッシュ応答で通ってしまう場合にここへ来る。
            // 原因候補からネットワークを落とすと、電源が落ちた相手PCを疑えず
            // アクセス権の調査へ誤誘導するため、共有モードでは必ず含める。
            var detail = SafeBool(() => _databaseInfo.IsSharedMode)
                ? $"データベース（{path}）へ書き込めません。" +
                  "共有フォルダへのネットワーク接続が切れかけているか、" +
                  "ファイルまたはフォルダのアクセス権が読み取り専用になっているか、" +
                  "他のパソコンが復元処理中でファイルを占有している可能性があります。" +
                  "共有フォルダのあるパソコンが起動しているかを確認し、" +
                  "フォルダの「変更」権限があるかIT担当に確認してください。"
                : $"データベース（{path}）へ書き込めません。" +
                  "ファイルの「読み取り専用」属性が付いているか、フォルダのアクセス権が不足しているか、" +
                  "ほかのプログラムがファイルを開いている可能性があります。" +
                  "ファイルのプロパティで「読み取り専用」を外し、フォルダの「変更」権限があるか確認してください。";

            return Error(DiagnosticItemKind.DatabaseWritable, DatabaseWritableTitle,
                "書き込めません", detail);
        }

        #endregion

        #region 項目 3: ジャーナルモード

        private const string JournalModeTitle = "ジャーナルモード";

        private DiagnosticItem CheckJournalMode()
        {
            var mode = _databaseInfo.CurrentJournalMode;
            var modeText = string.IsNullOrWhiteSpace(mode) ? "不明" : mode;

            if (!_databaseInfo.IsJournalModeDegraded)
            {
                return Ok(DiagnosticItemKind.JournalMode, JournalModeTitle,
                    $"{modeText}（正常）",
                    $"ジャーナルモードは {modeText} で、停電などに強い設定で動作しています。");
            }

            // 「今すぐ使えない」わけではないため異常ではなく警告とする。
            return Warning(DiagnosticItemKind.JournalMode, JournalModeTitle,
                $"{modeText}（低下）",
                $"ジャーナルモードが {modeText} になっており、本来の DELETE ではありません。" +
                "停電や強制終了が起きたときにデータが壊れる危険が通常より高い状態です。" +
                "すべてのパソコンでこのアプリを終了してから、もう一度起動し直してください。");
        }

        #endregion

        #region 項目 4: 共有フォルダ接続状態

        private const string SharedFolderConnectionTitle = "共有フォルダ接続状態";

        /// <summary>
        /// 接続監視の実行間隔（秒）。文言で「どれくらい古い情報か」を示すために参照する
        /// </summary>
        /// <remarks>
        /// <see cref="SharedModeMonitor.HealthCheckIntervalSeconds"/> を単一の出典とし、
        /// 監視間隔を変えたときに診断の文言だけが取り残されないようにする。
        /// </remarks>
        private const int MonitoringIntervalSeconds = SharedModeMonitor.HealthCheckIntervalSeconds;

        /// <summary>
        /// 共有フォルダの接続状態を診断する
        /// </summary>
        /// <param name="isReachable">項目1（データベース到達性）の即時確認が成功したか</param>
        /// <remarks>
        /// <para>
        /// 判定元の <see cref="ISharedDbConnectionStateProvider.CurrentConnectionState"/> は
        /// <see cref="SharedModeMonitor"/> の <c>HealthCheckIntervalSeconds</c>（15秒）周期の
        /// ヘルスチェックでしか更新されない。したがって <c>Connected</c> が意味するのは
        /// 「最大15秒前の確認が成功していた」であって「今この瞬間つながっている」ではない。
        /// </para>
        /// <para>
        /// この時間差を無視すると、ネットワークを抜いた直後の診断で
        /// 「項目1: 到達できません（異常）」と「項目4: 正常」が同じ結果に併存し、
        /// 診断ツールとして自己矛盾した出力になる。そのため即時確認の結果
        /// (<paramref name="isReachable"/>) と突き合わせ、監視が追いついていない場合は
        /// 正常と断定せず警告として報告する。
        /// </para>
        /// </remarks>
        private DiagnosticItem CheckSharedFolderConnection(bool isReachable)
        {
            if (!_databaseInfo.IsSharedMode)
            {
                return NotApplicable(DiagnosticItemKind.SharedFolderConnection, SharedFolderConnectionTitle,
                    "対象外（ローカルモード）",
                    "このパソコンは自分のディスク上のデータベースを使用しているため、共有フォルダの接続監視は行っていません。");
            }

            switch (_connectionStateProvider.CurrentConnectionState)
            {
                case SharedDbConnectionState.Connected:
                    // 監視は成功を記録しているが、たった今の確認では到達できなかった場合。
                    // 監視が切断をまだ検知していないだけなので「正常」と断定しない。
                    if (!isReachable)
                    {
                        return Warning(DiagnosticItemKind.SharedFolderConnection, SharedFolderConnectionTitle,
                            "確認中（監視は未検知）",
                            $"共有フォルダの接続監視は直近{MonitoringIntervalSeconds}秒以内の確認で成功を記録していますが、" +
                            "この診断の時点では共有フォルダへ到達できませんでした。" +
                            $"監視は{MonitoringIntervalSeconds}秒ごとに確認するため、切断がまだ反映されていない可能性があります。" +
                            "「データベース到達性」の対処を行ったうえで、しばらく待ってから再診断してください。");
                    }

                    return Ok(DiagnosticItemKind.SharedFolderConnection, SharedFolderConnectionTitle,
                        "接続中",
                        $"直近の接続確認は成功しています（{MonitoringIntervalSeconds}秒ごとに確認しています）。");

                case SharedDbConnectionState.Reconnecting:
                    return Warning(DiagnosticItemKind.SharedFolderConnection, SharedFolderConnectionTitle,
                        "再接続中",
                        "直近の接続確認に失敗し、再接続を試みています。" +
                        "共有フォルダへのネットワークが不安定になっている可能性があります。" +
                        "しばらく待ってから再診断し、改善しない場合はネットワーク管理者へ連絡してください。");

                default:
                    return Error(DiagnosticItemKind.SharedFolderConnection, SharedFolderConnectionTitle,
                        "切断",
                        "共有フォルダへの接続が切断されています。" +
                        "ネットワークケーブルの抜けや、ファイルサーバーの停止が考えられます。" +
                        "ネットワーク接続を復旧させ、復旧後にこのアプリを再起動してください。");
            }
        }

        #endregion

        #region 項目 5: ICカードリーダー

        private const string CardReaderTitle = "ICカードリーダー";

        private DiagnosticItem CheckCardReader()
        {
            switch (_cardReader.ConnectionState)
            {
                case CardReaderConnectionState.Connected:
                    return Ok(DiagnosticItemKind.CardReader, CardReaderTitle,
                        "接続中",
                        "ICカードリーダーが認識されています。");

                case CardReaderConnectionState.Reconnecting:
                    return Warning(DiagnosticItemKind.CardReader, CardReaderTitle,
                        "再接続中",
                        "ICカードリーダーとの通信が途切れ、再接続を試みています。" +
                        "USBケーブルの接触不良や、USBハブの電力不足が考えられます。" +
                        "リーダーをパソコン本体のUSBポートへ直接つなぎ直してください。");

                default:
                    return Error(DiagnosticItemKind.CardReader, CardReaderTitle,
                        "接続されていません",
                        "ICカードリーダーが認識されていません。" +
                        "USBケーブルが抜けているか、ドライバーが停止している可能性があります。" +
                        "リーダーをUSBポートに挿し直し、改善しない場合はパソコンを再起動してください。");
            }
        }

        #endregion

        #region 項目 6・7: バックアップ保存先

        private const string BackupFolderWritableTitle = "バックアップ保存先";
        private const string DiskFreeSpaceTitle = "空きディスク容量";

        private async Task<string> ResolveBackupFolderOrNullAsync()
        {
            try
            {
                return await _backupService.ResolveBackupFolderAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "接続診断: バックアップ保存先の解決に失敗しました");
                return null;
            }
        }

        /// <summary>
        /// バックアップ保存先への書込可否を実測する
        /// </summary>
        /// <remarks>
        /// OS へ実際に問い合わせる部分を切り出したテスト用の継ぎ目。
        /// 「権限がない」「ディスクが満杯」といった状態は単体テストから再現できないため、
        /// 本メソッドを差し替えて判定と文言だけを検証できるようにしている。
        /// </remarks>
        protected virtual FolderWriteAccess ProbeFolderWriteAccess(string folder) =>
            FolderWriteAccessProbe.Probe(folder);

        /// <summary>
        /// バックアップ保存先の空き容量を取得する（テスト用の継ぎ目。<see cref="ProbeFolderWriteAccess"/> と同じ理由）
        /// </summary>
        protected virtual long? GetFreeSpaceBytes(string folder) =>
            DiskSpaceHelper.TryGetAvailableFreeSpace(folder);

        private DiagnosticItem CheckBackupFolderWritable(string folder)
        {
            var access = ProbeFolderWriteAccess(folder);

            switch (access)
            {
                case FolderWriteAccess.Writable:
                    return Ok(DiagnosticItemKind.BackupFolderWritable, BackupFolderWritableTitle,
                        "書き込めます",
                        $"バックアップ保存先（{folder}）へ書き込めています。");

                case FolderWriteAccess.PathNotSpecified:
                    return Error(DiagnosticItemKind.BackupFolderWritable, BackupFolderWritableTitle,
                        "保存先が特定できません",
                        "バックアップ保存先のフォルダを特定できませんでした。" +
                        "設定が壊れているか、保存先の読み取りに失敗しています。" +
                        "設定画面（F5）でバックアップ保存先を設定し直してください。");

                case FolderWriteAccess.FolderNotFound:
                    return Error(DiagnosticItemKind.BackupFolderWritable, BackupFolderWritableTitle,
                        "フォルダが見つかりません",
                        $"バックアップ保存先（{folder}）が見つかりません。" +
                        "フォルダが削除・移動されたか、ネットワーク接続が切れている可能性があります。" +
                        "設定画面（F5）で保存先を確認し、必要ならフォルダを作り直してください。");

                case FolderWriteAccess.AccessDenied:
                    return Error(DiagnosticItemKind.BackupFolderWritable, BackupFolderWritableTitle,
                        "書き込み権限がありません",
                        $"バックアップ保存先（{folder}）へ書き込む権限がありません。" +
                        "フォルダのアクセス権が読み取り専用になっています。" +
                        "このフォルダへの「変更」権限を付けてもらうよう、IT担当へ依頼してください。");

                default:
                    return Error(DiagnosticItemKind.BackupFolderWritable, BackupFolderWritableTitle,
                        "書き込めません",
                        $"バックアップ保存先（{folder}）へ書き込めませんでした。" +
                        "ディスクの空き容量不足や、一時的な入出力エラーが考えられます。" +
                        "空きディスク容量の診断結果を確認し、しばらく待ってから再診断してください。");
            }
        }

        private DiagnosticItem CheckDiskFreeSpace(string folder)
        {
            var freeBytes = GetFreeSpaceBytes(folder);
            var formatted = DiskSpaceHelper.FormatBytes(freeBytes);

            if (freeBytes == null)
            {
                return Warning(DiagnosticItemKind.DiskFreeSpace, DiskFreeSpaceTitle,
                    "不明",
                    "バックアップ保存先の空き容量を取得できませんでした。" +
                    "保存先へアクセスできない状態か、容量を返さない種類の共有フォルダである可能性があります。" +
                    "先に「バックアップ保存先」の診断結果を確認してください。");
            }

            if (freeBytes.Value < AppConstants.DiagnosticsLowDiskSpaceWarningBytes)
            {
                var threshold = DiskSpaceHelper.FormatBytes(AppConstants.DiagnosticsLowDiskSpaceWarningBytes);
                return Warning(DiagnosticItemKind.DiskFreeSpace, DiskFreeSpaceTitle,
                    formatted,
                    $"バックアップ保存先（{folder}）の空き容量が {formatted} しかありません。" +
                    $"バックアップは自動分を直近 {AppConstants.BackupRetentionDays} 日分、" +
                    $"手動分を最新 {AppConstants.MaxManualBackupGenerations} 件まで保持するため、" +
                    "このままではバックアップが失敗するおそれがあります。" +
                    $"不要なファイルを削除して、{threshold} 以上の空きを確保してください。");
            }

            return Ok(DiagnosticItemKind.DiskFreeSpace, DiskFreeSpaceTitle,
                formatted,
                $"バックアップ保存先の空き容量は {formatted} です。");
        }

        #endregion

        #region 項目 8: バックアップ実施状況

        private const string BackupHealthTitle = "バックアップ実施状況";

        private async Task<DiagnosticItem> CheckBackupHealthAsync()
        {
            BackupHealthInfo health;
            try
            {
                health = await _backupHealthService.GetHealthAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "接続診断: バックアップ健全性の取得に失敗しました");
                return Error(DiagnosticItemKind.BackupHealth, BackupHealthTitle,
                    "確認できませんでした",
                    ExceptionMessageFormatter.ToUserMessage(ex, "バックアップ実施状況の確認"));
            }

            var lastSuccess = health?.LastSuccessAt;
            if (lastSuccess == null)
            {
                // 記録がないのは「失敗している」ではなく「判断材料がない」。
                // 導入前からの既存環境で必ず警告が出る状態を避ける（Issue #1689 の方針）。
                return NotApplicable(DiagnosticItemKind.BackupHealth, BackupHealthTitle,
                    "記録なし",
                    "バックアップ成功の記録がまだありません。この機能を追加する前から使用している場合、記録がないのは正常です。");
            }

            var days = health.GetDaysSinceLastSuccess(_clock.Now) ?? 0;
            var lastSuccessText = lastSuccess.Value.ToString("yyyy年M月d日 HH:mm");

            if (days > AppConstants.BackupStaleWarningDays)
            {
                return Warning(DiagnosticItemKind.BackupHealth, BackupHealthTitle,
                    $"{days}日前",
                    $"最後にバックアップが成功したのは {lastSuccessText} で、{days}日が経過しています。" +
                    "保存先へ書き込めない状態が続いているか、長期間このアプリを起動していなかった可能性があります。" +
                    "システム管理画面（F6）で「バックアップを作成」を実行し、成功するか確かめてください。");
            }

            return Ok(DiagnosticItemKind.BackupHealth, BackupHealthTitle,
                $"{days}日前",
                $"最後にバックアップが成功したのは {lastSuccessText} です。");
        }

        #endregion

        #region 共通処理

        /// <summary>
        /// 1 項目を実行し、例外が出た場合は「その項目だけ異常」に変換して続行する
        /// </summary>
        /// <returns>その項目が正常（<see cref="DiagnosticStatus.Ok"/>）だったかどうか</returns>
        private bool AddItem(
            List<DiagnosticItem> items,
            DiagnosticItemKind kind,
            string title,
            Func<DiagnosticItem> check)
        {
            DiagnosticItem item;
            try
            {
                item = check();
            }
            catch (Exception ex)
            {
                // ここに来るのは想定外の失敗のみ（各 Check は既知の失敗を戻り値で表す）。
                // 技術的詳細はログへ逃がし、UI には 3 要素の文言だけを出す（Issue #1614）。
                // どの項目が失敗したのかを利用者が特定できるよう、種別と項目名は保つ。
                _logger.LogWarning(ex, "接続診断の項目実行に失敗しました: {Kind}", kind);
                item = Error(kind, title,
                    "確認できませんでした",
                    ExceptionMessageFormatter.ToUserMessage(ex, title + "の確認"));
            }

            items.Add(item);
            return item.Status == DiagnosticStatus.Ok;
        }

        private static DiagnosticItem Ok(DiagnosticItemKind kind, string title, string summary, string detail) =>
            Build(kind, title, DiagnosticStatus.Ok, summary, detail);

        private static DiagnosticItem Warning(DiagnosticItemKind kind, string title, string summary, string detail) =>
            Build(kind, title, DiagnosticStatus.Warning, summary, detail);

        private static DiagnosticItem Error(DiagnosticItemKind kind, string title, string summary, string detail) =>
            Build(kind, title, DiagnosticStatus.Error, summary, detail);

        private static DiagnosticItem NotApplicable(DiagnosticItemKind kind, string title, string summary, string detail) =>
            Build(kind, title, DiagnosticStatus.NotApplicable, summary, detail);

        private static DiagnosticItem Build(
            DiagnosticItemKind kind, string title, DiagnosticStatus status, string summary, string detail) =>
            new DiagnosticItem
            {
                Kind = kind,
                Title = title,
                Status = status,
                SummaryText = summary,
                DetailText = detail
            };

        /// <summary>
        /// 環境情報の取得は診断の本体ではないため、失敗しても診断結果を壊さない
        /// </summary>
        private string SafePath(Func<string> getter)
        {
            try
            {
                var value = getter();
                return string.IsNullOrWhiteSpace(value) ? "不明" : value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "接続診断: データベースパスの取得に失敗しました");
                return "不明";
            }
        }

        private bool SafeBool(Func<bool> getter)
        {
            try
            {
                return getter();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "接続診断: 動作モードの判定に失敗しました");
                return false;
            }
        }

        #endregion
    }
}
