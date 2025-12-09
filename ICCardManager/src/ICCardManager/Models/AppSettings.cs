namespace ICCardManager.Models;

/// <summary>
/// アプリケーション設定モデル
/// settingsテーブルのKVS形式を構造化して保持
/// </summary>
public class AppSettings
{
    /// <summary>
    /// 残額警告閾値（円）
    /// </summary>
    public int WarningBalance { get; set; } = 10000;

    /// <summary>
    /// バックアップ先フォルダパス
    /// </summary>
    public string BackupPath { get; set; } = string.Empty;

    /// <summary>
    /// 文字サイズ
    /// </summary>
    public FontSizeOption FontSize { get; set; } = FontSizeOption.Medium;

    /// <summary>
    /// 最終VACUUM実行日
    /// </summary>
    public DateTime? LastVacuumDate { get; set; }
}

/// <summary>
/// 文字サイズオプション
/// </summary>
public enum FontSizeOption
{
    /// <summary>
    /// 小
    /// </summary>
    Small,

    /// <summary>
    /// 中（デフォルト）
    /// </summary>
    Medium,

    /// <summary>
    /// 大
    /// </summary>
    Large,

    /// <summary>
    /// 特大
    /// </summary>
    ExtraLarge
}
