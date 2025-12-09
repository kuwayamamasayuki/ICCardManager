using ICCardManager.Models;

namespace ICCardManager.Data.Repositories;

/// <summary>
/// 設定リポジトリインターフェース
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// 設定値を取得
    /// </summary>
    /// <param name="key">設定キー</param>
    Task<string?> GetAsync(string key);

    /// <summary>
    /// 設定値を保存
    /// </summary>
    /// <param name="key">設定キー</param>
    /// <param name="value">設定値</param>
    Task<bool> SetAsync(string key, string? value);

    /// <summary>
    /// 全設定をAppSettingsオブジェクトとして取得
    /// </summary>
    Task<AppSettings> GetAppSettingsAsync();

    /// <summary>
    /// AppSettingsオブジェクトを保存
    /// </summary>
    Task<bool> SaveAppSettingsAsync(AppSettings settings);
}
