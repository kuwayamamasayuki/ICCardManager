using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
/// <summary>
    /// 操作ログ記録サービス
    /// </summary>
    /// <remarks>
    /// Issue #1265: 操作者 IDm / 氏名は、呼び出し元引数ではなく
    /// <see cref="ICurrentOperatorContext"/> からのみ解決される。
    /// 旧シグネチャ (operatorIdm 付き) は [Obsolete] で残存するが、渡された値は無視される。
    /// </remarks>
    public class OperationLogger
    {
        private readonly IOperationLogRepository _operationLogRepository;
        private readonly ICurrentOperatorContext _operatorContext;

        /// <summary>
        /// 操作種別
        /// </summary>
        public static class Actions
        {
            public const string Insert = "INSERT";
            public const string Update = "UPDATE";
            public const string Delete = "DELETE";
            public const string Restore = "RESTORE";
            public const string Merge = "MERGE";
            public const string Split = "SPLIT";
        }

        /// <summary>
        /// GUI操作用の識別子
        /// </summary>
        public static class GuiOperator
        {
            /// <summary>
            /// GUI操作を示すIDm（16文字の16進数形式）
            /// </summary>
            public const string Idm = "0000000000000000";

            /// <summary>
            /// GUI操作を示す操作者名
            /// </summary>
            public const string Name = "GUI操作";
        }

        /// <summary>
        /// 対象テーブル名
        /// </summary>
        public static class Tables
        {
            public const string Staff = "staff";
            public const string IcCard = "ic_card";
            public const string Ledger = "ledger";
        }

        public OperationLogger(
            IOperationLogRepository operationLogRepository,
            ICurrentOperatorContext operatorContext)
        {
            _operationLogRepository = operationLogRepository;
            _operatorContext = operatorContext;
        }

        #region 新 API (operatorIdm 引数なし) — Issue #1265

        /// <summary>
        /// 職員登録のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogStaffInsertAsync(Staff staff)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Staff,
                TargetId = staff.StaffIdm,
                Action = Actions.Insert,
                BeforeData = null,
                AfterData = SerializeToJson(staff)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員更新のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogStaffUpdateAsync(Staff beforeStaff, Staff afterStaff)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Staff,
                TargetId = afterStaff.StaffIdm,
                Action = Actions.Update,
                BeforeData = SerializeToJson(beforeStaff),
                AfterData = SerializeToJson(afterStaff)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員削除のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogStaffDeleteAsync(Staff staff)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Staff,
                TargetId = staff.StaffIdm,
                Action = Actions.Delete,
                BeforeData = SerializeToJson(staff),
                AfterData = null
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員復元のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogStaffRestoreAsync(Staff staff)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Staff,
                TargetId = staff.StaffIdm,
                Action = Actions.Restore,
                BeforeData = null,
                AfterData = SerializeToJson(staff)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// ICカード登録のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogCardInsertAsync(IcCard card)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.IcCard,
                TargetId = card.CardIdm,
                Action = Actions.Insert,
                BeforeData = null,
                AfterData = SerializeToJson(card)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// ICカード更新のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogCardUpdateAsync(IcCard beforeCard, IcCard afterCard)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.IcCard,
                TargetId = afterCard.CardIdm,
                Action = Actions.Update,
                BeforeData = SerializeToJson(beforeCard),
                AfterData = SerializeToJson(afterCard)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// ICカード削除のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogCardDeleteAsync(IcCard card)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.IcCard,
                TargetId = card.CardIdm,
                Action = Actions.Delete,
                BeforeData = SerializeToJson(card),
                AfterData = null
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// ICカード復元のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogCardRestoreAsync(IcCard card)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.IcCard,
                TargetId = card.CardIdm,
                Action = Actions.Restore,
                BeforeData = null,
                AfterData = SerializeToJson(card)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴更新のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogLedgerUpdateAsync(Ledger beforeLedger, Ledger afterLedger)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = afterLedger.Id.ToString(),
                Action = Actions.Update,
                BeforeData = SerializeToJson(beforeLedger),
                AfterData = SerializeToJson(afterLedger)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴挿入のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogLedgerInsertAsync(Ledger ledger)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = ledger.Id.ToString(),
                Action = Actions.Insert,
                BeforeData = null,
                AfterData = SerializeToJson(ledger)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴削除のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogLedgerDeleteAsync(Ledger ledger)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = ledger.Id.ToString(),
                Action = Actions.Delete,
                BeforeData = SerializeToJson(ledger),
                AfterData = null
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴統合のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogLedgerMergeAsync(IReadOnlyList<Ledger> sourceLedgers, Ledger mergedLedger)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = mergedLedger.Id.ToString(),
                Action = Actions.Merge,
                BeforeData = SerializeToJson(sourceLedgers),
                AfterData = SerializeToJson(mergedLedger)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴分割のログを記録。操作者情報は <see cref="ICurrentOperatorContext"/> から自動取得する。
        /// </summary>
        public async Task LogLedgerSplitAsync(Ledger originalLedger, IReadOnlyList<Ledger> splitLedgers)
        {
            var (idm, name) = ResolveOperator();
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = originalLedger.Id.ToString(),
                Action = Actions.Split,
                BeforeData = SerializeToJson(originalLedger),
                AfterData = SerializeToJson(splitLedgers)
            }).ConfigureAwait(false);
        }

        #endregion

        #region 旧 API (operatorIdm 引数付き) — 後方互換のため残存。引数は無視される (Issue #1265)

        private const string ObsoleteMessage =
            "Issue #1265: operatorIdm パラメータは監査ログなりすまし防止のため無視されます。" +
            " ICurrentOperatorContext（StaffAuthService が職員証タッチ成功時に自動設定）経由で操作者を解決します。" +
            " operatorIdm 引数を取らないオーバーロードに移行してください。";

        /// <inheritdoc cref="LogStaffInsertAsync(Staff)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogStaffInsertAsync(string? operatorIdm, Staff staff) => LogStaffInsertAsync(staff);

        /// <inheritdoc cref="LogStaffUpdateAsync(Staff, Staff)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogStaffUpdateAsync(string? operatorIdm, Staff beforeStaff, Staff afterStaff) =>
            LogStaffUpdateAsync(beforeStaff, afterStaff);

        /// <inheritdoc cref="LogStaffDeleteAsync(Staff)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogStaffDeleteAsync(string? operatorIdm, Staff staff) => LogStaffDeleteAsync(staff);

        /// <inheritdoc cref="LogStaffRestoreAsync(Staff)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogStaffRestoreAsync(string? operatorIdm, Staff staff) => LogStaffRestoreAsync(staff);

        /// <inheritdoc cref="LogCardInsertAsync(IcCard)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogCardInsertAsync(string? operatorIdm, IcCard card) => LogCardInsertAsync(card);

        /// <inheritdoc cref="LogCardUpdateAsync(IcCard, IcCard)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogCardUpdateAsync(string? operatorIdm, IcCard beforeCard, IcCard afterCard) =>
            LogCardUpdateAsync(beforeCard, afterCard);

        /// <inheritdoc cref="LogCardDeleteAsync(IcCard)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogCardDeleteAsync(string? operatorIdm, IcCard card) => LogCardDeleteAsync(card);

        /// <inheritdoc cref="LogCardRestoreAsync(IcCard)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogCardRestoreAsync(string? operatorIdm, IcCard card) => LogCardRestoreAsync(card);

        /// <inheritdoc cref="LogLedgerUpdateAsync(Ledger, Ledger)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogLedgerUpdateAsync(string? operatorIdm, Ledger beforeLedger, Ledger afterLedger) =>
            LogLedgerUpdateAsync(beforeLedger, afterLedger);

        /// <inheritdoc cref="LogLedgerInsertAsync(Ledger)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogLedgerInsertAsync(string? operatorIdm, Ledger ledger) => LogLedgerInsertAsync(ledger);

        /// <inheritdoc cref="LogLedgerDeleteAsync(Ledger)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogLedgerDeleteAsync(string? operatorIdm, Ledger ledger) => LogLedgerDeleteAsync(ledger);

        /// <inheritdoc cref="LogLedgerMergeAsync(IReadOnlyList{Ledger}, Ledger)"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogLedgerMergeAsync(string? operatorIdm, IReadOnlyList<Ledger> sourceLedgers, Ledger mergedLedger) =>
            LogLedgerMergeAsync(sourceLedgers, mergedLedger);

        /// <inheritdoc cref="LogLedgerSplitAsync(Ledger, IReadOnlyList{Ledger})"/>
        [Obsolete(ObsoleteMessage)]
        public Task LogLedgerSplitAsync(string? operatorIdm, Ledger originalLedger, IReadOnlyList<Ledger> splitLedgers) =>
            LogLedgerSplitAsync(originalLedger, splitLedgers);

        #endregion

        /// <summary>
        /// <see cref="ICurrentOperatorContext"/> から現在の操作者を解決する。
        /// セッション無効時は GUI 操作として扱う。
        /// </summary>
        private (string idm, string name) ResolveOperator()
        {
            if (_operatorContext.HasSession)
            {
                return (_operatorContext.CurrentIdm!, _operatorContext.CurrentName!);
            }
            return (GuiOperator.Idm, GuiOperator.Name);
        }

        /// <summary>
        /// オブジェクトをJSON文字列にシリアライズ
        /// </summary>
        private static string SerializeToJson<T>(T obj)
        {
            return JsonSerializer.Serialize(obj, new JsonSerializerOptions
            {
                WriteIndented = false,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
    }
}
