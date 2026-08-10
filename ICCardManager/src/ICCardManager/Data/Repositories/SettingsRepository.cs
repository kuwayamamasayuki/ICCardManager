using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using Microsoft.Extensions.Options;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 設定リポジトリ実装
    /// </summary>
    public class SettingsRepository : ISettingsRepository
    {
        private readonly DbContext _dbContext;
        private readonly ICacheService _cacheService;
        private readonly CacheOptions _cacheOptions;

        // 設定キー定数
        public const string KeyWarningBalance = "warning_balance";
        public const string KeyBackupPath = "backup_path";
        public const string KeyFontSize = "font_size";
        public const string KeyLastVacuumDate = "last_vacuum_date";

        // バックアップ健全性キー（Issue #1689）
        // settings は key-value テーブルのためスキーマ変更（マイグレーション）は不要。
        /// <summary>最後にバックアップが成功した日時（ISO 8601 "yyyy-MM-dd HH:mm:ss"）</summary>
        public const string KeyLastBackupSuccessAt = "last_backup_success_at";

        /// <summary>最後にバックアップを実施した PC 名</summary>
        public const string KeyLastBackupMachine = "last_backup_machine";

        /// <summary>最後に VACUUM を実行した PC 名（日付は <see cref="KeyLastVacuumDate"/>）</summary>
        public const string KeyLastVacuumMachine = "last_vacuum_machine";

        // ウィンドウ設定キー
        public const string KeyWindowLeft = "window_left";
        public const string KeyWindowTop = "window_top";
        public const string KeyWindowWidth = "window_width";
        public const string KeyWindowHeight = "window_height";
        public const string KeyWindowMaximized = "window_maximized";

        // 音声モード設定キー
        public const string KeySoundMode = "sound_mode";

        // トースト位置設定キー
        public const string KeyToastPosition = "toast_position";

        // 部署種別設定キー
        public const string KeyDepartmentType = "department_type";

        // バス停入力スキップ設定キー
        public const string KeySkipBusStopInputOnReturn = "skip_bus_stop_input_on_return";

        // 帳票出力先フォルダ設定キー
        public const string KeyReportOutputFolder = "report_output_folder";

        public SettingsRepository(DbContext dbContext, ICacheService cacheService, IOptions<CacheOptions> cacheOptions)
        {
            _dbContext = dbContext;
            _cacheService = cacheService;
            _cacheOptions = cacheOptions.Value;
        }

        /// <inheritdoc/>
        public async Task<string> GetAsync(string key)
        {
            using var lease = await _dbContext.LeaseConnectionAsync();
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = @key";
            command.Parameters.AddWithValue("@key", key);

            var result = await command.ExecuteScalarAsync();
            return result == DBNull.Value ? null : result?.ToString();
        }

        /// <inheritdoc/>
        public async Task<bool> SetAsync(string key, string value)
        {
            return await WriteGuardedAsync(command =>
            {
                command.CommandText = @"INSERT INTO settings (key, value) VALUES (@key, @value)
ON CONFLICT(key) DO UPDATE SET value = @value";

                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", (object)value ?? DBNull.Value);
            }).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<bool> TryAcquireMonthlyVacuumLockAsync(DateTime today)
        {
            // Issue #1482: 「先勝ち CAS ロック」。
            // ON CONFLICT DO UPDATE の WHERE 句で「既存値が当月外なら更新」を表現し、
            // SQLite のステートメント原子性を利用して複数 PC 間の排他を実現する。
            // - 行が存在しない初回: INSERT が走り rowsAffected=1（先勝ち成立）
            // - 既存値が前月/null: WHERE 真 → UPDATE 走り rowsAffected=1
            // - 既存値が当月: WHERE 偽 → 何もしない、rowsAffected=0
            var acquired = await WriteGuardedAsync(command =>
            {
                command.CommandText = @"INSERT INTO settings (key, value) VALUES (@key, @today)
ON CONFLICT(key) DO UPDATE SET value = excluded.value
WHERE settings.value IS NULL OR substr(settings.value, 1, 7) <> @currentMonth";

                command.Parameters.AddWithValue("@key", KeyLastVacuumDate);
                command.Parameters.AddWithValue("@today", today.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("@currentMonth", today.ToString("yyyy-MM"));
            }).ConfigureAwait(false);

            if (acquired)
            {
                _cacheService.Invalidate(CacheKeys.AppSettings);
                return true;
            }
            return false;
        }

        /// <summary>
        /// settings への 1 文の書き込みを、単一接続のセマフォ保護下で実行する（Issue #1737）。
        /// </summary>
        /// <param name="configureCommand">CommandText とパラメータを設定するデリゲート</param>
        /// <returns>影響行数が 1 以上なら true</returns>
        /// <remarks>
        /// <para>
        /// <c>settings</c> は起動時の自動バックアップ（<c>last_backup_success_at</c>）や
        /// 月次 VACUUM の CAS ロックなど、**UI 操作を伴わない保守処理**から書かれる。
        /// <see cref="DbContext.LeaseConnectionAsync"/> はセマフォを取らないため、そのまま使うと
        /// <c>CleanupOldData</c> が開いているトランザクションの内側に INSERT が潜り込み、
        /// cleanup のロールバックで書き込みが道連れで消える。VACUUM 実行中なら
        /// "cannot VACUUM - SQL statements in progress" になる。
        /// </para>
        /// <para>
        /// 分岐は <c>.claude/rules/development-conventions.md</c> の規約に従う:
        /// 外側スコープが既にある場合は接続だけを借りて暗黙参加する（②）。
        /// <see cref="DbContext.BeginTransactionAsync"/> は <c>SemaphoreSlim(1,1)</c> を取るため、
        /// 入れ子で開くと自己デッドロックする（Issue #1575）。
        /// <see cref="SaveAppSettingsAsync"/> は実際に外側スコープの内側から本メソッドを繰り返し呼ぶ。
        /// </para>
        /// </remarks>
        private async Task<bool> WriteGuardedAsync(Action<SQLiteCommand> configureCommand)
        {
            // ② 外側に BeginTransactionAsync のスコープがある場合は、そのトランザクションへ暗黙参加する
            //    （セマフォは外側が保持済み。commit/rollback も外側の責務）
            if (_dbContext.HasActiveTransactionScope)
            {
                using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                using var joinedCommand = lease.Connection.CreateCommand();
                configureCommand(joinedCommand);
                return await joinedCommand.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
            }

            // ③ 外側スコープが無い場合は自前でトランザクションを持つ（＝セマフォを取る）
            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            using var command = scope.Lease.Connection.CreateCommand();
            command.Transaction = scope.Transaction;
            configureCommand(command);

            var rowsAffected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            scope.Commit();
            return rowsAffected > 0;
        }

        /// <inheritdoc/>
        public async Task<AppSettings> GetAppSettingsAsync()
        {
            return await _cacheService.GetOrCreateAsync(
                CacheKeys.AppSettings,
                async () => await GetAppSettingsFromDbAsync(),
                TimeSpan.FromMinutes(_cacheOptions.SettingsMinutes));
        }

        /// <inheritdoc/>
        public AppSettings GetAppSettings()
        {
            // キャッシュから取得を試みる（同期版）
            var cached = _cacheService.Get<AppSettings>(CacheKeys.AppSettings);
            if (cached != null)
            {
                return cached;
            }

            // DBから同期的に取得
            var settings = GetAppSettingsFromDb();
            _cacheService.Set(CacheKeys.AppSettings, settings, TimeSpan.FromMinutes(_cacheOptions.SettingsMinutes));
            return settings;
        }

        /// <summary>
        /// DBから設定を取得（同期版）
        /// </summary>
        private AppSettings GetAppSettingsFromDb()
        {
            var settings = new AppSettings();

            // 残額警告閾値
            var warningBalance = Get(KeyWarningBalance);
            if (int.TryParse(warningBalance, out var balance))
            {
                settings.WarningBalance = balance;
            }

            // バックアップパス
            var backupPath = Get(KeyBackupPath);
            settings.BackupPath = backupPath ?? GetDefaultBackupPath();

            // 文字サイズ
            var fontSize = Get(KeyFontSize);
            settings.FontSize = ParseFontSize(fontSize);

            // 最終VACUUM実行日
            var lastVacuumDate = Get(KeyLastVacuumDate);
            if (DateTime.TryParse(lastVacuumDate, out var date))
            {
                settings.LastVacuumDate = date;
            }

            // ウィンドウ設定
            settings.MainWindowSettings = GetWindowSettingsFromDb();

            // 音声モード設定
            var soundMode = Get(KeySoundMode);
            settings.SoundMode = ParseSoundMode(soundMode);

            // トースト位置設定
            var toastPosition = Get(KeyToastPosition);
            settings.ToastPosition = ParseToastPosition(toastPosition);

            // 部署種別設定
            var departmentType = Get(KeyDepartmentType);
            settings.DepartmentType = ParseDepartmentType(departmentType);

            // バス停入力スキップ設定
            var skipBusStopInput = Get(KeySkipBusStopInputOnReturn);
            settings.SkipBusStopInputOnReturn = skipBusStopInput?.ToLowerInvariant() == "true";

            // 帳票出力先フォルダ設定
            var reportOutputFolder = Get(KeyReportOutputFolder);
            settings.ReportOutputFolder = reportOutputFolder ?? string.Empty;

            return settings;
        }

        /// <summary>
        /// 設定値を取得（同期版）
        /// </summary>
        private string Get(string key)
        {
            using var lease = _dbContext.LeaseConnection();
            var connection = lease.Connection;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = @key";
            command.Parameters.AddWithValue("@key", key);

            var result = command.ExecuteScalar();
            return result == DBNull.Value ? null : result?.ToString();
        }

        /// <summary>
        /// DBからウィンドウ設定を取得（同期版）
        /// </summary>
        private WindowSettings GetWindowSettingsFromDb()
        {
            var windowSettings = new WindowSettings();

            var left = Get(KeyWindowLeft);
            if (double.TryParse(left, out var leftValue))
            {
                windowSettings.Left = leftValue;
            }

            var top = Get(KeyWindowTop);
            if (double.TryParse(top, out var topValue))
            {
                windowSettings.Top = topValue;
            }

            var width = Get(KeyWindowWidth);
            if (double.TryParse(width, out var widthValue))
            {
                windowSettings.Width = widthValue;
            }

            var height = Get(KeyWindowHeight);
            if (double.TryParse(height, out var heightValue))
            {
                windowSettings.Height = heightValue;
            }

            var maximized = Get(KeyWindowMaximized);
            windowSettings.IsMaximized = maximized?.ToLowerInvariant() == "true";

            return windowSettings;
        }

        /// <summary>
        /// DBから設定を取得
        /// </summary>
        private async Task<AppSettings> GetAppSettingsFromDbAsync()
        {
            var settings = new AppSettings();

            // 残額警告閾値
            var warningBalance = await GetAsync(KeyWarningBalance);
            if (int.TryParse(warningBalance, out var balance))
            {
                settings.WarningBalance = balance;
            }

            // バックアップパス
            var backupPath = await GetAsync(KeyBackupPath);
            settings.BackupPath = backupPath ?? GetDefaultBackupPath();

            // 文字サイズ
            var fontSize = await GetAsync(KeyFontSize);
            settings.FontSize = ParseFontSize(fontSize);

            // 最終VACUUM実行日
            var lastVacuumDate = await GetAsync(KeyLastVacuumDate);
            if (DateTime.TryParse(lastVacuumDate, out var date))
            {
                settings.LastVacuumDate = date;
            }

            // ウィンドウ設定
            settings.MainWindowSettings = await GetWindowSettingsFromDbAsync();

            // 音声モード設定
            var soundMode = await GetAsync(KeySoundMode);
            settings.SoundMode = ParseSoundMode(soundMode);

            // トースト位置設定
            var toastPosition = await GetAsync(KeyToastPosition);
            settings.ToastPosition = ParseToastPosition(toastPosition);

            // 部署種別設定
            var departmentType = await GetAsync(KeyDepartmentType);
            settings.DepartmentType = ParseDepartmentType(departmentType);

            // バス停入力スキップ設定
            var skipBusStopInput = await GetAsync(KeySkipBusStopInputOnReturn);
            settings.SkipBusStopInputOnReturn = skipBusStopInput?.ToLowerInvariant() == "true";

            // 帳票出力先フォルダ設定
            var reportOutputFolder = await GetAsync(KeyReportOutputFolder);
            settings.ReportOutputFolder = reportOutputFolder ?? string.Empty;

            return settings;
        }

        /// <summary>
        /// DBからウィンドウ設定を取得
        /// </summary>
        private async Task<WindowSettings> GetWindowSettingsFromDbAsync()
        {
            var windowSettings = new WindowSettings();

            var left = await GetAsync(KeyWindowLeft);
            if (double.TryParse(left, out var leftValue))
            {
                windowSettings.Left = leftValue;
            }

            var top = await GetAsync(KeyWindowTop);
            if (double.TryParse(top, out var topValue))
            {
                windowSettings.Top = topValue;
            }

            var width = await GetAsync(KeyWindowWidth);
            if (double.TryParse(width, out var widthValue))
            {
                windowSettings.Width = widthValue;
            }

            var height = await GetAsync(KeyWindowHeight);
            if (double.TryParse(height, out var heightValue))
            {
                windowSettings.Height = heightValue;
            }

            var maximized = await GetAsync(KeyWindowMaximized);
            windowSettings.IsMaximized = maximized?.ToLowerInvariant() == "true";

            return windowSettings;
        }

        /// <inheritdoc/>
        public async Task<bool> SaveAppSettingsAsync(AppSettings settings)
        {
            // Issue #1240: 共有モードで他PCが更新途中の中間状態を読み取らないよう、
            // すべての設定キーの更新を単一トランザクション内で実行する。
            var success = true;

            await _dbContext.ExecuteWithRetryAsync(async () =>
            {
                using var scope = await _dbContext.BeginTransactionAsync();

                try
                {
                    success = true;

                    success &= await SetAsync(KeyWarningBalance, settings.WarningBalance.ToString());
                    success &= await SetAsync(KeyBackupPath, settings.BackupPath);
                    success &= await SetAsync(KeyFontSize, FontSizeToString(settings.FontSize));

                    if (settings.LastVacuumDate.HasValue)
                    {
                        success &= await SetAsync(KeyLastVacuumDate, settings.LastVacuumDate.Value.ToString("yyyy-MM-dd"));
                    }

                    // ウィンドウ設定を保存
                    success &= await SaveWindowSettingsToDbAsync(settings.MainWindowSettings);

                    // 音声モード設定を保存
                    success &= await SetAsync(KeySoundMode, SoundModeToString(settings.SoundMode));

                    // トースト位置設定を保存
                    success &= await SetAsync(KeyToastPosition, ToastPositionToString(settings.ToastPosition));

                    // 部署種別設定を保存
                    success &= await SetAsync(KeyDepartmentType, DepartmentTypeToString(settings.DepartmentType));

                    // バス停入力スキップ設定を保存
                    success &= await SetAsync(KeySkipBusStopInputOnReturn, settings.SkipBusStopInputOnReturn.ToString().ToLowerInvariant());

                    // 帳票出力先フォルダ設定を保存
                    success &= await SetAsync(KeyReportOutputFolder, settings.ReportOutputFolder ?? string.Empty);

                    scope.Commit();
                }
                catch
                {
                    scope.Rollback();
                    throw;
                }
            });

            // トランザクション完了後にキャッシュを無効化
            _cacheService.Invalidate(CacheKeys.AppSettings);

            return success;
        }

        /// <summary>
        /// ウィンドウ設定をDBに保存
        /// </summary>
        private async Task<bool> SaveWindowSettingsToDbAsync(WindowSettings windowSettings)
        {
            var success = true;

            if (windowSettings.Left.HasValue)
            {
                success &= await SetAsync(KeyWindowLeft, windowSettings.Left.Value.ToString("F0"));
            }

            if (windowSettings.Top.HasValue)
            {
                success &= await SetAsync(KeyWindowTop, windowSettings.Top.Value.ToString("F0"));
            }

            if (windowSettings.Width.HasValue)
            {
                success &= await SetAsync(KeyWindowWidth, windowSettings.Width.Value.ToString("F0"));
            }

            if (windowSettings.Height.HasValue)
            {
                success &= await SetAsync(KeyWindowHeight, windowSettings.Height.Value.ToString("F0"));
            }

            success &= await SetAsync(KeyWindowMaximized, windowSettings.IsMaximized.ToString().ToLowerInvariant());

            return success;
        }

        /// <summary>
        /// デフォルトのバックアップパスを取得
        /// </summary>
        /// <remarks>
        /// CommonApplicationData（C:\ProgramData）を使用して全ユーザーで共有
        /// </remarks>
        private static string GetDefaultBackupPath()
        {
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ICCardManager",
                "backup");

            return appDataPath;
        }

        /// <summary>
        /// 文字列からFontSizeOptionに変換
        /// </summary>
        private static FontSizeOption ParseFontSize(string value)
        {
            return value?.ToLowerInvariant() switch
            {
                "small" => FontSizeOption.Small,
                "medium" => FontSizeOption.Medium,
                "large" => FontSizeOption.Large,
                "xlarge" or "extralarge" => FontSizeOption.ExtraLarge,
                _ => FontSizeOption.Medium
            };
        }

        /// <summary>
        /// FontSizeOptionを文字列に変換
        /// </summary>
        private static string FontSizeToString(FontSizeOption fontsize)
        {
            return fontsize switch
            {
                FontSizeOption.Small => "small",
                FontSizeOption.Medium => "medium",
                FontSizeOption.Large => "large",
                FontSizeOption.ExtraLarge => "xlarge",
                _ => "medium"
            };
        }

        /// <summary>
        /// 文字列からSoundModeに変換
        /// </summary>
        private static SoundMode ParseSoundMode(string value)
        {
            return value?.ToLowerInvariant() switch
            {
                "beep" => SoundMode.Beep,
                "voice_male" => SoundMode.VoiceMale,
                "voice_female" => SoundMode.VoiceFemale,
                "none" => SoundMode.None,
                _ => SoundMode.Beep
            };
        }

        /// <summary>
        /// SoundModeを文字列に変換
        /// </summary>
        private static string SoundModeToString(SoundMode soundMode)
        {
            return soundMode switch
            {
                SoundMode.Beep => "beep",
                SoundMode.VoiceMale => "voice_male",
                SoundMode.VoiceFemale => "voice_female",
                SoundMode.None => "none",
                _ => "beep"
            };
        }

        /// <summary>
        /// 文字列からToastPositionに変換
        /// </summary>
        private static ToastPosition ParseToastPosition(string value)
        {
            return value?.ToLowerInvariant() switch
            {
                "top_right" => ToastPosition.TopRight,
                "top_left" => ToastPosition.TopLeft,
                "bottom_right" => ToastPosition.BottomRight,
                "bottom_left" => ToastPosition.BottomLeft,
                _ => ToastPosition.TopRight
            };
        }

        /// <summary>
        /// ToastPositionを文字列に変換
        /// </summary>
        private static string ToastPositionToString(ToastPosition position)
        {
            return position switch
            {
                ToastPosition.TopRight => "top_right",
                ToastPosition.TopLeft => "top_left",
                ToastPosition.BottomRight => "bottom_right",
                ToastPosition.BottomLeft => "bottom_left",
                _ => "top_right"
            };
        }

        /// <summary>
        /// 文字列からDepartmentTypeに変換
        /// </summary>
        internal static DepartmentType ParseDepartmentType(string value)
        {
            return value?.ToLowerInvariant() switch
            {
                "mayor_office" => DepartmentType.MayorOffice,
                "enterprise_account" => DepartmentType.EnterpriseAccount,
                _ => DepartmentType.MayorOffice
            };
        }

        /// <summary>
        /// DepartmentTypeを文字列に変換
        /// </summary>
        internal static string DepartmentTypeToString(DepartmentType departmentType)
        {
            return departmentType switch
            {
                DepartmentType.MayorOffice => "mayor_office",
                DepartmentType.EnterpriseAccount => "enterprise_account",
                _ => "mayor_office"
            };
        }
    }
}
