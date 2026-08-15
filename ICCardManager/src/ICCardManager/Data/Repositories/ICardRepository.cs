using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Models;
using System.Data.SQLite;

namespace ICCardManager.Data.Repositories
{
/// <summary>
    /// 交通系ICカードリポジトリインターフェース
    /// </summary>
    public interface ICardRepository
    {
        /// <summary>
        /// 全ICカードを取得（論理削除されていないもののみ）
        /// </summary>
        Task<IEnumerable<IcCard>> GetAllAsync();

        /// <summary>
        /// 全ICカードを取得（論理削除されたものを含む）
        /// </summary>
        Task<IEnumerable<IcCard>> GetAllIncludingDeletedAsync();

        /// <summary>
        /// カード関連のキャッシュを破棄する
        /// </summary>
        /// <remarks>
        /// Issue #1760: 競合を検出したが<b>書き込みを 1 回も行わなかった</b>経路から呼ぶ
        /// （更新前データを読めず更新自体を中止した等）。書き込み経路では影響行数 0 のときに
        /// <c>UpdateAsync</c> / <c>RestoreAsync</c> が内部で破棄する（Issue #1759）が、
        /// 書き込みを行わない経路にはその契機が無い。<see cref="GetAllAsync"/> はキャッシュ経由
        /// （既定 TTL 60 秒／共有モード 15 秒）のため、破棄しないと UI が案内どおり一覧を
        /// 再読込しても削除済みのカードが並んだままになり、
        /// 「一覧を再読み込みしました」という文言が事実にならない。
        /// </remarks>
        void InvalidateCache();

        /// <summary>
        /// 貸出可能なICカードを取得
        /// </summary>
        /// <param name="bypassCache">
        /// Issue #1167: trueの場合、キャッシュをバイパスしてDBから直接読み取る。
        /// 共有モード時に他PCの最新の貸出状態を反映する必要がある場合に使用する。
        /// </param>
        Task<IEnumerable<IcCard>> GetAvailableAsync(bool bypassCache = false);

        /// <summary>
        /// 貸出中のICカードを取得
        /// </summary>
        /// <param name="bypassCache">
        /// Issue #1167: trueの場合、キャッシュをバイパスしてDBから直接読み取る。
        /// 共有モード時に他PCの最新の貸出状態を反映する必要がある場合に使用する。
        /// </param>
        Task<IEnumerable<IcCard>> GetLentAsync(bool bypassCache = false);

        /// <summary>
        /// IDmでICカードを取得
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <param name="includeDeleted">論理削除されたものも含めるか</param>
        Task<IcCard> GetByIdmAsync(string cardIdm, bool includeDeleted = false);

        /// <summary>
        /// ICカードを登録
        /// </summary>
        /// <remarks>
        /// Issue #1757: カード種別＋管理番号の UNIQUE 制約（<c>idx_card_type_number_active</c>）に
        /// 触れる 3 経路（登録・更新・復元）は、いずれも制約違反を
        /// <see cref="DuplicateCardNumberException"/> へ変換して投げる。呼び出し元は
        /// <c>bool</c> の戻り値だけでなく本例外も扱うこと（未捕捉のまま UI へ抜けると、
        /// 操作の文脈から切り離されたモーダルダイアログになる）。
        /// </remarks>
        /// <exception cref="DuplicateCardNumberException">
        /// 同一種別で同一管理番号の有効なカードが既に存在する場合
        /// </exception>
        Task<bool> InsertAsync(IcCard card);

        /// <summary>
        /// ICカードを登録（トランザクション対応）
        /// </summary>
        /// <exception cref="DuplicateCardNumberException">
        /// 同一種別で同一管理番号の有効なカードが既に存在する場合（<see cref="InsertAsync(IcCard)"/> と同じ）
        /// </exception>
        Task<bool> InsertAsync(IcCard card, SQLiteTransaction transaction);

