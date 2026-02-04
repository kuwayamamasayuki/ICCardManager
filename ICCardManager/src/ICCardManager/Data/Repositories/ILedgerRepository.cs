using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
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
        Task<IEnumerable<Ledger>> GetByDateRangeAsync(string cardIdm, DateTime fromDate, DateTime toDate);

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
        Task<Ledger> GetByIdAsync(int id);

        /// <summary>
        /// ICカードの貸出中レコードを取得
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        Task<Ledger> GetLentRecordAsync(string cardIdm);

        /// <summary>
        /// 利用履歴を登録
        /// </summary>
        Task<int> InsertAsync(Ledger ledger);

        /// <summary>
        /// 利用履歴を更新
        /// </summary>
        Task<bool> UpdateAsync(Ledger ledger);

        /// <summary>
        /// 利用履歴を削除
        /// </summary>
        /// <param name="id">利用履歴ID</param>
        /// <returns>削除成功の場合true</returns>
        Task<bool> DeleteAsync(int id);

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
        Task<Ledger> GetLatestBeforeDateAsync(string cardIdm, DateTime beforeDate);

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
        Task<Ledger> GetLatestLedgerAsync(string cardIdm);

        /// <summary>
        /// 全カードの最新残高情報を一括取得（ダッシュボード用）
        /// </summary>
        /// <returns>カードIDmをキーとした最新残高のディクショナリ</returns>
        Task<Dictionary<string, (int Balance, DateTime? LastUsageDate)>> GetAllLatestBalancesAsync();

        /// <summary>
        /// 過去に入力されたバス停名を使用頻度順で取得（オートコンプリート用）
        /// </summary>
        /// <returns>バス停名と使用回数のリスト（使用頻度順）</returns>
        Task<IEnumerable<(string BusStops, int UsageCount)>> GetBusStopSuggestionsAsync();

        /// <summary>
        /// 指定期間の利用履歴をページング付きで取得
        /// </summary>
        /// <param name="cardIdm">ICカードIDm（nullの場合は全カード）</param>
        /// <param name="fromDate">開始日</param>
        /// <param name="toDate">終了日</param>
        /// <param name="page">ページ番号（1から開始）</param>
        /// <param name="pageSize">1ページあたりの件数</param>
        /// <returns>履歴リストと総件数のタプル</returns>
        Task<(IEnumerable<Ledger> Items, int TotalCount)> GetPagedAsync(
            string cardIdm,
            DateTime fromDate,
            DateTime toDate,
            int page,
            int pageSize);

        /// <summary>
        /// 指定カードの既存の履歴詳細キーを取得（重複チェック用）
        /// </summary>
        /// <remarks>
        /// Issue #326対応: 同じ履歴を二回以上登録しないための重複チェックに使用。
        /// キーは use_date + balance + is_charge の組み合わせ。
        /// FeliCa履歴では取引ごとに残高が変化するため、この組み合わせで一意に識別可能。
        /// </remarks>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <param name="fromDate">検索開始日</param>
        /// <returns>既存の履歴詳細キーのセット</returns>
        Task<HashSet<(DateTime? UseDate, int? Balance, bool IsCharge)>> GetExistingDetailKeysAsync(
            string cardIdm, DateTime fromDate);

        /// <summary>
        /// 指定カードの既存の履歴キーを取得（CSVインポート重複チェック用）
        /// </summary>
        /// <remarks>
        /// Issue #334対応: CSVインポート時に既存の履歴をスキップするための重複チェックに使用。
        /// キーは card_idm + date + summary + income + expense + balance の組み合わせ。
        /// </remarks>
        /// <param name="cardIdms">チェック対象のカードIDmリスト</param>
        /// <returns>既存の履歴キーのセット</returns>
        Task<HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>> GetExistingLedgerKeysAsync(
            IEnumerable<string> cardIdms);

        /// <summary>
        /// 利用履歴詳細を置き換え（全削除後に再登録）
        /// </summary>
        /// <remarks>
        /// Issue #484対応: 乗車履歴の統合・分割機能で、グループIDを更新する際に使用。
        /// 既存の詳細をすべて削除してから新しい詳細リストを登録する。
        /// </remarks>
        /// <param name="ledgerId">利用履歴ID</param>
        /// <param name="details">新しい詳細リスト</param>
        /// <returns>成功した場合true</returns>
        Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details);

        /// <summary>
        /// 指定カードの新規購入日を取得
        /// </summary>
        /// <remarks>
        /// Issue #501対応: 物品出納簿の作成時に、新規購入より前の月をスキップするために使用。
        /// summary = "新規購入" の最初のレコードの日付を返す。
        /// </remarks>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <returns>新規購入日、存在しない場合はnull</returns>
        Task<DateTime?> GetPurchaseDateAsync(string cardIdm);
    }
}
