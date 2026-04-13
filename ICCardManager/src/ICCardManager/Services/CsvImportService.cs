using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using System.Data.SQLite;

namespace ICCardManager.Services
{
/// <summary>
    /// CSVインポート結果
    /// </summary>
    public class CsvImportResult
    {
        /// <summary>成功したか</summary>
        public bool Success { get; set; }

        /// <summary>インポートした件数</summary>
        public int ImportedCount { get; set; }

        /// <summary>スキップした件数（既存データ）</summary>
        public int SkippedCount { get; set; }

        /// <summary>エラー件数</summary>
        public int ErrorCount { get; set; }

        /// <summary>エラー詳細リスト</summary>
        public List<CsvImportError> Errors { get; set; } = new();

        /// <summary>エラーメッセージ</summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// CSVインポートエラー詳細
    /// </summary>
    public class CsvImportError
    {
        /// <summary>行番号</summary>
        public int LineNumber { get; set; }

        /// <summary>エラー内容</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>対象データ</summary>
        public string Data { get; set; }
    }

    /// <summary>
    /// CSVインポートプレビュー結果
    /// </summary>
    public class CsvImportPreviewResult
    {
        /// <summary>プレビューが有効か（エラーがないか）</summary>
        public bool IsValid { get; set; }

        /// <summary>新規追加予定件数</summary>
        public int NewCount { get; set; }

        /// <summary>更新予定件数</summary>
        public int UpdateCount { get; set; }

        /// <summary>スキップ予定件数</summary>
        public int SkipCount { get; set; }

        /// <summary>エラー件数</summary>
        public int ErrorCount { get; set; }

        /// <summary>エラー詳細リスト</summary>
        public List<CsvImportError> Errors { get; set; } = new();

        /// <summary>プレビューアイテムリスト</summary>
        public List<CsvImportPreviewItem> Items { get; set; } = new();

        /// <summary>エラーメッセージ</summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// CSVインポートプレビューアイテム
    /// </summary>
    public class CsvImportPreviewItem
    {
        /// <summary>行番号</summary>
        public int LineNumber { get; set; }

        /// <summary>IDm</summary>
        public string Idm { get; set; } = string.Empty;

        /// <summary>名前（カード種別または氏名）</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>追加情報（管理番号または職員番号）</summary>
        public string AdditionalInfo { get; set; }

        /// <summary>アクション（新規/更新/スキップ）</summary>
        public ImportAction Action { get; set; }

        /// <summary>変更点リスト（更新時および新規追加時）</summary>
        public List<FieldChange> Changes { get; set; } = new();

        /// <summary>変更点があるか</summary>
        public bool HasChanges => Changes.Count > 0;

        /// <summary>変更点のサマリ文字列</summary>
        public string ChangesSummary => HasChanges
            ? string.Join("、", Changes.Select(c => c.FieldName))
            : string.Empty;

        /// <summary>詳細セクションのヘッダー（アクションに応じて変化）</summary>
        public string ChangesHeader => Action == ImportAction.Insert
            ? "追加する内容:"
            : Action == ImportAction.Skip
                ? "スキップするデータ:"
                : "変更内容の詳細:";
    }

    /// <summary>
    /// フィールド変更情報
    /// </summary>
    public class FieldChange
    {
        /// <summary>フィールド名</summary>
        public string FieldName { get; set; } = string.Empty;

        /// <summary>変更前の値</summary>
        public string OldValue { get; set; } = string.Empty;

        /// <summary>変更後の値</summary>
        public string NewValue { get; set; } = string.Empty;

        /// <summary>表示専用フラグ（追加・スキップ時のデータ表示用）</summary>
        public bool IsDisplayOnly { get; set; }

        /// <summary>変更内容の表示文字列</summary>
        public string DisplayText => IsDisplayOnly
            ? $"{FieldName}: {NewValue ?? "(なし)"}"
            : $"{FieldName}: {OldValue ?? "(なし)"} → {NewValue ?? "(なし)"}";
    }

    /// <summary>
    /// インポートアクション
    /// </summary>
    public enum ImportAction
    {
        /// <summary>新規追加</summary>
        Insert,

        /// <summary>更新</summary>
        Update,

        /// <summary>スキップ</summary>
        Skip,

        /// <summary>削除済みを復元して更新</summary>
        Restore
    }

    /// <summary>
    /// CSVインポートサービス
    /// </summary>
    public partial class CsvImportService
    {
        private readonly ICardRepository _cardRepository;
        private readonly IStaffRepository _staffRepository;
        private readonly ILedgerRepository _ledgerRepository;
        private readonly IValidationService _validationService;
        private readonly DbContext _dbContext;
        private readonly ICacheService _cacheService;

        public CsvImportService(
            ICardRepository cardRepository,
            IStaffRepository staffRepository,
            ILedgerRepository ledgerRepository,
            IValidationService validationService,
            DbContext dbContext,
            ICacheService cacheService)
        {
            _cardRepository = cardRepository;
            _staffRepository = staffRepository;
            _ledgerRepository = ledgerRepository;
            _validationService = validationService;
            _dbContext = dbContext;
            _cacheService = cacheService;
        }

        // === 共通ユーティリティ ===

