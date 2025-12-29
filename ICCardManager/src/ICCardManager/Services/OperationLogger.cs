using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
/// <summary>
    /// 操作ログ記録サービス
    /// </summary>
    public class OperationLogger
    {
        private readonly IOperationLogRepository _operationLogRepository;
        private readonly IStaffRepository _staffRepository;

        /// <summary>
        /// 操作種別
        /// </summary>
        public static class Actions
        {
            public const string Insert = "INSERT";
            public const string Update = "UPDATE";
            public const string Delete = "DELETE";
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
            IStaffRepository staffRepository)
        {
            _operationLogRepository = operationLogRepository;
            _staffRepository = staffRepository;
        }

        /// <summary>
        /// 職員登録のログを記録
        /// </summary>
        public async Task LogStaffInsertAsync(string operatorIdm, Staff staff)
        {
            var operatorName = await GetOperatorNameAsync(operatorIdm);

            var log = new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = operatorIdm,
                OperatorName = operatorName,
                TargetTable = Tables.Staff,
                TargetId = staff.StaffIdm,
                Action = Actions.Insert,
                BeforeData = null,
                AfterData = SerializeToJson(staff)
            };

            await _operationLogRepository.InsertAsync(log);
        }

        /// <summary>
        /// 職員更新のログを記録
        /// </summary>
        public async Task LogStaffUpdateAsync(string operatorIdm, Staff beforeStaff, Staff afterStaff)
        {
            var operatorName = await GetOperatorNameAsync(operatorIdm);

            var log = new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = operatorIdm,
                OperatorName = operatorName,
                TargetTable = Tables.Staff,
                TargetId = afterStaff.StaffIdm,
                Action = Actions.Update,
                BeforeData = SerializeToJson(beforeStaff),
                AfterData = SerializeToJson(afterStaff)
            };

            await _operationLogRepository.InsertAsync(log);
        }

        /// <summary>
        /// 職員削除のログを記録
        /// </summary>
        public async Task LogStaffDeleteAsync(string operatorIdm, Staff staff)
        {
            var operatorName = await GetOperatorNameAsync(operatorIdm);

            var log = new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = operatorIdm,
                OperatorName = operatorName,
                TargetTable = Tables.Staff,
                TargetId = staff.StaffIdm,
                Action = Actions.Delete,
                BeforeData = SerializeToJson(staff),
                AfterData = null
            };

            await _operationLogRepository.InsertAsync(log);
        }

        /// <summary>
        /// ICカード登録のログを記録
        /// </summary>
        public async Task LogCardInsertAsync(string operatorIdm, IcCard card)
        {
            var operatorName = await GetOperatorNameAsync(operatorIdm);

            var log = new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = operatorIdm,
                OperatorName = operatorName,
                TargetTable = Tables.IcCard,
                TargetId = card.CardIdm,
                Action = Actions.Insert,
                BeforeData = null,
                AfterData = SerializeToJson(card)
            };

            await _operationLogRepository.InsertAsync(log);
        }

        /// <summary>
        /// ICカード更新のログを記録
        /// </summary>
        public async Task LogCardUpdateAsync(string operatorIdm, IcCard beforeCard, IcCard afterCard)
        {
            var operatorName = await GetOperatorNameAsync(operatorIdm);

            var log = new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = operatorIdm,
                OperatorName = operatorName,
                TargetTable = Tables.IcCard,
                TargetId = afterCard.CardIdm,
                Action = Actions.Update,
                BeforeData = SerializeToJson(beforeCard),
                AfterData = SerializeToJson(afterCard)
            };

            await _operationLogRepository.InsertAsync(log);
        }

        /// <summary>
        /// ICカード削除のログを記録
        /// </summary>
        public async Task LogCardDeleteAsync(string operatorIdm, IcCard card)
        {
            var operatorName = await GetOperatorNameAsync(operatorIdm);

            var log = new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = operatorIdm,
                OperatorName = operatorName,
                TargetTable = Tables.IcCard,
                TargetId = card.CardIdm,
                Action = Actions.Delete,
                BeforeData = SerializeToJson(card),
                AfterData = null
            };

            await _operationLogRepository.InsertAsync(log);
        }

        /// <summary>
        /// 履歴更新のログを記録
        /// </summary>
        /// <param name="operatorIdm">操作者IDm（nullまたは空文字列の場合はGUI操作として記録）</param>
        /// <param name="beforeLedger">変更前の履歴データ</param>
        /// <param name="afterLedger">変更後の履歴データ</param>
        public async Task LogLedgerUpdateAsync(string? operatorIdm, Ledger beforeLedger, Ledger afterLedger)
        {
            // GUI操作（operatorIdmがnullまたは空）の場合はGUI用識別子を使用
            var isGuiOperation = string.IsNullOrEmpty(operatorIdm);
            var actualIdm = isGuiOperation ? GuiOperator.Idm : operatorIdm;
            var operatorName = isGuiOperation ? GuiOperator.Name : await GetOperatorNameAsync(operatorIdm!);

            var log = new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = actualIdm,
                OperatorName = operatorName,
                TargetTable = Tables.Ledger,
                TargetId = afterLedger.Id.ToString(),
                Action = Actions.Update,
                BeforeData = SerializeToJson(beforeLedger),
                AfterData = SerializeToJson(afterLedger)
            };

            await _operationLogRepository.InsertAsync(log);
        }

        /// <summary>
        /// 履歴削除のログを記録
        /// </summary>
        public async Task LogLedgerDeleteAsync(string operatorIdm, Ledger ledger)
        {
            var operatorName = await GetOperatorNameAsync(operatorIdm);

            var log = new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = operatorIdm,
                OperatorName = operatorName,
                TargetTable = Tables.Ledger,
                TargetId = ledger.Id.ToString(),
                Action = Actions.Delete,
                BeforeData = SerializeToJson(ledger),
                AfterData = null
            };

            await _operationLogRepository.InsertAsync(log);
        }

        /// <summary>
        /// 操作者の氏名を取得
        /// </summary>
        private async Task<string> GetOperatorNameAsync(string operatorIdm)
        {
            var staff = await _staffRepository.GetByIdmAsync(operatorIdm, includeDeleted: true);
            return staff?.Name ?? "不明";
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
