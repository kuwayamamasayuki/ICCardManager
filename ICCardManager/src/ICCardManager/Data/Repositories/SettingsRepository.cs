using System.IO;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories;

/// <summary>
/// 設定リポジトリ実装
/// </summary>
public class SettingsRepository : ISettingsRepository
{
    private readonly DbContext _dbContext;
    private readonly ICacheService _cacheService;

    // 設定キー定数
    public const string KeyWarningBalance = "warning_balance";
    public const string KeyBackupPath = "backup_path";
    public const string KeyFontSize = "font_size";
    public const string KeyLastVacuumDate = "last_vacuum_date";

    public SettingsRepository(DbContext dbContext, ICacheService cacheService)
    {
        _dbContext = dbContext;
        _cacheService = cacheService;
    }

    /// <inheritdoc/>
    public async Task<string?> GetAsync(string key)
    {
        var connection = _dbContext.GetConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key";
        command.Parameters.AddWithValue("@key", key);

        var result = await command.ExecuteScalarAsync();
        return result == DBNull.Value ? null : result?.ToString();
    }

    /// <inheritdoc/>
    public async Task<bool> SetAsync(string key, string? value)
    {
        var connection = _dbContext.GetConnection();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value
            """;

        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);

        var result = await command.ExecuteNonQueryAsync();
        return result > 0;
    }

    /// <inheritdoc/>
    public async Task<AppSettings> GetAppSettingsAsync()
    {
        return await _cacheService.GetOrCreateAsync(
            CacheKeys.AppSettings,
            async () => await GetAppSettingsFromDbAsync(),
            CacheDurations.Settings);
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

        return settings;
    }

    /// <inheritdoc/>
    public async Task<bool> SaveAppSettingsAsync(AppSettings settings)
    {
        var success = true;

        success &= await SetAsync(KeyWarningBalance, settings.WarningBalance.ToString());
        success &= await SetAsync(KeyBackupPath, settings.BackupPath);
        success &= await SetAsync(KeyFontSize, FontSizeToString(settings.FontSize));

        if (settings.LastVacuumDate.HasValue)
        {
            success &= await SetAsync(KeyLastVacuumDate, settings.LastVacuumDate.Value.ToString("yyyy-MM-dd"));
        }

        // 設定保存後にキャッシュを無効化
        _cacheService.Invalidate(CacheKeys.AppSettings);

        return success;
    }

    /// <summary>
    /// デフォルトのバックアップパスを取得
    /// </summary>
    private static string GetDefaultBackupPath()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ICCardManager",
            "backup");

        return appDataPath;
    }

    /// <summary>
    /// 文字列からFontSizeOptionに変換
    /// </summary>
    private static FontSizeOption ParseFontSize(string? value)
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
}