        /// <summary>
        /// ICカード情報（カード種別・管理番号・備考）を更新
        /// </summary>
        /// <remarks>
        /// Issue #1726: 更新対象は <see cref="IcCard.CardType"/> / <see cref="IcCard.CardNumber"/> /
        /// <see cref="IcCard.Note"/> の 3 項目のみ。
        /// <see cref="IcCard.StartingPageNumber"/> / <see cref="IcCard.CarryoverIncomeTotal"/> /
        /// <see cref="IcCard.CarryoverExpenseTotal"/> / <see cref="IcCard.CarryoverFiscalYear"/> は
        /// 登録時にのみ確定する値（編集 UI を持たない）ため、本メソッドでは**更新しない**。
        /// これらを引数の <paramref name="card"/> に設定しても DB へは反映されない。
        /// 同様に <see cref="IcCard.IsLent"/> 系は <see cref="UpdateLentStatusAsync"/>、
        /// <see cref="IcCard.IsRefunded"/> 系は <see cref="SetRefundedAsync"/>、
        /// <see cref="IcCard.IsDeleted"/> 系は <see cref="DeleteAsync"/> / <see cref="RestoreAsync(string)"/>
        /// が担当する専用列で、本メソッドの更新対象ではない。
        /// 詳細は <c>.claude/rules/development-conventions.md</c> の
        /// 「UPDATE の SET 句は『その経路で本当に編集する列』に限る」および
        /// <c>docs/design/05_クラス設計書.md</c> §5.5b を参照。
        /// </remarks>
        /// <exception cref="DuplicateCardNumberException">
        /// 変更後のカード種別＋管理番号を、有効な別のカードが既に使用している場合（Issue #1757）
        /// </exception>
        Task<bool> UpdateAsync(IcCard card);

        /// <summary>
        /// ICカード情報（カード種別・管理番号・備考）を更新（トランザクション対応）
        /// </summary>
        /// <remarks>
        /// 更新対象列の範囲は <see cref="UpdateAsync(IcCard)"/> と同じ（Issue #1726）。
        /// </remarks>
        /// <exception cref="DuplicateCardNumberException">
        /// <see cref="UpdateAsync(IcCard)"/> と同じ（Issue #1757）
        /// </exception>
        Task<bool> UpdateAsync(IcCard card, SQLiteTransaction transaction);

        /// <summary>
        /// 貸出状態を更新
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <param name="isLent">貸出状態</param>
        /// <param name="lentAt">貸出日時（貸出時のみ）</param>
        /// <param name="staffIdm">貸出者IDm（貸出時のみ）</param>
        Task<bool> UpdateLentStatusAsync(string cardIdm, bool isLent, DateTime? lentAt, string staffIdm);

        /// <summary>
        /// ICカードを論理削除
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <returns>操作結果（成功/未存在/貸出中/競合）</returns>
        Task<CardOperationResult> DeleteAsync(string cardIdm);

        /// <summary>
        /// 論理削除されたICカードを復元
        /// </summary>
        /// <remarks>
        /// Issue #1757: 削除済みカードの管理番号は再利用できる（部分ユニークインデックスは
        /// <c>is_deleted = 0</c> のみ対象）ため、削除中に同じ種別・番号のカードが登録されていると
        /// 復元が UNIQUE 制約違反になる。番号を指定し直せない経路のため、例外の
        /// <c>UserFriendlyMessage</c> は「使用中カードの番号を変更してから復元する」案内になる。
        /// </remarks>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <exception cref="DuplicateCardNumberException">
        /// 復元対象の種別＋管理番号を、有効な別のカードが既に使用している場合
        /// </exception>
        Task<bool> RestoreAsync(string cardIdm);

        /// <summary>
        /// 論理削除されたICカードを復元（トランザクション対応）
        /// </summary>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <param name="transaction">SQLiteトランザクション</param>
        /// <exception cref="DuplicateCardNumberException">
        /// <see cref="RestoreAsync(string)"/> と同じ（Issue #1757）
        /// </exception>
        Task<bool> RestoreAsync(string cardIdm, SQLiteTransaction transaction);

        /// <summary>
        /// IDmが存在するか確認
        /// </summary>
        Task<bool> ExistsAsync(string cardIdm);

        /// <summary>
        /// 次の管理番号を取得
        /// </summary>
        /// <param name="cardType">カード種別</param>
        Task<string> GetNextCardNumberAsync(string cardType);

        /// <summary>
        /// ICカードを払戻済状態に設定（Issue #530）
        /// </summary>
        /// <remarks>
        /// 払戻済カードは論理削除と異なり、帳票作成時には引き続き選択可能。
        /// ただし、貸出対象からは除外される。
        /// </remarks>
        /// <param name="cardIdm">ICカードIDm</param>
        /// <returns>操作結果（成功/未存在/貸出中/競合）</returns>
        Task<CardOperationResult> SetRefundedAsync(string cardIdm);
    }
}
