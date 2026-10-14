#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Timing;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// バックアップ先・データベース保存先の変更と、その操作ログの記録（Issue #2264）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 設定画面（F5）は職員証の認証なしで開け、以前は保存にも認証が無く、変更が操作ログに残らなかった。
    /// バックアップ先は共有 DB に保存され、全 PC が起動のたびに DB 全体をその場所へコピーするため、
    /// 別のサーバーの共有フォルダーを指定されると DB の内容が持ち出され、NTLM 認証が試みられ続ける。
    /// データベース保存先（PC ごとの <c>database_config.txt</c>）は、その PC の以後の記録を別の DB へそらせる。
    /// </para>
    /// <para>
    /// 本サービスは変更と操作ログを 1 つにまとめて確定させる。認証は呼び出し元（<c>SettingsViewModel</c>）が行い、
    /// 操作ログの操作者は認証のスコープから <see cref="OperationLogger"/> が解決する。
    /// </para>
    /// </remarks>
    public class SettingsChangeService : ISettingsChangeService
    {
        private readonly DbContext _dbContext;
        private readonly ISettingsRepository _settingsRepository;
        private readonly OperationLogger _operationLogger;
        private readonly StaffManagementService _staffManagementService;
        private readonly ISystemClock _clock;
        private readonly ILogger<SettingsChangeService> _logger;

        public SettingsChangeService(
            DbContext dbContext,
            ISettingsRepository settingsRepository,
            OperationLogger operationLogger,
            StaffManagementService staffManagementService,
            ISystemClock clock,
            ILogger<SettingsChangeService> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
            // 既定値を持たない必須引数にする（#1820 / #2151 と同じ判断）
            _operationLogger = operationLogger ?? throw new ArgumentNullException(nameof(operationLogger));
            _staffManagementService = staffManagementService ?? throw new ArgumentNullException(nameof(staffManagementService));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc/>
        public Task<bool> RequiresOperatorAuthenticationAsync()
            => _staffManagementService.RequiresOperatorAuthenticationAsync();

        /// <inheritdoc/>
        public async Task<bool> ChangeBackupPathAsync(string backupPath)
        {
            var newValue = backupPath ?? string.Empty;
            var machineName = Environment.MachineName;
            var beforeValue = string.Empty;

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "バックアップ先の変更",
                    async tx =>
                    {
                        // 変更前は同じトランザクションで DB から読む（画面が読み込んだ値は他 PC の変更より古い）
                        beforeValue = await _settingsRepository.GetAsync(SettingsRepository.KeyBackupPath, tx)
                            .ConfigureAwait(false) ?? string.Empty;
                        if (string.Equals(beforeValue, newValue, StringComparison.Ordinal))
                        {
                            // 起きていない変更を記録しない（AuditedWriteTransaction は監査ログを書かずに閉じる）
                            return false;
                        }

                        await _settingsRepository.ChangeBackupPathAsync(newValue, machineName, _clock.Now, tx)
                            .ConfigureAwait(false);
                        return true;
                    },
                    changed => changed,
                    tx => _operationLogger.LogSettingChangeAsync(
                        OperationLogger.SettingNames.BackupPath,
                        DescribeBackupPath(beforeValue),
                        DescribeBackupPath(newValue),
                        tx)).ConfigureAwait(false);
            }
            finally
            {
                // トランザクションを渡した書き込みはキャッシュを破棄しない。コミット・ロールバックの後で破棄する
                _settingsRepository.InvalidateCache();
            }
        }

        /// <inheritdoc/>
        public async Task<bool> ChangeDatabaseConfigAsync(string configFilePath, string? databaseFilePath)
        {
            var deleting = databaseFilePath == null;
            var existedBefore = File.Exists(configFilePath);
            // 元に戻すために書き換え前の内容をバイト列のまま控える（文字コードの判定を経由させない）。
            // 変更の有無を判定する前に読む: AppConfigFiles.Read は読めない（ロック・権限）ときも空文字を返すため、
            // 先に判定すると、読めないファイルを「空＝既定と同じ」とみなして何もせず成功を返してしまう。
            // 読めなければここで例外になり、呼び出し元が失敗を案内する
            var originalBytes = existedBefore ? File.ReadAllBytes(configFilePath) : null;
            var beforeContent = AppConfigFiles.Read(configFilePath);
            var afterContent = databaseFilePath ?? string.Empty;

            var unchanged = deleting
                ? !existedBefore
                : existedBefore && string.Equals(beforeContent, afterContent, StringComparison.Ordinal);
            if (unchanged)
            {
                return false;
            }

            var fileChanged = false;

            try
            {
                await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "データベース保存先の変更",
                    _ =>
                    {
                        // 設定ファイルはトランザクションの中（コミットの前）で書き換える。
                        // 書き換えに失敗したら操作ログを書かずに巻き戻る（起きていない変更を記録しない）
                        if (deleting)
                        {
                            File.Delete(configFilePath);
                        }
                        else
                        {
                            AppConfigFiles.Write(configFilePath, afterContent);
                        }
                        fileChanged = true;
                        return Task.FromResult(true);
                    },
                    changed => changed,
                    tx => _operationLogger.LogSettingChangeAsync(
                        OperationLogger.SettingNames.DatabasePath,
                        DescribeDatabaseConfig(beforeContent),
                        DescribeDatabaseConfig(afterContent),
                        tx)).ConfigureAwait(false);
                return true;
            }
            catch
            {
                // 操作ログの記録・コミットに失敗した。記録の無い変更を残さないよう、設定ファイルを元に戻す
                if (fileChanged)
                {
                    RestoreConfigFile(configFilePath, originalBytes);
                }
                throw;
            }
        }

        /// <summary>
        /// 操作ログに記録するバックアップ先の表記（空・未設定は既定の場所を補って書く）
        /// </summary>
        /// <remarks>
        /// 空のまま記録すると、操作ログが「値: →\\server\backup」のように変更前が抜けて読める。
        /// DB に保存する値は空のまま（既定の場所の解決は <see cref="BackupService"/> が行う）。
        /// </remarks>
        internal static string DescribeBackupPath(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? $"既定のバックアップ先（{PathValidator.GetDefaultBackupPath()}）"
                : value;
        }

        /// <summary>
        /// 操作ログに記録するデータベース保存先の表記（空は既定の場所を補って書く）
        /// </summary>
        internal static string DescribeDatabaseConfig(string content)
        {
            return string.IsNullOrWhiteSpace(content)
                ? $"既定の保存先（{Path.Combine(AppDataPaths.RootDirectory, DbContext.DatabaseFileName)}）"
                : content;
        }

        private void RestoreConfigFile(string configFilePath, byte[]? originalBytes)
        {
            try
            {
                if (originalBytes != null)
                {
                    File.WriteAllBytes(configFilePath, originalBytes);
                }
                else if (File.Exists(configFilePath))
                {
                    File.Delete(configFilePath);
                }
            }
            catch (Exception ex)
            {
                // 戻せなかった: 操作ログに残っていない保存先の変更が、再起動後に効いてしまう。痕跡を Error で残す
                _logger.LogError(
                    ex,
                    "データベース保存先の変更を操作ログに記録できなかったため設定ファイルを元に戻そうとしましたが、戻せませんでした: {Path}",
                    configFilePath);
            }
        }
    }
}
