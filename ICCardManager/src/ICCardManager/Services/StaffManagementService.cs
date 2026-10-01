using System;
using System.Threading.Tasks;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
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
