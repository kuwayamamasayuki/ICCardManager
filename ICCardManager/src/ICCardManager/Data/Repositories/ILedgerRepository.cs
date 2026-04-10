using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// 利用履歴リポジトリインターフェース（CRUD + クエリ + 統合の統合インターフェース）
    /// </summary>
    /// <remarks>
    /// ILedgerQueryService（読み取り専用）と ILedgerMergeRepository（統合操作）を継承し、
    /// CRUD操作を直接定義する。既存コードはこのインターフェースを通じて全機能にアクセスでき、
    /// 新規コードは必要な狭いインターフェースのみに依存できる。
    /// </remarks>
    public interface ILedgerRepository : ILedgerQueryService, ILedgerMergeRepository
    {
        // === CRUD操作（ILedgerRepository固有） ===

        /// <summary>
        /// ICカードの貸出中レコードを取得
        /// </summary>
        Task<Ledger> GetLentRecordAsync(string cardIdm);

        /// <summary>
        /// 全カードの貸出中レコードを一括取得（整合性チェック用）
        /// </summary>
        Task<List<Ledger>> GetAllLentRecordsAsync();

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
        Task<bool> DeleteAsync(int id);

        /// <summary>
        /// 指定カードの貸出中レコードをすべて削除
        /// </summary>
        Task<int> DeleteAllLentRecordsAsync(string cardIdm);

        /// <summary>
        /// 利用履歴詳細を登録
        /// </summary>
        Task<bool> InsertDetailAsync(LedgerDetail detail);

        /// <summary>
        /// 利用履歴詳細を一括登録
        /// </summary>
        Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details);

        /// <summary>
        /// バス利用詳細のバス停名を更新
        /// </summary>
        Task UpdateDetailBusStopsAsync(int ledgerId, IEnumerable<(int SequenceNumber, string BusStops)> updates);
    }
}
