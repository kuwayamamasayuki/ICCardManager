using System;
using System.Threading.Tasks;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 起動時に一度だけ実行するメンテナンスタスクを順に実行する（Issue #1737）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 実行順は「自動バックアップ → 6年経過データの削除 → 月次 VACUUM」。
    /// <see cref="App"/> から切り出したのは、この順序と直列性が
    /// <see cref="DbContext"/> の単一接続制約に依存しており、単体テストで固定する必要があるため。
    /// </para>
    /// <para>
    /// <b>重要（Issue #1737）— 各タスクは必ず直列 await すること:</b>
    /// <see cref="DbContext"/> は <see cref="System.Data.SQLite.SQLiteConnection"/> を 1 本しか持たない。
    /// いずれかのタスクを fire-and-forget で起動すると、その継続（バックアップなら
    /// 成功日時の記録）が他タスクのトランザクション内側で実行され、
    /// ロールバック時に道連れで消える／VACUUM 実行中の同一接続へコマンドが届いて
    /// "cannot VACUUM - SQL statements in progress" になる。
    /// <see cref="DbContext.LeaseConnectionAsync"/> の注記（Issue #1452）と同じ制約。
    /// </para>
    /// </remarks>
    public class StartupTaskRunner
    {
        private readonly DbContext _dbContext;
        private readonly BackupService _backupService;
        private readonly ISettingsRepository _settingsRepository;
        private readonly IBackupHealthService _backupHealthService;
        private readonly ILogger<StartupTaskRunner> _logger;

        public StartupTaskRunner(
            DbContext dbContext,
            BackupService backupService,
            ISettingsRepository settingsRepository,
            IBackupHealthService backupHealthService,
            ILogger<StartupTaskRunner> logger)
        {
            _dbContext = dbContext;
            _backupService = backupService;
            _settingsRepository = settingsRepository;
            _backupHealthService = backupHealthService;
            _logger = logger;
        }

        /// <summary>
        /// 起動時タスクを実行する。
        /// </summary>
        /// <param name="today">実行日。月次 VACUUM の実施判定（10日以降）に使用する</param>
        /// <remarks>
        /// 個々のタスクの失敗で起動を止めない（catch して Error ログのみ）。
        /// </remarks>
        public async Task RunAsync(DateTime today)
        {
            try
            {
                // 自動バックアップ（Issue #1737: 必ず完了を待つ）
                // 戻り値を捨てる fire-and-forget にすると、バックアップ末尾の
                // 「成功日時・実施PC名を settings へ記録する INSERT」（Issue #1689）が
                // 後続タスクと同一接続上で並走する。バックアップ本体（BackupDatabaseTo）は
                // 同期 LeaseConnection() でセマフォを取るため後続タスクはどのみち待たされており、
                // await 化で増える待ち時間は「古い世代の削除（ファイル走査）＋ INSERT 2 件」分のみ。
                //
                // バックアップの失敗で後続の保守タスクを巻き添えにしないよう、
                // ここだけは個別に catch する（fire-and-forget 時代の性質を維持する）。
                try
                {
                    await _backupService.ExecuteAutoBackupAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "起動時の自動バックアップでエラー");
                }

                // 古いデータの削除（6年経過分）
                var (ledgerDeleted, logDeleted) = await _dbContext.CleanupOldDataAsync().ConfigureAwait(false);
                if (ledgerDeleted > 0)
                {
                    _logger.LogInformation("古い利用履歴を{DeletedCount}件削除しました", ledgerDeleted);
                }
                if (logDeleted > 0)
                {
                    _logger.LogInformation("古い操作ログを{DeletedCount}件削除しました", logDeleted);
                }

                // VACUUM（月次実行、先勝ち CAS ロック、Issue #1482）
                // 共有モードで複数 PC が同時に起動した場合、ロック獲得した 1 台のみが
                // VACUUM を試行する。ロック獲得後の VACUUM 失敗は当月スキップとして確定し、
                // 来月まで誰も再試行しない（デッドロックスパイラル防止）。
                if (today.Day >= MonthlyVacuumStartDay)
                {
                    if (await _settingsRepository.TryAcquireMonthlyVacuumLockAsync(today).ConfigureAwait(false))
                    {
                        if (await _dbContext.VacuumAsync().ConfigureAwait(false))
                        {
                            _logger.LogInformation("VACUUM実行完了");

                            // Issue #1689: 共有モードでは複数PCのうち1台だけがVACUUMを実施するため、
                            // どのPCが実施したかを記録してシステム管理画面から追跡できるようにする
                            await _backupHealthService.RecordVacuumMachineAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogWarning("VACUUM失敗。来月再試行します。");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "起動時タスクでエラー");
            }
        }

        /// <summary>
        /// 月次 VACUUM を試行し始める日（Issue #1482）。
        /// 月初は前月分の帳票作成が集中するため、10日以降に実施する。
        /// </summary>
        internal const int MonthlyVacuumStartDay = 10;
    }
}
