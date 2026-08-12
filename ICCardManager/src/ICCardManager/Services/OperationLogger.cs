using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Threading.Tasks;
using System.Text.Json;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 操作ログの操作種別・対象テーブルの表示名マッピング（Issue #1787）
    /// </summary>
    /// <remarks>
    /// 表示名の Single Source of Truth。画面（OperationLogSearchViewModel の一覧表示・絞り込みコンボ・
    /// 詳細サマリー）と Excel（OperationLogExcelExportService）はすべてここへ委譲する。
    /// <see cref="OperationLogger.Actions"/> / <see cref="OperationLogger.Tables"/> へ定数を追加したら
    /// 本クラスのエントリにも追加すること（漏れは OperationLogDisplayNamesTests の
    /// 定数リフレクション走査テストが検出する）。
    /// </remarks>
    public static class OperationLogDisplayNames
    {
        /// <summary>操作種別の表示名（コンボの表示順を兼ねる順序付きリスト）</summary>
        public static IReadOnlyList<KeyValuePair<string, string>> ActionEntries { get; } =
            new[]
            {
                new KeyValuePair<string, string>(OperationLogger.Actions.Insert, "登録"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Update, "更新"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Delete, "削除"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Restore, "復元"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Merge, "統合"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Split, "分割"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Import, "インポート"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Export, "エクスポート"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Backup, "バックアップ"),
            };

        /// <summary>対象テーブルの表示名（コンボの表示順を兼ねる順序付きリスト）</summary>
        /// <remarks>
        /// ledger_detail の「利用明細」は <c>DatabaseException.GetEntityDisplayName</c> と同じ訳語にしている。
        /// ただし ledger は本表が「利用履歴」、同メソッドが「出納記録」で**意図的に異なる**:
        /// 操作ログ画面は職員が日々の貸出・返却を追う文脈、DB エラーダイアログは帳票の元帳を指す文脈で、
        /// 画面ごとに定着した呼称を優先している。両者を統合するなら用語集（00_用語集）の改訂を伴うため、
        /// ここで片方へ寄せない（Issue #1787 のコードレビューで検出）。
        /// </remarks>
        public static IReadOnlyList<KeyValuePair<string, string>> TableEntries { get; } =
            new[]
            {
                new KeyValuePair<string, string>(OperationLogger.Tables.Staff, "職員"),
                new KeyValuePair<string, string>(OperationLogger.Tables.IcCard, "交通系ICカード"),
                new KeyValuePair<string, string>(OperationLogger.Tables.Ledger, "利用履歴"),
                new KeyValuePair<string, string>(OperationLogger.Tables.LedgerDetail, "利用明細"),
                new KeyValuePair<string, string>(OperationLogger.Tables.Database, "データベース"),
                new KeyValuePair<string, string>(OperationLogger.Tables.OperationLog, "操作ログ"),
            };

        private static readonly Dictionary<string, string> ActionMap = BuildMap(ActionEntries);
        private static readonly Dictionary<string, string> TableMap = BuildMap(TableEntries);

        /// <summary>操作種別の表示名を取得（未知の値は生値、null は空文字）</summary>
        public static string GetActionDisplayName(string? action) =>
            action != null && ActionMap.TryGetValue(action, out var name) ? name : action ?? "";

        /// <summary>対象テーブルの表示名を取得（未知の値は生値、null は空文字）</summary>
        public static string GetTableDisplayName(string? targetTable) =>
            targetTable != null && TableMap.TryGetValue(targetTable, out var name) ? name : targetTable ?? "";

        private static Dictionary<string, string> BuildMap(IReadOnlyList<KeyValuePair<string, string>> entries)
        {
            var map = new Dictionary<string, string>(entries.Count);
            foreach (var entry in entries)
            {
                map.Add(entry.Key, entry.Value);
            }
            return map;
        }
    }

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
            // Issue #1302: 一括操作 (監査証跡)
            public const string Import = "IMPORT";
            public const string Export = "EXPORT";
            public const string Backup = "BACKUP";
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
            // Issue #1302: 一括操作対象
            public const string LedgerDetail = "ledger_detail";
            public const string Database = "database";
            // Issue #1787: 操作ログ自身の Excel 書き出し（個人情報の持ち出し）を EXPORT として記録する対象
            public const string OperationLog = "operation_log";
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

        /// <summary>
        /// CSV インポート操作のログを記録。Issue #1302。
        /// </summary>
        /// <param name="tableName">対象テーブル名 (<see cref="Tables"/> 定数)</param>
        /// <param name="filePath">インポート元ファイルのフルパス</param>
        /// <param name="insertedCount">新規挿入件数</param>
        /// <param name="skippedCount">スキップ件数</param>
        /// <param name="errorCount">エラー件数</param>
        public async Task LogImportAsync(
            string tableName,
            string filePath,
            int insertedCount,
            int skippedCount,
            int errorCount)
        {
            var (idm, name) = ResolveOperator();
            var fileName = System.IO.Path.GetFileName(filePath);
            var payload = new
            {
                FilePath = filePath,
                FileName = fileName,
                InsertedCount = insertedCount,
                SkippedCount = skippedCount,
                ErrorCount = errorCount
            };
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = tableName,
                TargetId = fileName,
                Action = Actions.Import,
                BeforeData = null,
                AfterData = SerializeToJson(payload)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// CSV/Excel エクスポート操作のログを記録。Issue #1302。
        /// </summary>
        /// <param name="tableName">対象テーブル名 (<see cref="Tables"/> 定数)</param>
        /// <param name="filePath">エクスポート先ファイルのフルパス</param>
        /// <param name="recordCount">出力件数</param>
        public async Task LogExportAsync(string tableName, string filePath, int recordCount)
        {
            var (idm, name) = ResolveOperator();
            var fileName = System.IO.Path.GetFileName(filePath);
            var payload = new
            {
                FilePath = filePath,
                FileName = fileName,
                RecordCount = recordCount
            };
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = tableName,
                TargetId = fileName,
                Action = Actions.Export,
                BeforeData = null,
                AfterData = SerializeToJson(payload)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// バックアップ取得のログを記録。Issue #1302。
        /// </summary>
        /// <param name="filePath">バックアップファイルのフルパス</param>
        public async Task LogBackupAsync(string filePath)
        {
            var (idm, name) = ResolveOperator();
            var fileName = System.IO.Path.GetFileName(filePath);
            var payload = new
            {
                FilePath = filePath,
                FileName = fileName
            };
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Database,
                TargetId = fileName,
                Action = Actions.Backup,
                BeforeData = null,
                AfterData = SerializeToJson(payload)
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// DB リストア操作のログを記録。Issue #1302。
        /// </summary>
        /// <remarks>
        /// 既存の <see cref="LogStaffRestoreAsync(Staff)"/> はレコード単位復元だが、
        /// こちらは DB 丸ごとリストア。<c>TargetTable="database"</c> で区別する。
        /// </remarks>
        /// <param name="filePath">リストア元ファイルのフルパス</param>
        public async Task LogRestoreAsync(string filePath)
        {
            var (idm, name) = ResolveOperator();
            var fileName = System.IO.Path.GetFileName(filePath);
            var payload = new
            {
                FilePath = filePath,
                FileName = fileName
            };
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Database,
                TargetId = fileName,
                Action = Actions.Restore,
                BeforeData = null,
                AfterData = SerializeToJson(payload)
            }).ConfigureAwait(false);
        }

        #endregion

        #region tx 受入オーバーロード — Issue #1458

        /// <summary>
        /// 履歴挿入のログを既存トランザクションで記録する (Issue #1458)。
        /// Ledger 操作と同一 tx で監査ログを書き込むことで fsync 1 回分の RTT を削減する。
        /// </summary>
        public async Task LogLedgerInsertAsync(Ledger ledger, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴更新のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerUpdateAsync(Ledger beforeLedger, Ledger afterLedger, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴削除のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerDeleteAsync(Ledger ledger, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴統合のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerMergeAsync(IReadOnlyList<Ledger> sourceLedgers, Ledger mergedLedger, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 履歴分割のログを既存トランザクションで記録する (Issue #1458)。
        /// </summary>
        public async Task LogLedgerSplitAsync(Ledger originalLedger, IReadOnlyList<Ledger> splitLedgers, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
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
