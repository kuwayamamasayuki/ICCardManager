#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using ICCardManager.Common;
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
                new KeyValuePair<string, string>(OperationLogger.Actions.Unmerge, "統合取消"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Import, "インポート"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Export, "エクスポート"),
                new KeyValuePair<string, string>(OperationLogger.Actions.Backup, "バックアップ"),
            };

        /// <summary>対象テーブルの表示名（コンボの表示順を兼ねる順序付きリスト）</summary>
        /// <remarks>
        /// ledger は「利用履歴」、ledger_detail は「利用明細」と呼ぶ（操作ログ画面は職員が日々の貸出・返却を追う文脈）。
        /// かつて DB エラーダイアログ用の訳語（<c>DatabaseException.GetEntityDisplayName</c>。ledger は「出納記録」）が
        /// 別にあったが、それを使うファクトリが本番から送出されず Issue #2330 で削除した（Issue #1787 の経緯）。
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
                new KeyValuePair<string, string>(OperationLogger.Tables.Settings, "設定"),
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
    /// 操作者を引数で受け取るオーバーロードは持たない（引数経由のなりすましを構造的に防ぐ。Issue #2164 で旧シグネチャを削除）。
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
            // Issue #2265: 統合の取り消し（統合先を書き戻し、統合元を挿入し直す）。表示名は「統合取消」
            // （Excel の B 列・画面の「操作」列は全角 6 文字までを折り返さない幅にしている。#1787）
            public const string Unmerge = "UNMERGE";
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
            // Issue #2264: 設定画面（F5）のバックアップ先・データベース保存先の変更。対象 ID は設定項目名
            // （<see cref="SettingNames"/>）。データベース保存先はテーブルではなく PC ごとの設定ファイルだが、
            // 「設定」として同じ対象にまとめる（操作ログ画面の絞り込みで設定の変更を 1 つの対象として探せるように）
            public const string Settings = "settings";
        }

        /// <summary>
        /// 設定の変更を記録するときの設定項目名（操作ログの対象 ID。Issue #2264）
        /// </summary>
        /// <remarks>
        /// 対象 ID は操作ログ画面の「対象」列と詳細列（「設定（バックアップ先）を更新: …」）にそのまま出るため、
        /// 設定キー（<c>backup_path</c>）ではなく画面の項目名で記録する。
        /// </remarks>
        public static class SettingNames
        {
            /// <summary>データベースの自動バックアップ先（共有 DB の <c>settings.backup_path</c>。全 PC に効く）</summary>
            public const string BackupPath = "バックアップ先";

            /// <summary>データベースの保存先（その PC の <c>database_config.txt</c>）</summary>
            public const string DatabasePath = "データベース保存先";
        }

        public OperationLogger(
            IOperationLogRepository operationLogRepository,
            ICurrentOperatorContext operatorContext)
        {
            _operationLogRepository = operationLogRepository;
            _operatorContext = operatorContext;
        }

        #region 操作ログの記録（操作者は ICurrentOperatorContext から解決する） — Issue #1265 / #2164

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
                TargetId = afterLedger.Id.ToString(CultureInfo.InvariantCulture),
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
                TargetId = ledger.Id.ToString(CultureInfo.InvariantCulture),
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
                TargetId = ledger.Id.ToString(CultureInfo.InvariantCulture),
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
                TargetId = mergedLedger.Id.ToString(CultureInfo.InvariantCulture),
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
                TargetId = originalLedger.Id.ToString(CultureInfo.InvariantCulture),
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
        /// <param name="updatedCount">
        /// 既存データを書き換えた件数（更新・復元・利用履歴詳細の置き換え。Issue #2262）。
        /// 以前は利用履歴の取り込みが追加と更新を合計して <paramref name="insertedCount"/> に入れており、
        /// 既存の履歴を書き換えた取り込みかどうかを操作ログから区別できなかった
        /// </param>
        /// <param name="skippedCount">スキップ件数</param>
        /// <param name="errorCount">エラー件数</param>
        /// <remarks>
        /// 取り込み全体についての 1 行。書き換えた行ごとの変更前・変更後は、取り込みと同じトランザクションで
        /// <c>CsvImportService</c> が別に記録する（Issue #2262）。
        /// </remarks>
        public async Task LogImportAsync(
            string tableName,
            string filePath,
            int insertedCount,
            int updatedCount,
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
                UpdatedCount = updatedCount,
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
                TargetId = ledger.Id.ToString(CultureInfo.InvariantCulture),
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
                TargetId = afterLedger.Id.ToString(CultureInfo.InvariantCulture),
                Action = Actions.Update,
                BeforeData = SerializeToJson(beforeLedger),
                AfterData = SerializeToJson(afterLedger)
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// ICカード更新のログを既存トランザクションで記録する (Issue #2151)。
        /// </summary>
        /// <remarks>
        /// 払い戻し（払戻台帳の INSERT ＋ 払戻済への更新）と同じトランザクションで書く。
        /// 別々に確定させると、監査ログの書き込みだけが失敗したときに「誰が払い戻したか分からない
        /// 払い戻し」が 6 年保存の台帳に残る（Issue #1760 と同じ害）。
        /// </remarks>
        public async Task LogCardUpdateAsync(IcCard beforeCard, IcCard afterCard, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// ICカード登録のログを既存トランザクションで記録する (Issue #2156)。
        /// </summary>
        /// <remarks>
        /// カードの登録（<c>ic_card</c> への INSERT）と同じトランザクションで書く
        /// （<c>CardManagementService.RegisterAsync</c>）。別々に確定させると、監査ログの書き込みだけが
        /// 失敗したときに「誰が登録したか分からない登録」が残る。以下のカード・職員の tx 版も同じ理由。
        /// </remarks>
        public async Task LogCardInsertAsync(IcCard card, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// ICカード削除のログを既存トランザクションで記録する (Issue #2156)。
        /// </summary>
        public async Task LogCardDeleteAsync(IcCard card, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// ICカード復元のログを既存トランザクションで記録する (Issue #2156)。
        /// </summary>
        public async Task LogCardRestoreAsync(IcCard card, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員登録のログを既存トランザクションで記録する (Issue #2156)。
        /// </summary>
        public async Task LogStaffInsertAsync(Staff staff, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員更新のログを既存トランザクションで記録する (Issue #2156)。
        /// </summary>
        public async Task LogStaffUpdateAsync(Staff beforeStaff, Staff afterStaff, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員削除のログを既存トランザクションで記録する (Issue #2156)。
        /// </summary>
        public async Task LogStaffDeleteAsync(Staff staff, SQLiteTransaction transaction)
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
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 職員復元のログを既存トランザクションで記録する (Issue #2156)。
        /// </summary>
        public async Task LogStaffRestoreAsync(Staff staff, SQLiteTransaction transaction)
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
                TargetId = ledger.Id.ToString(CultureInfo.InvariantCulture),
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
                TargetId = mergedLedger.Id.ToString(CultureInfo.InvariantCulture),
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
                TargetId = originalLedger.Id.ToString(CultureInfo.InvariantCulture),
                Action = Actions.Split,
                BeforeData = SerializeToJson(originalLedger),
                AfterData = SerializeToJson(splitLedgers)
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 統合の取り消しのログを既存トランザクションで記録する（Issue #2265）。
        /// </summary>
        /// <param name="mergedBefore">取り消す直前の統合先（明細を含む。DB から読み直した、書き換えと別のインスタンス。#1959）</param>
        /// <param name="restoredTarget">統合前の状態へ戻した統合先</param>
        /// <param name="reinsertedSources">挿入し直した統合元（新しい ID）</param>
        /// <param name="transaction">台帳の書き戻し・取り消し済みのマークと同じトランザクション</param>
        /// <remarks>
        /// 統合（<see cref="LogLedgerMergeAsync(IReadOnlyList{Ledger}, Ledger, SQLiteTransaction)"/>）の逆向き。
        /// 変更前は統合先 1 件、変更後は「統合先 → 統合元」の順の配列で、分割（1 件 → 複数件）と同じ形になる。
        /// 対象 ID は統合先（統合のログと同じ行を指す）。
        /// </remarks>
        public async Task LogLedgerUnmergeAsync(
            Ledger mergedBefore, Ledger restoredTarget, IReadOnlyList<Ledger> reinsertedSources, SQLiteTransaction transaction)
        {
            var (idm, name) = ResolveOperator();
            var after = new List<Ledger> { restoredTarget };
            after.AddRange(reinsertedSources);
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Ledger,
                TargetId = restoredTarget.Id.ToString(CultureInfo.InvariantCulture),
                Action = Actions.Unmerge,
                BeforeData = SerializeToJson(mergedBefore),
                AfterData = SerializeToJson(after)
            }, transaction).ConfigureAwait(false);
        }

        /// <summary>
        /// 設定の変更のログを既存トランザクションで記録する（Issue #2264）。
        /// </summary>
        /// <param name="settingName">設定項目名（<see cref="SettingNames"/>）。対象 ID になる</param>
        /// <param name="beforeValue">変更前の値（未設定は空文字）</param>
        /// <param name="afterValue">変更後の値（既定に戻した場合は空文字）</param>
        /// <param name="transaction">設定の書き込みと同じトランザクション（記録に失敗したら変更ごと巻き戻す）</param>
        /// <remarks>
        /// 操作種別は <see cref="Actions.Update"/>。値は <c>Value</c> に入れ、操作ログ画面の詳細列は
        /// 「設定（バックアップ先）を更新: 値: A→B」になる。JSON は <see cref="SerializeToJson{T}"/> で作る（手組みしない。#1996）。
        /// </remarks>
        public async Task LogSettingChangeAsync(
            string settingName, string beforeValue, string afterValue, SQLiteTransaction transaction)
        {
            var (idm, name) = ResolveOperator();
            // 変更した PC 名は変更後にだけ載せる（変更前の値をどの PC が設定したかは、この記録からは分からないため）。
            // 引数で受け取らない（操作者の識別子に当たる値は引数で受け取らない。#2164）
            var machineName = Environment.MachineName;
            await _operationLogRepository.InsertAsync(new OperationLog
            {
                Timestamp = DateTime.Now,
                OperatorIdm = idm,
                OperatorName = name,
                TargetTable = Tables.Settings,
                TargetId = settingName,
                Action = Actions.Update,
                BeforeData = SerializeToJson(new { Setting = settingName, Value = beforeValue }),
                AfterData = SerializeToJson(new { Setting = settingName, Value = afterValue, MachineName = machineName })
            }, transaction).ConfigureAwait(false);
        }

        #endregion

        /// <summary>
        /// <see cref="ICurrentOperatorContext"/> から現在の操作者を解決する。
        /// セッション無効時は GUI 操作として扱う。
        /// </summary>
        private (string idm, string name) ResolveOperator()
        {
            // Issue #2260: 有効性の判定と IDm・氏名の取り出しを 1 回で行う（別々に読むと、その間に失効し得る）
            return _operatorContext.TryGetOperator(out var idm, out var name)
                ? (idm, name)
                : (GuiOperator.Idm, GuiOperator.Name);
        }

        /// <summary>
        /// オブジェクトをJSON文字列にシリアライズ
        /// </summary>
        private static string SerializeToJson<T>(T obj)
        {
            return JsonSerializer.Serialize(obj, OperationLogJson.Options);
        }
    }
}
