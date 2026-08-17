using System;
using System.Collections.Generic;
using System.Data.SQLite;
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
        /// 利用履歴詳細を既存トランザクション内で置き換える (Issue #1458)。
        /// </summary>
        Task<bool> ReplaceDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction);

        /// <summary>
        /// 複数のLedgerレコードを1つに統合する
        /// </summary>
        Task<bool> MergeLedgersAsync(int targetLedgerId, IEnumerable<int> sourceLedgerIds, Ledger updatedTarget);

        /// <summary>
        /// 複数のLedgerレコードを既存トランザクション内で統合する (Issue #1458)。
        /// 監査ログ INSERT と同一 tx で実行することで fsync 1 回分の往復を削減する。
        /// </summary>
        Task<bool> MergeLedgersAsync(
            int targetLedgerId,
            IEnumerable<int> sourceLedgerIds,
            Ledger updatedTarget,
            SQLiteTransaction transaction);

        /// <summary>
        /// 統合を元に戻す（自前のトランザクションで確定する）
        /// </summary>
        /// <returns>
        /// 復元が確定したら true。統合後に統合先の明細が編集（rowid 振り直し）・削除されていた、
        /// または統合先そのものが削除されていた場合は競合として false（何も書き込まない。Issue #1806）
        /// </returns>
        Task<bool> UnmergeLedgersAsync(Services.LedgerMergeUndoData undoData);

        /// <summary>
        /// 統合を既存トランザクション内で元に戻す（Issue #1806）。
        /// 「取り消し済み」マーク（<see cref="MarkMergeHistoryUndoneAsync(int, SQLiteTransaction)"/>）と
        /// 同一トランザクションで確定させるために使う。commit / rollback は呼び出し元の責務。
        /// </summary>
        /// <returns><see cref="UnmergeLedgersAsync(Services.LedgerMergeUndoData)"/> と同じ</returns>
        Task<bool> UnmergeLedgersAsync(Services.LedgerMergeUndoData undoData, SQLiteTransaction transaction);

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
        /// <returns>
        /// 1 行更新できたら true。既に取り消し済み（他の PC・他の操作が先にマークした競合）なら false（Issue #1806）
        /// </returns>
        Task<bool> MarkMergeHistoryUndoneAsync(int historyId);

        /// <summary>
        /// 統合履歴を既存トランザクション内で取り消し済みにマーク（Issue #1806）。
        /// 台帳の復元（<see cref="UnmergeLedgersAsync(Services.LedgerMergeUndoData, SQLiteTransaction)"/>）と
        /// 同一トランザクションで確定させるために使う。commit / rollback は呼び出し元の責務。
        /// </summary>
        /// <returns><see cref="MarkMergeHistoryUndoneAsync(int)"/> と同じ</returns>
        Task<bool> MarkMergeHistoryUndoneAsync(int historyId, SQLiteTransaction transaction);
    }
}