        /// <summary>
        /// CSVファイルを読み込み、行のリストとして返す
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        internal static async Task<List<string>> ReadCsvFileAsync(string filePath)
        {
            // UTF-8 with BOMに対応
            // FileShare.ReadWrite で開くことで、他プロセス（Excel等）がファイルを使用中でも読み込み可能にする
            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (line != null)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        #region 共通処理基盤

        /// <summary>
        /// CSVインポート処理を標準的な例外ハンドリングで実行
        /// </summary>
        /// <param name="operation">実行する処理</param>
        /// <param name="errors">エラーリスト（処理中にエラーが追加される場合に使用）</param>
        /// <returns>インポート結果</returns>
        private async Task<CsvImportResult> ExecuteImportWithErrorHandlingAsync(
            Func<Task<CsvImportResult>> operation,
            List<CsvImportError> errors = null)
        {
            errors ??= new List<CsvImportError>();

            try
            {
                return await operation();
            }
            catch (FileNotFoundException)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "指定されたファイルが見つかりません。",
                    Errors = errors
                };
            }
            catch (UnauthorizedAccessException)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "ファイルへのアクセス権限がありません。",
                    Errors = errors
                };
            }
            catch (IOException ex)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = $"ファイルの読み込みエラー: {ex.Message}",
                    Errors = errors
                };
            }
            catch (DatabaseException ex)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = ex.UserFriendlyMessage,
                    Errors = errors
                };
            }
            catch (Exception ex)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = $"予期しないエラーが発生しました: {ex.Message}",
                    Errors = errors
                };
            }
        }

        /// <summary>
        /// CSVプレビュー処理を標準的な例外ハンドリングで実行
        /// </summary>
        /// <param name="operation">実行する処理</param>
        /// <param name="errors">エラーリスト（処理中にエラーが追加される場合に使用）</param>
        /// <returns>プレビュー結果</returns>
        private async Task<CsvImportPreviewResult> ExecutePreviewWithErrorHandlingAsync(
            Func<Task<CsvImportPreviewResult>> operation,
            List<CsvImportError> errors = null)
        {
            errors ??= new List<CsvImportError>();

            try
            {
                return await operation();
            }
            catch (FileNotFoundException)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = "指定されたファイルが見つかりません。",
                    Errors = errors
                };
            }
            catch (UnauthorizedAccessException)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = "ファイルへのアクセス権限がありません。",
                    Errors = errors
                };
            }
            catch (IOException ex)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = $"ファイルの読み込みエラー: {ex.Message}",
                    Errors = errors
                };
            }
            catch (DatabaseException ex)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = ex.UserFriendlyMessage,
                    Errors = errors
                };
            }
            catch (Exception ex)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = $"予期しないエラーが発生しました: {ex.Message}",
                    Errors = errors
                };
            }
        }

        /// <summary>
        /// IDmのバリデーションを実行し、エラーがあればリストに追加
        /// </summary>
        /// <param name="idm">検証するIDm</param>
        /// <param name="lineNumber">行番号</param>
        /// <param name="fieldName">フィールド名（エラーメッセージ用）</param>
        /// <param name="line">元の行データ</param>
        /// <param name="errors">エラーリスト</param>
        /// <param name="isStaff">職員IDmかどうか</param>
        /// <returns>バリデーション成功の場合true</returns>
        private bool ValidateIdm(
            string idm,
            int lineNumber,
            string fieldName,
            string line,
            List<CsvImportError> errors,
            bool isStaff = false)
        {
            if (string.IsNullOrWhiteSpace(idm))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = $"{fieldName}は必須です",
                    Data = line
                });
                return false;
            }

            var validation = isStaff
                ? _validationService.ValidateStaffIdm(idm)
                : _validationService.ValidateCardIdm(idm);

            if (!validation.IsValid)
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = validation.ErrorMessage ?? $"{fieldName}の形式が不正です",
                    Data = idm
                });
                return false;
            }

            return true;
        }

        /// <summary>
        /// 必須フィールドのバリデーションを実行し、エラーがあればリストに追加
        /// </summary>
        /// <param name="value">検証する値</param>
        /// <param name="lineNumber">行番号</param>
        /// <param name="fieldName">フィールド名</param>
        /// <param name="line">元の行データ</param>
        /// <param name="errors">エラーリスト</param>
        /// <returns>バリデーション成功の場合true</returns>
        private static bool ValidateRequired(
            string value,
            int lineNumber,
            string fieldName,
            string line,
            List<CsvImportError> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = $"{fieldName}は必須です",
                    Data = line
                });
                return false;
            }
            return true;
        }

        /// <summary>
        /// CSV行の列数をバリデーション
        /// </summary>
        /// <param name="fields">パースされたフィールド</param>
        /// <param name="minColumns">最低列数</param>
        /// <param name="lineNumber">行番号</param>
        /// <param name="line">元の行データ</param>
        /// <param name="errors">エラーリスト</param>
        /// <returns>バリデーション成功の場合true</returns>
        private static bool ValidateColumnCount(
            List<string> fields,
            int minColumns,
            int lineNumber,
            string line,
            List<CsvImportError> errors)
        {
            if (fields.Count < minColumns)
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = lineNumber,
                    Message = "列数が不足しています",
                    Data = line
                });
                return false;
            }
            return true;
        }

        /// <summary>
        /// CSV行をパースし、フィールドのリストとして返す（ダブルクォート対応）
        /// </summary>
        /// <param name="line">CSV行文字列</param>
        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var currentField = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // エスケープされたダブルクォート
                        currentField.Append('"');
                        i++; // 次の文字をスキップ
                    }
                    else
                    {
                        // クォートの開始/終了
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    // フィールドの区切り
                    fields.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            // 最後のフィールドを追加
            fields.Add(currentField.ToString());

            return fields;
        }

        #endregion
    }
}
