namespace ICCardManager.Dtos;

/// <summary>
/// 職員情報DTO
/// ViewModelで使用する職員情報の表示用オブジェクト
/// </summary>
public class StaffDto
{
    /// <summary>
    /// 職員証IDm（16進数16文字）
    /// </summary>
    public string StaffIdm { get; init; } = string.Empty;

    /// <summary>
    /// 職員氏名
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// 職員番号
    /// </summary>
    public string? Number { get; init; }

    /// <summary>
    /// 備考
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// 表示用: 職員名（番号付き）
    /// </summary>
    public string DisplayName => string.IsNullOrEmpty(Number)
        ? Name
        : $"{Number} {Name}";
}
