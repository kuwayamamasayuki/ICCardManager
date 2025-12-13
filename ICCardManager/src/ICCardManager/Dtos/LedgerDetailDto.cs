namespace ICCardManager.Dtos;

/// <summary>
/// 利用履歴詳細DTO
/// ViewModelで使用する履歴詳細表示用オブジェクト
/// </summary>
public class LedgerDetailDto
{
    /// <summary>
    /// 親レコードID
    /// </summary>
    public int LedgerId { get; init; }

    /// <summary>
    /// 利用日時
    /// </summary>
    public DateTime? UseDate { get; init; }

    /// <summary>
    /// 表示用: 利用日時
    /// </summary>
    public string? UseDateDisplay { get; init; }

    /// <summary>
    /// 乗車駅
    /// </summary>
    public string? EntryStation { get; init; }

    /// <summary>
    /// 降車駅
    /// </summary>
    public string? ExitStation { get; init; }

    /// <summary>
    /// バス停名
    /// </summary>
    public string? BusStops { get; init; }

    /// <summary>
    /// 利用額／チャージ額
    /// </summary>
    public int? Amount { get; init; }

    /// <summary>
    /// 残額
    /// </summary>
    public int? Balance { get; init; }

    /// <summary>
    /// チャージフラグ
    /// </summary>
    public bool IsCharge { get; init; }

    /// <summary>
    /// バス利用フラグ
    /// </summary>
    public bool IsBus { get; init; }

    #region 表示用プロパティ

    /// <summary>
    /// 表示用: 利用区間（駅名またはバス停）
    /// </summary>
    public string RouteDisplay
    {
        get
        {
            if (IsCharge)
            {
                return "チャージ";
            }

            if (IsBus)
            {
                return string.IsNullOrEmpty(BusStops) ? "バス（★）" : $"バス（{BusStops}）";
            }

            if (!string.IsNullOrEmpty(EntryStation) && !string.IsNullOrEmpty(ExitStation))
            {
                return $"{EntryStation}～{ExitStation}";
            }

            if (!string.IsNullOrEmpty(EntryStation))
            {
                return $"{EntryStation}～";
            }

            if (!string.IsNullOrEmpty(ExitStation))
            {
                return $"～{ExitStation}";
            }

            return "不明";
        }
    }

    /// <summary>
    /// 表示用: 金額
    /// </summary>
    public string AmountDisplay => Amount.HasValue ? $"{Amount:N0}円" : "";

    /// <summary>
    /// 表示用: 残額
    /// </summary>
    public string BalanceDisplay => Balance.HasValue ? $"{Balance:N0}円" : "";

    #endregion
}
