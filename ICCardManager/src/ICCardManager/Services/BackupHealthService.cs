using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// バックアップ健全性の取得・記録を担当するサービス（Issue #1689）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 起動時自動バックアップは結果を画面へ出さず、失敗してもログに残るだけである。
    /// 6年保存の監査対応という文脈では「静かな失敗」が最大のリスクになるため、
    /// 最終成功日時・世代数・空き容量を可視化し、長期間成功していない場合に警告を出す。
    /// </para>
    /// <para>
    /// 記録側（<see cref="BackupService"/> の成功時保存）と参照側（本サービス）を分離しているのは、
    /// 本サービスが <see cref="BackupService"/> に依存する一方向の関係を保つため。
    /// </para>
    /// </remarks>
    public class BackupHealthService : IBackupHealthService
    {
        private readonly BackupService _backupService;
        private readonly ISettingsRepository _settingsRepository;
        private readonly ILogger<BackupHealthService> _logger;

        public BackupHealthService(
            BackupService backupService,
            ISettingsRepository settingsRepository,
            ILogger<BackupHealthService> logger)
        {
            _backupService = backupService;
            _settingsRepository = settingsRepository;
            _logger = logger;
        }

        /// <inheritdoc/>
        public async Task<BackupHealthInfo> GetHealthAsync()
        {
            var info = new BackupHealthInfo
            {
                MaxGenerations = AppConstants.MaxBackupGenerations,
                IsSharedMode = _backupService.IsSharedMode
            };

            // 各項目は独立して失敗し得る（フォルダ不達・権限不足など）。
            // 1項目の失敗で健全性表示全体が落ちないよう、項目ごとに握りつぶして続行する。
            info.LastSuccessAt = await TryGetDateTimeSettingAsync(SettingsRepository.KeyLastBackupSuccessAt)
                .ConfigureAwait(false);
            info.LastSuccessMachineName = await TryGetStringSettingAsync(SettingsRepository.KeyLastBackupMachine)
                .ConfigureAwait(false);
            info.LastVacuumDate = await TryGetDateTimeSettingAsync(SettingsRepository.KeyLastVacuumDate)
                .ConfigureAwait(false);
            info.LastVacuumMachineName = await TryGetStringSettingAsync(SettingsRepository.KeyLastVacuumMachine)
                .ConfigureAwait(false);

            try
            {
                info.BackupFolderPath = await _backupService.ResolveBackupFolderAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "バックアップ保存先の解決に失敗しました");
            }

            try
            {
                var files = await _backupService.GetBackupFilesAsync().ConfigureAwait(false);
                info.GenerationCount = files?.Count() ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "バックアップ世代数の取得に失敗しました: {Path}", info.BackupFolderPath);
            }

            info.FreeSpaceBytes = DiskSpaceHelper.TryGetAvailableFreeSpace(info.BackupFolderPath);

            return info;
        }

        /// <inheritdoc/>
        public async Task RecordVacuumMachineAsync()
        {
            try
            {
                await _settingsRepository.SetAsync(
                    SettingsRepository.KeyLastVacuumMachine,
                    Environment.MachineName).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VACUUM実施PC名の記録に失敗しました（VACUUM自体は完了しています）");
            }
        }

        private async Task<string> TryGetStringSettingAsync(string key)
        {
            try
            {
                return await _settingsRepository.GetAsync(key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "設定値の取得に失敗しました: {Key}", key);
                return null;
            }
        }

        private async Task<DateTime?> TryGetDateTimeSettingAsync(string key)
        {
            var raw = await TryGetStringSettingAsync(key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // last_backup_success_at は "yyyy-MM-dd HH:mm:ss"、last_vacuum_date は "yyyy-MM-dd" と
            // 精度が異なるため、書式を固定せず一般的な解析に委ねる。
            // 解析できない値（手動編集・破損）は「記録なし」として扱い、例外にしない。
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return parsed;

            _logger.LogWarning("設定値を日時として解釈できませんでした: {Key}={Value}", key, raw);
            return null;
        }
    }
}
