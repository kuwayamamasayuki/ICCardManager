#nullable enable
using System;
using System.Threading.Tasks;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// 職員の登録・更新・削除・復元を、監査ログと 1 つのトランザクションで確定させる（Issue #2156）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 以前は <c>StaffManageViewModel</c> が <c>staff</c> への書き込みの<b>コミット後に</b>
    /// <see cref="OperationLogger"/> を呼んでおり、監査ログの書き込みだけが失敗すると
    /// 「操作は反映されたのに記録が残らない」状態が確定した。交通系ICカード側
    /// （<see cref="CardManagementService"/>）と同じ手順（<see cref="AuditedWriteTransaction"/>）で書く。
    /// </para>
    /// <para>
    /// 「更新前のデータ」を読んで書き込みの可否を決める判断（Issue #1760）と、競合の案内の組み立ては
    /// 呼び出し元（ViewModel）の責務のまま残す。本クラスはリポジトリと同じ <c>bool</c> を返す。
    /// </para>
    /// </remarks>
    public class StaffManagementService
    {
        private readonly DbContext _dbContext;
        private readonly IStaffRepository _staffRepository;
        private readonly OperationLogger _operationLogger;
        private readonly ILogger<StaffManagementService> _logger;

        public StaffManagementService(
            DbContext dbContext,
            IStaffRepository staffRepository,
            OperationLogger operationLogger,
            ILogger<StaffManagementService> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _staffRepository = staffRepository ?? throw new ArgumentNullException(nameof(staffRepository));
            // 既定値を持たない必須引数にする（#1820 / #2151 と同じ判断）
            _operationLogger = operationLogger ?? throw new ArgumentNullException(nameof(operationLogger));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 職員の登録・更新・復元に、操作者の職員証認証を求めるか（Issue #2263）
        /// </summary>
        /// <returns>
        /// 登録済み（論理削除されていない）職員が 1 人以上いれば <c>true</c>。
        /// 0 人（初回セットアップ）なら <c>false</c> で、認証を免除する
        /// </returns>
        /// <remarks>
        /// <para>
        /// 職員証の認証は登録済みの職員証でしか通らない（<c>StaffAuthDialog</c>）。職員が 0 人のときに認証を
        /// 求めると誰も認証できず、1 人目を登録する手段が無くなるため、この場合だけ免除する。
        /// 免除して行った登録・復元は、操作ログ上「GUI操作」として記録される（認証した職員がいないため）。
        /// </para>
        /// <para>
        /// 免除の判定は本メソッドの 1 か所に置く。画面ごとに「一覧が空か」で判定すると、経路によって
        /// 判定の根拠（一覧のキャッシュ・削除済みを含むか）が食い違う。判定は操作を行う時点（保存を押した時点）
        /// の DB の状態で行い、画面を開いた時点の状態は使わない。
        /// </para>
        /// <para>
        /// 認証の目的は操作者の記録であって権限制御ではない（ロールの区別は無い）。登録済みの職員なら
        /// 誰でも認証を通る。
        /// </para>
        /// </remarks>
        public async Task<bool> RequiresOperatorAuthenticationAsync()
        {
            return await _staffRepository.HasActiveStaffAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 職員を登録し、監査ログ（INSERT）を同じトランザクションで記録する
        /// </summary>
        /// <param name="staff">登録する職員。監査ログの変更後データにもなる</param>
        /// <returns>登録できたら <c>true</c>。<c>false</c> は一過性でない書き込み失敗（リポジトリが記録済み）</returns>
        public async Task<bool> RegisterAsync(Staff staff)
        {
            if (staff == null)
            {
                throw new ArgumentNullException(nameof(staff));
            }

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "職員の登録",
                    tx => _staffRepository.InsertAsync(staff, tx),
                    inserted => inserted,
                    tx => _operationLogger.LogStaffInsertAsync(staff, tx)).ConfigureAwait(false);
            }
            finally
            {
                // トランザクションを渡した書き込みはキャッシュを破棄しない。コミット・ロールバックの後で破棄する
                // （失敗時も、一覧を再読込する呼び出し元へ古い一覧を返さないため。Issue #1759）
                _staffRepository.InvalidateCache();
            }
        }

        /// <summary>
        /// 職員情報（氏名・職員番号・備考）を更新し、監査ログ（UPDATE）を同じトランザクションで記録する
        /// </summary>
        /// <param name="beforeStaff">更新前に読み取った職員。監査ログの変更前データになる</param>
        /// <param name="afterStaff">更新後の職員。監査ログの変更後データになる</param>
        /// <returns>
        /// 更新できたら <c>true</c>。<c>false</c> は競合（編集中に他のパソコンや別の操作で削除された）で、
        /// 監査ログは書かれない
        /// </returns>
        public async Task<bool> UpdateAsync(Staff beforeStaff, Staff afterStaff)
        {
            if (beforeStaff == null)
            {
                throw new ArgumentNullException(nameof(beforeStaff));
            }

            if (afterStaff == null)
            {
                throw new ArgumentNullException(nameof(afterStaff));
            }

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "職員の更新",
                    tx => _staffRepository.UpdateAsync(afterStaff, tx),
                    updated => updated,
                    tx => _operationLogger.LogStaffUpdateAsync(beforeStaff, afterStaff, tx)).ConfigureAwait(false);
            }
            finally
            {
                _staffRepository.InvalidateCache();
            }
        }

        /// <summary>
        /// 職員を論理削除し、監査ログ（DELETE）を同じトランザクションで記録する
        /// </summary>
        /// <param name="staff">削除前に読み取った職員。監査ログの変更前データになる</param>
        /// <returns>
        /// 削除できたら <c>true</c>。<c>false</c> は競合（他のパソコンや別の操作で先に削除された）で、
        /// 監査ログは書かれない
        /// </returns>
        public async Task<bool> DeleteAsync(Staff staff)
        {
            if (staff == null)
            {
                throw new ArgumentNullException(nameof(staff));
            }

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "職員の削除",
                    tx => _staffRepository.DeleteAsync(staff.StaffIdm, tx),
                    deleted => deleted,
                    tx => _operationLogger.LogStaffDeleteAsync(staff, tx)).ConfigureAwait(false);
            }
            finally
            {
                _staffRepository.InvalidateCache();
            }
        }

        /// <summary>
        /// 論理削除された職員を復元し、監査ログ（RESTORE）を同じトランザクションで記録する
        /// </summary>
        /// <param name="deletedStaff">復元前に読み取った職員（<c>includeDeleted: true</c> で取得したもの）</param>
        /// <returns>
        /// 復元できたら <c>true</c>。<c>false</c> は競合（他のパソコンや別の操作で先に復元された）で、
        /// 監査ログは書かれない
        /// </returns>
        /// <remarks>
        /// 監査ログの変更後データは同じトランザクションの中で、復元前のデータから組み立てる
        /// （<see cref="CreateRestoredSnapshot"/>。理由は <see cref="CardManagementService.RestoreAsync"/> と同じ）。
        /// </remarks>
        public async Task<bool> RestoreAsync(Staff deletedStaff)
        {
            if (deletedStaff == null)
            {
                throw new ArgumentNullException(nameof(deletedStaff));
            }

            try
            {
                return await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "職員の復元",
                    tx => _staffRepository.RestoreAsync(deletedStaff.StaffIdm, tx),
                    restored => restored,
                    tx => _operationLogger.LogStaffRestoreAsync(CreateRestoredSnapshot(deletedStaff), tx))
                    .ConfigureAwait(false);
            }
            finally
            {
                _staffRepository.InvalidateCache();
            }
        }

        /// <summary>
        /// 職員証の差し替え（Issue #2461）ができるかを確かめる（書き込みはしない）
        /// </summary>
        /// <param name="currentIdm">差し替える職員の今の職員証の IDm</param>
        /// <param name="newIdm">タッチした新しい職員証の IDm</param>
        public async Task<StaffCardReplacementResult> CheckCardReplacementAsync(string currentIdm, string newIdm)
        {
            ValidateReplacementArguments(currentIdm, newIdm);
            return await _staffRepository.CheckStaffIdmReplacementAsync(currentIdm, newIdm).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員証を差し替え（Issue #2461）、監査ログ（UPDATE。変更前・変更後の職員証IDm）を同じトランザクションで記録する
        /// </summary>
        /// <param name="currentIdm">差し替える職員の今の職員証の IDm</param>
        /// <param name="newIdm">新しい職員証の IDm</param>
        /// <returns>
        /// 差し替えたら <see cref="StaffCardReplacementResult.IsOk"/>。そうでなければ何も確定していない（監査ログも書かない）。
        /// 理由は <see cref="StaffCardReplacementResult.Status"/>（確認の後で他のパソコンが同じ職員証を登録した・
        /// 対象の職員を削除した等。判定はトランザクションの中でやり直す）
        /// </returns>
        /// <remarks>
        /// 監査ログは既存の職員情報の変更と同じ形（対象 <c>staff</c>・操作 UPDATE・対象 ID は変更後の IDm）で 1 件書き、
        /// 変更前データに古い職員証の IDm、変更後データに新しい職員証の IDm が載る。過去の操作ログは書き換えない。
        /// </remarks>
        public async Task<StaffCardReplacementResult> ReplaceCardAsync(string currentIdm, string newIdm)
        {
            ValidateReplacementArguments(currentIdm, newIdm);

            try
            {
                // 監査ログには書き込みが返した変更前・変更後が要る。RunAsync は監査ログの書き込みへ結果を渡さないので、
                // 書き込みのラムダで受けておく（共有モードのリトライでは試行ごとに上書きされる）
                StaffCardReplacementResult? replaced = null;
                var result = await AuditedWriteTransaction.RunAsync(
                    _dbContext, _logger, "職員証の差し替え",
                    async tx => replaced = await _staffRepository.ReplaceStaffIdmAsync(currentIdm, newIdm, tx)
                        .ConfigureAwait(false),
                    r => r.IsOk,
                    tx => replaced is { Before: { } before, After: { } after }
                        ? _operationLogger.LogStaffUpdateAsync(before, after, tx)
                        : throw new InvalidOperationException(
                            "職員証の差し替えの監査ログを、差し替えの結果（変更前・変更後）なしに書こうとしました。"))
                    .ConfigureAwait(false);

                if (result.IsOk)
                {
                    // IDm はログへ生で出さない（logging.md / Issue #1852）
                    _logger.LogInformation(
                        "職員証を差し替えました: {CurrentIdm} → {NewIdm}（台帳の貸出者 {LedgerRows} 行・交通系ICカードの最終貸出者 {CardRows} 枚・統合履歴 {MergeHistoryRows} 件を付け替え）",
                        IdmMasker.Mask(currentIdm), IdmMasker.Mask(newIdm),
                        result.LedgerRowsUpdated, result.CardRowsUpdated, result.MergeHistoryRowsUpdated);
                }
                else
                {
                    _logger.LogInformation(
                        "職員証の差し替えを行いませんでした: {CurrentIdm} → {NewIdm}（{Status}）",
                        IdmMasker.Mask(currentIdm), IdmMasker.Mask(newIdm), result.Status);
                }

                return result;
            }
            finally
            {
                // 差し替えは職員一覧と交通系ICカード（最終貸出者）の両方を書き換える。失敗時も破棄する（Issue #1759）
                _staffRepository.InvalidateCachesAffectedByStaffIdmReplacement();
            }
        }

        private static void ValidateReplacementArguments(string currentIdm, string newIdm)
        {
            if (string.IsNullOrEmpty(currentIdm))
            {
                throw new ArgumentException("差し替える職員の職員証の IDm が空です。", nameof(currentIdm));
            }

            if (string.IsNullOrEmpty(newIdm))
            {
                throw new ArgumentException("新しい職員証の IDm が空です。", nameof(newIdm));
            }
        }

        /// <summary>
        /// 復元後の職員の状態を、復元前に読み取ったデータから組み立てる（操作ログの変更後データ）
        /// </summary>
        /// <param name="deletedStaff">復元前に読み取った職員</param>
        /// <remarks>
        /// <c>RestoreAsync</c> が変えるのは <c>is_deleted</c> / <c>deleted_at</c> の 2 列だけなので、
        /// それ以外は復元前の値をそのまま引き継ぐ（Issue #1760）。
        /// </remarks>
        internal static Staff CreateRestoredSnapshot(Staff deletedStaff)
        {
            return new Staff
            {
                StaffIdm = deletedStaff.StaffIdm,
                Name = deletedStaff.Name,
                Number = deletedStaff.Number,
                Note = deletedStaff.Note,
                IsDeleted = false,
                DeletedAt = null
            };
        }
    }
}
