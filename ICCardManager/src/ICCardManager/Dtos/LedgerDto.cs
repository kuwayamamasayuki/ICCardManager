namespace ICCardManager.Dtos;

/// <summary>
/// 利用履歴DTO
/// ViewModelで使用する履歴表示用オブジェクト
/// </summary>
public class LedgerDto
{
    /// <summary>
    /// レコードID
    /// </summary>
    public int Id { get; init; }

    /// <summary>
    /// カードIDm
    /// </summary>
    public string CardIdm { get; init; } = string.Empty;

    /// <summary>
    /// 出納日付
    /// </summary>
    public DateTime Date { get; init; }

    /// <summary>
    /// 表示用: 日付（和暦変換済み）
    /// </summary>
    public string DateDisplay { get; init; } = string.Empty;

    /// <summary>
    /// 摘要
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// 受入金額（チャージ額）
    /// </summary>
    public int Income { get; init; }

    /// <summary>
    /// 払出金額（利用額）
    /// </summary>
    public int Expense { get; init; }

    /// <summary>
    /// 残額
    /// </summary>
    public int Balance { get; init; }

    /// <summary>
    /// 利用者氏名
    /// </summary>
    public string? StaffName { get; init; }

    /// <summary>
    /// 備考
    /// </summary>
    public string? Note { get; init; }

    /// <summary>
    /// 貸出中レコードフラグ
    /// </summary>
    public bool IsLentRecord { get; init; }

    /// <summary>
    /// 利用履歴詳細
    /// </summary>
    public List<LedgerDetailDto> Details { get; init; } = new();

    #region 表示用プロパティ

    /// <summary>
    /// 表示用: 受入金額（金額がある場合のみ表示）
    /// </summary>
    public string IncomeDisplay => Income > 0 ? $"+{Income:N0}" : "";

    /// <summary>
    /// 表示用: 払出金額（金額がある場合のみ表示）
    /// </summary>
    public string ExpenseDisplay => Expense > 0 ? $"-{Expense:N0}" : "";

    /// <summary>
    /// 表示用: 残額
    /// </summary>
    public string BalanceDisplay => $"{Balance:N0}";

    /// <summary>
    /// 受入金額があるかどうか
    /// </summary>
    public bool HasIncome => Income > 0;

    /// <summary>
    /// 払出金額があるかどうか
    /// </summary>
    public bool HasExpense => Expense > 0;

    #endregion
}
