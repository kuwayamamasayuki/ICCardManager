namespace ICCardManager.Models;

/// <summary>
/// 操作ログエンティティ（operation_logテーブル）
/// 監査証跡として全ての手動操作を記録
/// </summary>
public class OperationLog
{
    /// <summary>
    /// ログID（主キー、自動採番）
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 操作日時
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 操作者IDm
    /// </summary>
    public string OperatorIdm { get; set; } = string.Empty;

    /// <summary>
    /// 操作者氏名（スナップショット保存）
    /// </summary>
    public string OperatorName { get; set; } = string.Empty;

    /// <summary>
    /// 操作対象テーブル名
    /// </summary>
    public string? TargetTable { get; set; }

    /// <summary>
    /// 操作対象レコードID/IDm
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>
    /// 操作種別（INSERT/UPDATE/DELETE）
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// 操作前データ（JSON形式）
    /// </summary>
    public string? BeforeData { get; set; }

    /// <summary>
    /// 操作後データ（JSON形式）
    /// </summary>
    public string? AfterData { get; set; }
}
