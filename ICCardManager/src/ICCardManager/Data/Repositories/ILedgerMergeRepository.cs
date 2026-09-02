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
        /// <remarks>
        /// <para>
        /// <paramref name="updatedTarget"/> のうち DB へ書き戻すのは<b>統合が再計算する 6 列だけ</b>
        /// （摘要 / 受入 / 払出 / 残額 / 備考 / 同行者数）。利用日・利用者・カード等は統合で変わらないため
        /// SET 句に含めない（SET 句は「その経路で本当に編集する列」に限る、Issue #1726）。
        /// </para>
        /// <para>
        /// Issue #1942: 同行者数（<c>companion_count</c>）はこの 6 列に<b>含まれる</b>。
        /// 抜けると <see cref="Services.LedgerMergeService"/> が決めた「外N名」が in-memory にしか残らず、
        /// 再読込・物品出納簿から消える一方で監査ログには記録される（記録だけが事実と異なる状態）。
        /// 列の増減は <c>LedgerMergeUpdateColumnConventionTests</c> が固定しているので、
        /// 変更するときはそちらの期待値も更新すること。
        /// </para>
        /// </remarks>
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
        /// <remarks>
        /// 統合先の復元は <see cref="MergeLedgersAsync(int, IEnumerable{int}, Ledger)"/> と<b>同じ 6 列</b>を
        /// スナップショットの値へ戻す。統合が書き換えた列を復元しないと、統合で引き上げた値が
        /// 取り消し後も残る（Issue #1942 の同行者数がこの形だった）。
        /// </remarks>
        /// <returns>
        /// 復元が確定したら true。統合後に統合先の明細が編集（<c>ledger_detail.id</c> の振り直し）・削除されていた、
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
