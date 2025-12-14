using ICCardManager.Models;

namespace ICCardManager.Data.Repositories;

/// <summary>
/// 利用履歴リポジトリインターフェース
/// </summary>
public interface ILedgerRepository
{
    /// <summary>
    /// 指定期間の利用履歴を取得
    /// </summary>
    /// <param name="cardIdm">ICカードIDm（nullの場合は全カード）</param>
    /// <param name="fromDate">開始日</param>
    /// <param name="toDate">終了日</param>
    Task<IEnumerable<Ledger>> GetByDateRangeAsync(string? cardIdm, DateTime fromDate, DateTime toDate);

    /// <summary>
    /// 指定月の利用履歴を取得（帳票用）
    /// </summary>
    /// <param name="cardIdm">ICカードIDm</param>
    /// <param name="year">年</param>
    /// <param name="month">月</param>
    Task<IEnumerable<Ledger>> GetByMonthAsync(string cardIdm, int year, int month);

    /// <summary>
    /// IDで利用履歴を取得（詳細含む）
    /// </summary>
    Task<Ledger?> GetByIdAsync(int id);

    /// <summary>
    /// ICカードの貸出中レコードを取得
    /// </summary>
    /// <param name="cardIdm">ICカードIDm</param>
    Task<Ledger?> GetLentRecordAsync(string cardIdm);

    /// <summary>
    /// 利用履歴を登録
    /// </summary>
    Task<int> InsertAsync(Ledger ledger);

    /// <summary>
    /// 利用履歴を更新
    /// </summary>
    Task<bool> UpdateAsync(Ledger ledger);

    /// <summary>
    /// 利用履歴詳細を登録
    /// </summary>
    Task<bool> InsertDetailAsync(LedgerDetail detail);

    /// <summary>
    /// 利用履歴詳細を一括登録
    /// </summary>
    Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details);

    /// <summary>
    /// 指定日以前の利用履歴を取得（残額計算用）
    /// </summary>
    /// <param name="cardIdm">ICカードIDm</param>
    /// <param name="beforeDate">基準日</param>
    Task<Ledger?> GetLatestBeforeDateAsync(string cardIdm, DateTime beforeDate);

    /// <summary>
    /// 年度繰越残高を取得
    /// </summary>
    /// <param name="cardIdm">ICカードIDm</param>
    /// <param name="fiscalYear">年度</param>
    Task<int?> GetCarryoverBalanceAsync(string cardIdm, int fiscalYear);

    /// <summary>
    /// 指定カードの最新利用履歴を取得
    /// </summary>
    /// <param name="cardIdm">ICカードIDm</param>
    Task<Ledger?> GetLatestLedgerAsync(string cardIdm);

    /// <summary>
    /// 全カードの最新残高情報を一括取得（ダッシュボード用）
    /// </summary>
    /// <returns>カードIDmをキーとした最新残高のディクショナリ</returns>
    Task<Dictionary<string, (int Balance, DateTime? LastUsageDate)>> GetAllLatestBalancesAsync();
}
