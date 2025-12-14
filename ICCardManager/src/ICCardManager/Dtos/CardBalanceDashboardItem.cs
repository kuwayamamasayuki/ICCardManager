namespace ICCardManager.Dtos;

/// <summary>
/// カード残高ダッシュボード表示用DTO
/// メイン画面でカードの残高状況を一覧表示するために使用
/// </summary>
public class CardBalanceDashboardItem
{
    /// <summary>
    /// カードIDm（16進数16文字）
    /// </summary>
    public string CardIdm { get; init; } = string.Empty;

    /// <summary>
    /// カード種別（はやかけん/nimoca/SUGOCA等）
    /// </summary>
    public string CardType { get; init; } = string.Empty;

    /// <summary>
    /// 管理番号
    /// </summary>
    public string CardNumber { get; init; } = string.Empty;

    /// <summary>
    /// 現在残高（円）
    /// </summary>
    public int CurrentBalance { get; init; }

    /// <summary>
    /// 残高警告フラグ（残高が閾値以下の場合true）
    /// </summary>
    public bool IsBalanceWarning { get; init; }

    /// <summary>
    /// 最終利用日
    /// </summary>
    public DateTime? LastUsageDate { get; init; }

    /// <summary>
    /// 貸出状態（true: 貸出中）
    /// </summary>
    public bool IsLent { get; init; }

    /// <summary>
    /// 貸出者名（貸出中の場合）
    /// </summary>
    public string? LentStaffName { get; init; }

    #region 表示用プロパティ

    /// <summary>
    /// 表示用: カード名（種別 + 番号）
    /// </summary>
    public string DisplayName => $"{CardType} {CardNumber}";

    /// <summary>
    /// 表示用: 残高（円単位、3桁区切り）
    /// </summary>
    public string BalanceDisplay => $"¥{CurrentBalance:N0}";

    /// <summary>
    /// 表示用: 警告アイコン（⚠）
    /// </summary>
    public string WarningIcon => IsBalanceWarning ? "⚠" : "";

    /// <summary>
    /// 表示用: 貸出状態アイコン
    /// </summary>
    public string LentStatusIcon => IsLent ? "📤" : "📥";

    /// <summary>
    /// 表示用: 貸出状態テキスト
    /// </summary>
    public string LentStatusDisplay => IsLent ? "貸出中" : "在庫";

    /// <summary>
    /// 表示用: 貸出情報（貸出中の場合は貸出者名を表示）
    /// </summary>
    public string LentInfoDisplay => IsLent && !string.IsNullOrEmpty(LentStaffName)
        ? $"貸出中（{LentStaffName}）"
        : LentStatusDisplay;

    /// <summary>
    /// 表示用: 最終利用日
    /// </summary>
    public string LastUsageDateDisplay => LastUsageDate?.ToString("yyyy/MM/dd") ?? "-";

    /// <summary>
    /// 表示用: 行の背景色（警告時は薄い赤）
    /// </summary>
    public string RowBackgroundColor => IsBalanceWarning ? "#FFEBEE" : "Transparent";

    #endregion
}
