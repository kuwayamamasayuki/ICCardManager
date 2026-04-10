using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// 利用履歴の読み取り専用クエリインターフェース
    /// </summary>
    /// <remarks>
    /// ILedgerRepositoryから読み取り専用メソッドを分離。
    /// 読み取りのみが必要なサービス（DashboardService, ReportDataBuilder等）は
    /// このインターフェースに依存することで、不要な書き込み操作への依存を避けられる。
    /// </remarks>
    public interface ILedgerQueryService
    {
        /// <summary>
        /// 指定期間の利用履歴を取得
        /// </summary>
        Task<IEnumerable<Ledger>> GetByDateRangeAsync(string cardIdm, DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 指定月の利用履歴を取得（帳票用）
        /// </summary>
        Task<IEnumerable<Ledger>> GetByMonthAsync(string cardIdm, int year, int month);

        /// <summary>
        /// IDで利用履歴を取得（詳細含む）
        /// </summary>
        Task<Ledger> GetByIdAsync(int id);

        /// <summary>
        /// 指定日以前の利用履歴を取得（残額計算用）
        /// </summary>
        Task<Ledger> GetLatestBeforeDateAsync(string cardIdm, DateTime beforeDate);

        /// <summary>
        /// 年度繰越残高を取得
        /// </summary>
        Task<int?> GetCarryoverBalanceAsync(string cardIdm, int fiscalYear);

        /// <summary>
        /// 指定カードの最新利用履歴を取得
        /// </summary>
        Task<Ledger> GetLatestLedgerAsync(string cardIdm);

        /// <summary>
        /// 全カードの最新残高情報を一括取得（ダッシュボード用）
        /// </summary>
        Task<Dictionary<string, (int Balance, DateTime? LastUsageDate)>> GetAllLatestBalancesAsync();

        /// <summary>
        /// 過去に入力されたバス停名をスコア順で取得（オートコンプリート用）
        /// </summary>
        Task<IEnumerable<(string BusStops, int UsageCount, DateTime? LastUsedDate)>> GetBusStopSuggestionsAsync();

        /// <summary>
        /// 指定期間の利用履歴をページング付きで取得
        /// </summary>
        Task<(IEnumerable<Ledger> Items, int TotalCount)> GetPagedAsync(
            string cardIdm, DateTime fromDate, DateTime toDate, int page, int pageSize);

        /// <summary>
        /// 指定期間のledgerに紐づく全詳細を取得（CSVエクスポート用）
        /// </summary>
        Task<List<LedgerDetail>> GetAllDetailsInDateRangeAsync(DateTime fromDate, DateTime toDate);

        /// <summary>
        /// 複数Ledgerの詳細を一括取得（残高整合性チェック用）
        /// </summary>
        Task<Dictionary<int, List<LedgerDetail>>> GetDetailsByLedgerIdsAsync(IEnumerable<int> ledgerIds);

        /// <summary>
        /// 指定カードの新規購入日（または繰越日）を取得
        /// </summary>
        Task<DateTime?> GetPurchaseDateAsync(string cardIdm);

        /// <summary>
        /// 指定カードの既存の履歴詳細キーを取得（重複チェック用）
        /// </summary>
        Task<HashSet<(DateTime? UseDate, int? Balance, bool IsCharge)>> GetExistingDetailKeysAsync(
            string cardIdm, DateTime fromDate);

        /// <summary>
        /// 指定カードの既存の履歴キーを取得（CSVインポート重複チェック用）
        /// </summary>
        Task<HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>> GetExistingLedgerKeysAsync(
            IEnumerable<string> cardIdms);
    }
}
