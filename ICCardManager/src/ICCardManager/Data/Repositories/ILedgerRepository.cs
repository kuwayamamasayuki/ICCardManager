using System;
using System.Collections.Generic;
using System.Data.SQLite;
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
        /// 利用履歴を登録（既存トランザクション参加版・Issue #1481）。
        /// </summary>
        /// <param name="ledger">登録する履歴</param>
        /// <param name="transaction">参加するトランザクション。null の場合は <see cref="InsertAsync(Ledger)"/> と同じ挙動。</param>
        /// <remarks>
        /// SMB 共有モードでヘッダ＋詳細＋関連書込みを単一トランザクションに束ねるため、
        /// 呼び出し元から <see cref="SQLiteTransaction"/> を渡せるオーバーロード。
        /// </remarks>
        Task<int> InsertAsync(Ledger ledger, SQLiteTransaction transaction);

        /// <summary>
        /// 利用履歴を更新
        /// </summary>
        Task<bool> UpdateAsync(Ledger ledger);

        /// <summary>
        /// 利用履歴を更新（既存トランザクション参加版・Issue #1481）。
        /// </summary>
        Task<bool> UpdateAsync(Ledger ledger, SQLiteTransaction transaction);

        /// <summary>
        /// 利用履歴を削除
        /// </summary>
        Task<bool> DeleteAsync(int id);

        /// <summary>
        /// 利用履歴を既存トランザクション内で削除する (Issue #1458)。
        /// Ledger 削除と監査ログ INSERT を同一 tx に統合するために使用する。
        /// </summary>
        /// <param name="id">履歴ID</param>
        /// <param name="transaction">既存トランザクション</param>
        Task<bool> DeleteAsync(int id, SQLiteTransaction transaction);

        /// <summary>
        /// 指定カードの貸出中レコードをすべて削除
        /// </summary>
        Task<int> DeleteAllLentRecordsAsync(string cardIdm);

        /// <summary>
        /// 指定カードに「貸出中」状態のレコードが、指定 ID 以外に残っているか判定する（Issue #1574）。
        /// 貸出中レコード削除後の <c>ic_card.is_lent</c> 整合性判断に使用する。
        /// </summary>
        /// <param name="cardIdm">カード IDm</param>
        /// <param name="excludeLedgerId">判定から除外する履歴 ID（通常は削除対象の ID）</param>
        Task<bool> HasOtherLentRecordsAsync(string cardIdm, int excludeLedgerId);

        /// <summary>
        /// 利用履歴詳細を登録
        /// </summary>
        Task<bool> InsertDetailAsync(LedgerDetail detail);

        /// <summary>
        /// 利用履歴詳細を登録（既存トランザクション参加版・Issue #1481）。
        /// </summary>
        Task<bool> InsertDetailAsync(LedgerDetail detail, SQLiteTransaction transaction);

        /// <summary>
        /// 利用履歴詳細を一括登録
        /// </summary>
        Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details);

        /// <summary>
        /// 利用履歴詳細を一括登録（既存トランザクション参加版・Issue #1481）。
        /// </summary>
        Task<bool> InsertDetailsAsync(int ledgerId, IEnumerable<LedgerDetail> details, SQLiteTransaction transaction);

        /// <summary>
        /// バス利用詳細のバス停名を更新する（Issue #1945）。
        /// </summary>
        /// <returns>
        /// 指定した明細をすべて更新できた場合 true。
        /// 1 件でも影響行数 0（他のパソコンや履歴詳細の全置換で rowid が振り直された等の競合、Issue #1753 / #1806）
        /// があった場合 false。
        /// <para>
        /// false のときに「1 件も反映されない」ことを保証できるのは、本メソッドが自前で
        /// トランザクションを開いた場合だけである。外側のトランザクションスコープが活性なとき
        /// （<c>DbContext.HasActiveTransactionScope</c>。これはプロセス全体のカウンタであり、
        /// 無関係な別フローのスコープでも立つ。Issue #1737）は、そのスコープへ暗黙参加するため
        /// 先行して適用済みの UPDATE は外側の commit/rollback に従う。
        /// 巻き戻しを確実にしたい呼び出し元は、<c>SQLiteTransaction</c> を受け取るオーバーロードで
        /// 自分のトランザクションを明示的に引き渡すこと（Issue #1737 の①）。
        /// </para>
        /// </returns>
        /// <remarks>
        /// 呼び出し元は戻り値を必ず確認し、false のときは <c>ledger.summary</c> の更新を行わないこと。
        /// 摘要だけが先に確定すると、6 年保存の台帳が「摘要はバス停名入り・明細は★のまま」と自己矛盾する。
        /// </remarks>
        Task<bool> UpdateDetailBusStopsAsync(int ledgerId, IEnumerable<(int SequenceNumber, string BusStops)> updates);

        /// <summary>
        /// バス利用詳細のバス停名を更新する（既存トランザクション参加版・Issue #1945）。
        /// </summary>
        /// <remarks>
        /// 摘要（<c>ledger.summary</c>）の更新と同一トランザクションで束ねるために使う（Issue #1806）。
        /// commit / rollback は呼び出し元の責務。
        /// </remarks>
        /// <returns>
        /// 指定した明細をすべて更新できた場合 true、1 件でも影響行数 0 なら false。
        /// false を返した時点で先行する明細の UPDATE は渡されたトランザクション上に適用済みのため、
        /// 呼び出し元は commit せずに巻き戻すこと（commit すると部分更新が確定する）。
        /// </returns>
        Task<bool> UpdateDetailBusStopsAsync(
            int ledgerId, IEnumerable<(int SequenceNumber, string BusStops)> updates, SQLiteTransaction transaction);

        /// <summary>
        /// 同行者数だけを更新する（Issue #1906、返却時の同行者数入力ダイアログ用）
        /// </summary>
        /// <returns>更新できた場合 true。対象行が無い（削除済み等の競合、Issue #1753）場合 false</returns>
        Task<bool> UpdateCompanionCountAsync(int ledgerId, int companionCount);
    }
}
