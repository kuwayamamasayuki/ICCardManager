using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using ICCardManager.Common;
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

        // 同行者数入力スキップ設定キー（Issue #1906）
        public const string KeySkipCompanionCountInputOnReturn = "skip_companion_count_input_on_return";

        // 帳票出力先フォルダ設定キー
        public const string KeyReportOutputFolder = "report_output_folder";

        /// <summary>
        /// 同一とみなす駅・バス停のグループ（JSON 配列の配列、Issue #1905）
        /// </summary>
        /// <remarks>
        /// settings は key-value テーブルのためスキーマ変更（マイグレーション）は不要。
        /// 読み書きは <see cref="Services.ITransferStationGroupService"/> 経由で行い、
        /// <see cref="SaveAppSettingsAsync"/> の一括保存には含めない。
        /// </remarks>
        public const string KeyTransferStationGroups = "transfer_station_groups";

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
            return await WriteGuardedAsync(command => ConfigureSetCommand(command, key, value), scope: null)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// 呼び出し元が開いたトランザクションへ書き込む <see cref="SetAsync(string, string)"/>（Issue #1737）。
        /// </summary>
        /// <remarks>
        /// 外側スコープの内側から書く経路は**必ずこちらを使う**こと。
        /// 引数なし版はスコープの持ち主を判別できず、他フローのスコープに巻き込まれる余地が残る
        /// （<see cref="WriteGuardedAsync"/> の分岐②の注記を参照）。
        /// </remarks>
        private async Task<bool> SetAsync(string key, string value, TransactionScope scope)
        {
            return await WriteGuardedAsync(command => ConfigureSetCommand(command, key, value), scope)
                .ConfigureAwait(false);
        }

        private static void ConfigureSetCommand(SQLiteCommand command, string key, string value)
        {
            command.CommandText = @"INSERT INTO settings (key, value) VALUES (@key, @value)
ON CONFLICT(key) DO UPDATE SET value = @value";

            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", (object)value ?? DBNull.Value);
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
            }, scope: null).ConfigureAwait(false);

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
        /// <param name="scope">呼び出し元が開いたトランザクション。無い場合は null</param>
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
        /// 分岐は <c>.claude/rules/development-conventions.md</c> の 3 分岐規約に従う。
        /// </para>
        /// <para>
        /// <b>① <paramref name="scope"/> あり</b>: そのトランザクションを全文に適用する。
        /// commit / rollback は呼び出し元の責務。<see cref="SaveAppSettingsAsync"/> のように
        /// 外側スコープの内側から書く経路は**必ずこの形にする**。
        /// </para>
        /// <para>
        /// <b>② <paramref name="scope"/> が null かつ <see cref="DbContext.HasActiveTransactionScope"/></b>:
        /// 接続だけ借りて暗黙参加する。<see cref="DbContext.BeginTransactionAsync"/> は
        /// <c>SemaphoreSlim(1,1)</c> を取るため、自分のフローが既に開いているスコープの内側で
        /// 開き直すと自己デッドロックする（Issue #1575）。この分岐はそれを避けるための backstop。
        /// <b>ただし <see cref="DbContext.HasActiveTransactionScope"/> はプロセス全体のカウンタであり、
        /// 「自分のスコープか他フローのスコープか」を区別できない。</b>
        /// 他フローのスコープに巻き込まれると、そのロールバックで書き込みが消える（#1737 の欠陥そのもの）。
        /// したがって**入れ子になる経路は①で明示的に引き渡すこと**。②に頼ってはならない。
        /// </para>
        /// <para>
        /// <b>③ それ以外</b>: 自前でトランザクションを持つ（＝セマフォを取る）。
        /// 他フローがスコープを保持していればここで待たされるため、巻き込まれない。
        /// 共有モードの SQLITE_BUSY に備え <c>DbContext.ExecuteWithRetryAsync</c>
        /// で包む（Issue #1727「書込みトランザクションを新設したら既存の同種経路と同じ防御が掛かっているかを見る」）。
        /// </para>
        /// </remarks>
        private async Task<bool> WriteGuardedAsync(Action<SQLiteCommand> configureCommand, TransactionScope scope)
        {
            // ① 呼び出し元がトランザクションを引き渡した場合はそれを使う
            if (scope != null)
            {
                return await ExecuteWriteAsync(scope.Lease.Connection, scope.Transaction, configureCommand)
                    .ConfigureAwait(false);
            }

            // ② 引数 null かつ外側スコープが開いている場合は接続だけ借りて暗黙参加する
            //    （commit/rollback は外側の責務。上記 remarks の注意書きを必ず読むこと）
            if (_dbContext.HasActiveTransactionScope)
            {
                using var lease = await _dbContext.LeaseConnectionAsync().ConfigureAwait(false);
                return await ExecuteWriteAsync(lease.Connection, transaction: null, configureCommand)
                    .ConfigureAwait(false);
            }

            // ③ 外側スコープが無い場合は自前でトランザクションを持つ（＝セマフォを取る）
            var result = false;
            await _dbContext.ExecuteWithRetryAsync(async () =>
            {
                using var ownScope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
                result = await ExecuteWriteAsync(
                    ownScope.Lease.Connection, ownScope.Transaction, configureCommand).ConfigureAwait(false);
                ownScope.Commit();
            }).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// 指定の接続／トランザクション上で 1 文を実行する（Issue #1737）。
        /// </summary>
        /// <remarks>
        /// コマンドは呼び出しごとに新規作成するため、
        /// <c>DbContext.ExecuteWithRetryAsync</c> による再試行でも
        /// パラメータが二重登録されない。
        /// </remarks>
        private static async Task<bool> ExecuteWriteAsync(
            SQLiteConnection connection,
            SQLiteTransaction transaction,
            Action<SQLiteCommand> configureCommand)
        {
            using var command = connection.CreateCommand();
            if (transaction != null)
            {
                command.Transaction = transaction;
            }
            configureCommand(command);

            return await command.ExecuteNonQueryAsync().ConfigureAwait(false) > 0;
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

            // 同行者数入力スキップ設定（Issue #1906）
            settings.SkipCompanionCountInputOnReturn = Get(KeySkipCompanionCountInputOnReturn)?.ToLowerInvariant() == "true";

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

            // 同行者数入力スキップ設定（Issue #1906）
            settings.SkipCompanionCountInputOnReturn = (await GetAsync(KeySkipCompanionCountInputOnReturn))?.ToLowerInvariant() == "true";

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

                    // Issue #1737: 外側スコープの内側から書くため、トランザクションを明示的に引き渡す（分岐①）。
                    // 引数なし版に頼ると HasActiveTransactionScope（プロセス全体のカウンタ）経由の
                    // 暗黙参加になり、「自分のスコープか他フローのスコープか」を区別できない。
                    success &= await SetAsync(KeyWarningBalance, settings.WarningBalance.ToString(), scope);
                    success &= await SetAsync(KeyBackupPath, settings.BackupPath, scope);
                    success &= await SetAsync(KeyFontSize, FontSizeToString(settings.FontSize), scope);

                    if (settings.LastVacuumDate.HasValue)
                    {
                        success &= await SetAsync(KeyLastVacuumDate, settings.LastVacuumDate.Value.ToString("yyyy-MM-dd"), scope);
                    }

                    // ウィンドウ設定を保存
                    success &= await SaveWindowSettingsToDbAsync(settings.MainWindowSettings, scope);

                    // 音声モード設定を保存
                    success &= await SetAsync(KeySoundMode, SoundModeToString(settings.SoundMode), scope);

                    // トースト位置設定を保存
                    success &= await SetAsync(KeyToastPosition, ToastPositionToString(settings.ToastPosition), scope);

                    // 部署種別設定を保存
                    success &= await SetAsync(KeyDepartmentType, DepartmentTypeToString(settings.DepartmentType), scope);

                    // バス停入力スキップ設定を保存
                    success &= await SetAsync(KeySkipBusStopInputOnReturn, settings.SkipBusStopInputOnReturn.ToString().ToLowerInvariant(), scope);

                    // 同行者数入力スキップ設定を保存（Issue #1906）
                    success &= await SetAsync(KeySkipCompanionCountInputOnReturn, settings.SkipCompanionCountInputOnReturn.ToString().ToLowerInvariant(), scope);

                    // 帳票出力先フォルダ設定を保存
                    success &= await SetAsync(KeyReportOutputFolder, settings.ReportOutputFolder ?? string.Empty, scope);

                    scope.Commit();
                }
                catch
                {
                    // Issue #1831: 素の Rollback() を呼ばない（詳細は SafeRollback の XML doc）
                    SafeRollback.TryRollback(() => scope.Rollback(), logger: null, "アプリ設定の保存");
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
        /// <param name="windowSettings">保存するウィンドウ設定</param>
        /// <param name="scope">呼び出し元が開いたトランザクション（Issue #1737: 分岐①で引き渡す）</param>
        private async Task<bool> SaveWindowSettingsToDbAsync(WindowSettings windowSettings, TransactionScope scope)
        {
            var success = true;

            if (windowSettings.Left.HasValue)
            {
                success &= await SetAsync(KeyWindowLeft, windowSettings.Left.Value.ToString("F0"), scope);
            }

            if (windowSettings.Top.HasValue)
            {
                success &= await SetAsync(KeyWindowTop, windowSettings.Top.Value.ToString("F0"), scope);
            }

            if (windowSettings.Width.HasValue)
            {
                success &= await SetAsync(KeyWindowWidth, windowSettings.Width.Value.ToString("F0"), scope);
            }

            if (windowSettings.Height.HasValue)
            {
                success &= await SetAsync(KeyWindowHeight, windowSettings.Height.Value.ToString("F0"), scope);
            }

            success &= await SetAsync(KeyWindowMaximized, windowSettings.IsMaximized.ToString().ToLowerInvariant(), scope);

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
