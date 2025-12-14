namespace ICCardManager.Infrastructure.Logging;

/// <summary>
/// ファイルロガーの設定オプション
/// </summary>
public class FileLoggerOptions
{
    /// <summary>
    /// ファイルログが有効かどうか
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// ログファイルの出力ディレクトリ（アプリケーションディレクトリからの相対パス）
    /// </summary>
    public string Path { get; set; } = "Logs";

    /// <summary>
    /// ログファイルの保持日数
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// ログファイルの最大サイズ（MB）
    /// </summary>
    public int MaxFileSizeMB { get; set; } = 10;
}
