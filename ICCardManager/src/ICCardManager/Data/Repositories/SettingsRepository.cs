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

    // ウィンドウ設定キー
    public const string KeyWindowLeft = "window_left";
    public const string KeyWindowTop = "window_top";
    public const string KeyWindowWidth = "window_width";
    public const string KeyWindowHeight = "window_height";
    public const string KeyWindowMaximized = "window_maximized";

    // 職員証スキップ設定キー
    public const string KeySkipStaffTouch = "skip_staff_touch";
    public const string KeyDefaultStaffIdm = "default_staff_idm";

    // 音声モード設定キー
    public const string KeySoundMode = "sound_mode";

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

        // ウィンドウ設定
        settings.MainWindowSettings = await GetWindowSettingsFromDbAsync();

        // 職員証スキップ設定
        var skipStaffTouch = await GetAsync(KeySkipStaffTouch);
        settings.SkipStaffTouch = skipStaffTouch?.ToLowerInvariant() == "true";

        var defaultStaffIdm = await GetAsync(KeyDefaultStaffIdm);
        settings.DefaultStaffIdm = string.IsNullOrEmpty(defaultStaffIdm) ? null : defaultStaffIdm;

        // 音声モード設定
        var soundMode = await GetAsync(KeySoundMode);
        settings.SoundMode = ParseSoundMode(soundMode);

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
        var success = true;

        success &= await SetAsync(KeyWarningBalance, settings.WarningBalance.ToString());
        success &= await SetAsync(KeyBackupPath, settings.BackupPath);
        success &= await SetAsync(KeyFontSize, FontSizeToString(settings.FontSize));

        if (settings.LastVacuumDate.HasValue)
        {
            success &= await SetAsync(KeyLastVacuumDate, settings.LastVacuumDate.Value.ToString("yyyy-MM-dd"));
        }

        // ウィンドウ設定を保存
        success &= await SaveWindowSettingsToDbAsync(settings.MainWindowSettings);

        // 職員証スキップ設定を保存
        success &= await SetAsync(KeySkipStaffTouch, settings.SkipStaffTouch.ToString().ToLowerInvariant());
        success &= await SetAsync(KeyDefaultStaffIdm, settings.DefaultStaffIdm);

        // 音声モード設定を保存
        success &= await SetAsync(KeySoundMode, SoundModeToString(settings.SoundMode));

        // 設定保存後にキャッシュを無効化
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

    /// <summary>
    /// 文字列からSoundModeに変換
    /// </summary>
    private static SoundMode ParseSoundMode(string? value)
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
}
