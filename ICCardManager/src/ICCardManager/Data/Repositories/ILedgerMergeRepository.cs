using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Data.Repositories
{
    /// <summary>
    /// 利用履歴の統合・分割操作インターフェース
    /// </summary>
    /// <remarks>
    /// ILedgerRepositoryから統合関連メソッドを分離。
    /// 統合機能のみが必要なサービス（LedgerMergeService等）は
    /// このインターフェースに依存することで、責務の境界を明確にできる。
    /// </remarks>
    public interface ILedgerMergeRepository
    {
        /// <summary>
        /// 利用履歴詳細を置き換え（全削除後に再登録）
        /// </summary>
        Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details);

        /// <summary>
        /// 複数のLedgerレコードを1つに統合する
        /// </summary>
        Task<bool> MergeLedgersAsync(int targetLedgerId, IEnumerable<int> sourceLedgerIds, Ledger updatedTarget);

        /// <summary>
        /// 統合を元に戻す
        /// </summary>
        Task<bool> UnmergeLedgersAsync(Services.LedgerMergeUndoData undoData);

        /// <summary>
        /// 統合履歴をDBに保存
        /// </summary>
        Task SaveMergeHistoryAsync(int targetLedgerId, string description, string undoDataJson);

        /// <summary>
        /// 統合履歴一覧を取得
        /// </summary>
        Task<List<(int Id, DateTime MergedAt, int TargetLedgerId, string Description, string UndoDataJson, bool IsUndone)>> GetMergeHistoriesAsync(bool undoneOnly);

        /// <summary>
        /// 統合履歴を取り消し済みにマーク
        /// </summary>
        Task MarkMergeHistoryUndoneAsync(int historyId);
    }
}
