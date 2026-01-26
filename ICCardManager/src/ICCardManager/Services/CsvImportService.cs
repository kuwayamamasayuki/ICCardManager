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

        /// <summary>変更点リスト（更新時のみ）</summary>
        public List<FieldChange> Changes { get; set; } = new();

        /// <summary>変更点があるか</summary>
        public bool HasChanges => Changes.Count > 0;

        /// <summary>変更点のサマリ文字列</summary>
        public string ChangesSummary => HasChanges
            ? string.Join("、", Changes.Select(c => c.FieldName))
            : string.Empty;
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

        /// <summary>変更内容の表示文字列</summary>
        public string DisplayText => $"{FieldName}: {OldValue ?? "(なし)"} → {NewValue ?? "(なし)"}";
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
    public class CsvImportService
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

        /// <summary>
        /// カードCSVをインポート
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
        public async Task<CsvImportResult> ImportCardsAsync(string filePath, bool skipExisting = true)
        {
            var errors = new List<CsvImportError>();
            return await ExecuteImportWithErrorHandlingAsync(
                () => ImportCardsInternalAsync(filePath, skipExisting, errors),
                errors);
        }

        /// <summary>
        /// カードCSVインポートの内部処理
        /// </summary>
        private async Task<CsvImportResult> ImportCardsInternalAsync(
            string filePath,
            bool skipExisting,
            List<CsvImportError> errors)
        {
            var importedCount = 0;
            var skippedCount = 0;

            var lines = await ReadCsvFileAsync(filePath);
            if (lines.Count < 2)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            // バリデーションパス: まず全データをバリデーション
            // IsRestore: 削除済みカードを復元して更新する場合true
            var validRecords = new List<(int LineNumber, IcCard Card, bool IsUpdate, bool IsRestore)>();

            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                // 最低4列（カードIDm, カード種別, 管理番号, 備考）が必要
                if (!ValidateColumnCount(fields, 4, lineNumber, line, errors))
                {
                    continue;
                }

                var cardIdm = fields[0].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                var cardType = fields[1].Trim();
                var cardNumber = fields[2].Trim();
                var note = fields.Count > 3 ? fields[3].Trim() : "";

                // バリデーション（共通メソッドを使用）
                if (!ValidateIdm(cardIdm, lineNumber, "カードIDm", line, errors))
                {
                    continue;
                }

                if (!ValidateRequired(cardType, lineNumber, "カード種別", line, errors))
                {
                    continue;
                }

                if (!ValidateRequired(cardNumber, lineNumber, "管理番号", line, errors))
                {
                    continue;
                }

                // 既存チェック（削除済みも含めて検索）
                var existingCard = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
                if (existingCard != null)
                {
                    // 削除済みカードの場合は復元対象として扱う
                    if (existingCard.IsDeleted)
                    {
                        // 削除済みカードは復元して更新する（skipExistingでもスキップしない）
                        existingCard.CardType = cardType;
                        existingCard.CardNumber = cardNumber;
                        existingCard.Note = string.IsNullOrWhiteSpace(note) ? null : note;
                        validRecords.Add((lineNumber, existingCard, true, true)); // isRestore = true
                    }
                    else if (skipExisting)
                    {
                        // 有効なカードが存在し、スキップ設定の場合
                        skippedCount++;
                        continue;
                    }
                    else
                    {
                        // 有効なカードを更新
                        existingCard.CardType = cardType;
                        existingCard.CardNumber = cardNumber;
                        existingCard.Note = string.IsNullOrWhiteSpace(note) ? null : note;
                        validRecords.Add((lineNumber, existingCard, true, false)); // isRestore = false
                    }
                }
                else
                {
                    // 新規登録用のカード
                    var card = new IcCard
                    {
                        CardIdm = cardIdm,
                        CardType = cardType,
                        CardNumber = cardNumber,
                        Note = string.IsNullOrWhiteSpace(note) ? null : note
                    };
                    validRecords.Add((lineNumber, card, false, false));
                }
            }

            // バリデーションエラーがあれば中断
            if (errors.Count > 0)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ImportedCount = 0,
                    SkippedCount = skippedCount,
                    ErrorCount = errors.Count,
                    Errors = errors
                };
            }

            // トランザクション内でインポート実行
            using var transaction = _dbContext.BeginTransaction();
            try
            {
                foreach (var (lineNumber, card, isUpdate, isRestore) in validRecords)
                {
                    bool success;
                    if (isRestore)
                    {
                        // 削除済みカードを復元してから更新（トランザクション内）
                        success = await _cardRepository.RestoreAsync(card.CardIdm, transaction);
                        if (success)
                        {
                            success = await _cardRepository.UpdateAsync(card, transaction);
                        }
                    }
                    else if (isUpdate)
                    {
                        success = await _cardRepository.UpdateAsync(card, transaction);
                    }
                    else
                    {
                        success = await _cardRepository.InsertAsync(card, transaction);
                    }

                    if (success)
                    {
                        importedCount++;
                    }
                    else
                    {
                        var message = isRestore ? "カードの復元・更新に失敗しました"
                            : isUpdate ? "カードの更新に失敗しました"
                            : "カードの登録に失敗しました";
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = message,
                            Data = card.CardIdm
                        });
                    }
                }

                // すべて成功したらコミット
                if (errors.Count == 0)
                {
                    transaction.Commit();
                    // コミット後にキャッシュを無効化
                    _cacheService.InvalidateByPrefix(CacheKeys.CardPrefixForInvalidation);
                }
                else
                {
                    transaction.Rollback();
                    importedCount = 0;
                }
            }
            catch (SQLiteException ex)
            {
                transaction.Rollback();
                throw DatabaseException.QueryFailed("CSV import transaction", ex);
            }
            catch (Exception)
            {
                transaction.Rollback();
                throw;
            }

            return new CsvImportResult
            {
                Success = errors.Count == 0,
                ImportedCount = importedCount,
                SkippedCount = skippedCount,
                ErrorCount = errors.Count,
                Errors = errors
            };
        }

        /// <summary>
        /// 職員CSVをインポート
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
        public async Task<CsvImportResult> ImportStaffAsync(string filePath, bool skipExisting = true)
        {
            var errors = new List<CsvImportError>();
            return await ExecuteImportWithErrorHandlingAsync(
                () => ImportStaffInternalAsync(filePath, skipExisting, errors),
                errors);
        }

        /// <summary>
        /// 職員CSVインポートの内部処理
        /// </summary>
        private async Task<CsvImportResult> ImportStaffInternalAsync(
            string filePath,
            bool skipExisting,
            List<CsvImportError> errors)
        {
            var importedCount = 0;
            var skippedCount = 0;

            var lines = await ReadCsvFileAsync(filePath);
            if (lines.Count < 2)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            // バリデーションパス: まず全データをバリデーション
            // IsRestore: 削除済み職員を復元して更新する場合true
            var validRecords = new List<(int LineNumber, Staff Staff, bool IsUpdate, bool IsRestore)>();

            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                // 最低4列（職員IDm, 氏名, 職員番号, 備考）が必要
                if (!ValidateColumnCount(fields, 4, lineNumber, line, errors))
                {
                    continue;
                }

                var staffIdm = fields[0].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                var name = fields[1].Trim();
                var number = fields.Count > 2 ? fields[2].Trim() : "";
                var note = fields.Count > 3 ? fields[3].Trim() : "";

                // バリデーション（共通メソッドを使用）
                if (!ValidateIdm(staffIdm, lineNumber, "職員IDm", line, errors, isStaff: true))
                {
                    continue;
                }

                if (!ValidateRequired(name, lineNumber, "氏名", line, errors))
                {
                    continue;
                }

                // 既存チェック（削除済みも含めて検索）
                var existingStaff = await _staffRepository.GetByIdmAsync(staffIdm, includeDeleted: true);
                if (existingStaff != null)
                {
                    // 削除済み職員の場合は復元対象として扱う
                    if (existingStaff.IsDeleted)
                    {
                        // 削除済み職員は復元して更新する（skipExistingでもスキップしない）
                        existingStaff.Name = name;
                        existingStaff.Number = string.IsNullOrWhiteSpace(number) ? null : number;
                        existingStaff.Note = string.IsNullOrWhiteSpace(note) ? null : note;
                        validRecords.Add((lineNumber, existingStaff, true, true)); // isRestore = true
                    }
                    else if (skipExisting)
                    {
                        // 有効な職員が存在し、スキップ設定の場合
                        skippedCount++;
                        continue;
                    }
                    else
                    {
                        // 有効な職員を更新
                        existingStaff.Name = name;
                        existingStaff.Number = string.IsNullOrWhiteSpace(number) ? null : number;
                        existingStaff.Note = string.IsNullOrWhiteSpace(note) ? null : note;
                        validRecords.Add((lineNumber, existingStaff, true, false)); // isRestore = false
                    }
                }
                else
                {
                    // 新規登録用の職員
                    var staff = new Staff
                    {
                        StaffIdm = staffIdm,
                        Name = name,
                        Number = string.IsNullOrWhiteSpace(number) ? null : number,
                        Note = string.IsNullOrWhiteSpace(note) ? null : note
                    };
                    validRecords.Add((lineNumber, staff, false, false));
                }
            }

            // バリデーションエラーがあれば中断
            if (errors.Count > 0)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ImportedCount = 0,
                    SkippedCount = skippedCount,
                    ErrorCount = errors.Count,
                    Errors = errors
                };
            }

            // トランザクション内でインポート実行
            using var transaction = _dbContext.BeginTransaction();
            try
            {
                foreach (var (lineNumber, staff, isUpdate, isRestore) in validRecords)
                {
                    bool success;
                    if (isRestore)
                    {
                        // 削除済み職員を復元してから更新（トランザクション内）
                        success = await _staffRepository.RestoreAsync(staff.StaffIdm, transaction);
                        if (success)
                        {
                            success = await _staffRepository.UpdateAsync(staff, transaction);
                        }
                    }
                    else if (isUpdate)
                    {
                        success = await _staffRepository.UpdateAsync(staff, transaction);
                    }
                    else
                    {
                        success = await _staffRepository.InsertAsync(staff, transaction);
                    }

                    if (success)
                    {
                        importedCount++;
                    }
                    else
                    {
                        var message = isRestore ? "職員の復元・更新に失敗しました"
                            : isUpdate ? "職員の更新に失敗しました"
                            : "職員の登録に失敗しました";
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = message,
                            Data = staff.StaffIdm
                        });
                    }
                }

                // すべて成功したらコミット
                if (errors.Count == 0)
                {
                    transaction.Commit();
                    // コミット後にキャッシュを無効化
                    _cacheService.InvalidateByPrefix(CacheKeys.StaffPrefixForInvalidation);
                }
                else
                {
                    transaction.Rollback();
                    importedCount = 0;
                }
            }
            catch (SQLiteException ex)
            {
                transaction.Rollback();
                throw DatabaseException.QueryFailed("CSV import transaction", ex);
            }
            catch (Exception)
            {
                transaction.Rollback();
                throw;
            }

            return new CsvImportResult
            {
                Success = errors.Count == 0,
                ImportedCount = importedCount,
                SkippedCount = skippedCount,
                ErrorCount = errors.Count,
                Errors = errors
            };
        }

        /// <summary>
        /// カードCSVのインポートプレビューを取得
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
        public async Task<CsvImportPreviewResult> PreviewCardsAsync(string filePath, bool skipExisting = true)
        {
            var errors = new List<CsvImportError>();
            return await ExecutePreviewWithErrorHandlingAsync(
                () => PreviewCardsInternalAsync(filePath, skipExisting, errors),
                errors);
        }

        /// <summary>
        /// カードCSVプレビューの内部処理
        /// </summary>
        private async Task<CsvImportPreviewResult> PreviewCardsInternalAsync(
            string filePath,
            bool skipExisting,
            List<CsvImportError> errors)
        {
            var items = new List<CsvImportPreviewItem>();
            var newCount = 0;
            var updateCount = 0;
            var skipCount = 0;

            var lines = await ReadCsvFileAsync(filePath);
            if (lines.Count < 2)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                if (!ValidateColumnCount(fields, 4, lineNumber, line, errors))
                {
                    continue;
                }

                var cardIdm = fields[0].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                var cardType = fields[1].Trim();
                var cardNumber = fields[2].Trim();

                // バリデーション（共通メソッドを使用）
                if (!ValidateIdm(cardIdm, lineNumber, "カードIDm", line, errors))
                {
                    continue;
                }

                if (!ValidateRequired(cardType, lineNumber, "カード種別", line, errors))
                {
                    continue;
                }

                if (!ValidateRequired(cardNumber, lineNumber, "管理番号", line, errors))
                {
                    continue;
                }

                // 既存チェック（削除済みも含めて検索）
                var existingCard = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
                ImportAction action;
                var changes = new List<FieldChange>();

                if (existingCard != null)
                {
                    // 削除済みカードの場合は復元対象として扱う
                    if (existingCard.IsDeleted)
                    {
                        action = ImportAction.Restore;
                        updateCount++; // 復元+更新なので更新件数に含める
                        // 復元時も変更点を検出
                        DetectCardChanges(existingCard, cardType, cardNumber, changes);
                        changes.Insert(0, new FieldChange
                        {
                            FieldName = "状態",
                            OldValue = "削除済み",
                            NewValue = "有効"
                        });
                    }
                    else if (skipExisting)
                    {
                        action = ImportAction.Skip;
                        skipCount++;
                    }
                    else
                    {
                        action = ImportAction.Update;
                        // 変更点を検出
                        DetectCardChanges(existingCard, cardType, cardNumber, changes);
                        if (changes.Count > 0)
                        {
                            updateCount++;
                        }
                        else
                        {
                            // 変更点がない場合はスキップ扱い
                            action = ImportAction.Skip;
                            skipCount++;
                        }
                    }
                }
                else
                {
                    action = ImportAction.Insert;
                    newCount++;
                }

                items.Add(new CsvImportPreviewItem
                {
                    LineNumber = lineNumber,
                    Idm = cardIdm,
                    Name = cardType,
                    AdditionalInfo = cardNumber,
                    Action = action,
                    Changes = changes
                });
            }

            return new CsvImportPreviewResult
            {
                IsValid = errors.Count == 0,
                NewCount = newCount,
                UpdateCount = updateCount,
                SkipCount = skipCount,
                ErrorCount = errors.Count,
                Errors = errors,
                Items = items
            };
        }

        /// <summary>
        /// 職員CSVのインポートプレビューを取得
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
        public async Task<CsvImportPreviewResult> PreviewStaffAsync(string filePath, bool skipExisting = true)
        {
            var errors = new List<CsvImportError>();
            return await ExecutePreviewWithErrorHandlingAsync(
                () => PreviewStaffInternalAsync(filePath, skipExisting, errors),
                errors);
        }

        /// <summary>
        /// 職員CSVプレビューの内部処理
        /// </summary>
        private async Task<CsvImportPreviewResult> PreviewStaffInternalAsync(
            string filePath,
            bool skipExisting,
            List<CsvImportError> errors)
        {
            var items = new List<CsvImportPreviewItem>();
            var newCount = 0;
            var updateCount = 0;
            var skipCount = 0;

            var lines = await ReadCsvFileAsync(filePath);
            if (lines.Count < 2)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                if (!ValidateColumnCount(fields, 4, lineNumber, line, errors))
                {
                    continue;
                }

                var staffIdm = fields[0].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                var name = fields[1].Trim();
                var number = fields.Count > 2 ? fields[2].Trim() : "";

                // バリデーション（共通メソッドを使用）
                if (!ValidateIdm(staffIdm, lineNumber, "職員IDm", line, errors, isStaff: true))
                {
                    continue;
                }

                if (!ValidateRequired(name, lineNumber, "氏名", line, errors))
                {
                    continue;
                }

                // 既存チェック（削除済みも含めて検索）
                var existingStaff = await _staffRepository.GetByIdmAsync(staffIdm, includeDeleted: true);
                ImportAction action;
                var changes = new List<FieldChange>();

                if (existingStaff != null)
                {
                    // 削除済み職員の場合は復元対象として扱う
                    if (existingStaff.IsDeleted)
                    {
                        action = ImportAction.Restore;
                        updateCount++; // 復元+更新なので更新件数に含める
                        // 復元時も変更点を検出
                        DetectStaffChanges(existingStaff, name, number, changes);
                        changes.Insert(0, new FieldChange
                        {
                            FieldName = "状態",
                            OldValue = "削除済み",
                            NewValue = "有効"
                        });
                    }
                    else if (skipExisting)
                    {
                        action = ImportAction.Skip;
                        skipCount++;
                    }
                    else
                    {
                        action = ImportAction.Update;
                        // 変更点を検出
                        DetectStaffChanges(existingStaff, name, number, changes);
                        if (changes.Count > 0)
                        {
                            updateCount++;
                        }
                        else
                        {
                            // 変更点がない場合はスキップ扱い
                            action = ImportAction.Skip;
                            skipCount++;
                        }
                    }
                }
                else
                {
                    action = ImportAction.Insert;
                    newCount++;
                }

                items.Add(new CsvImportPreviewItem
                {
                    LineNumber = lineNumber,
                    Idm = staffIdm,
                    Name = name,
                    AdditionalInfo = string.IsNullOrWhiteSpace(number) ? null : number,
                    Action = action,
                    Changes = changes
                });
            }

            return new CsvImportPreviewResult
            {
                IsValid = errors.Count == 0,
                NewCount = newCount,
                UpdateCount = updateCount,
                SkipCount = skipCount,
                ErrorCount = errors.Count,
                Errors = errors,
                Items = items
            };
        }

        /// <summary>
        /// 履歴CSVをインポート
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        /// <remarks>
        /// CSVフォーマット: 日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
        /// 注意: LedgerDetailはインポートされません（エクスポート時に含まれないため）
        /// 注意: 管理番号は参照用で、実際のデータ識別はカードIDmで行います
        /// </remarks>
        public async Task<CsvImportResult> ImportLedgersAsync(string filePath)
        {
            var errors = new List<CsvImportError>();
            var importedCount = 0;
            var skippedCount = 0;

            try
            {
                var lines = await ReadCsvFileAsync(filePath);
                if (lines.Count < 2)
                {
                    return new CsvImportResult
                    {
                        Success = false,
                        ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                    };
                }

                // バリデーションパス: まず全データをバリデーション
                var validRecords = new List<(int LineNumber, Ledger Ledger)>();
                var existingCardIdms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 全カードのIDmをキャッシュ（パフォーマンス向上）
                var allCards = await _cardRepository.GetAllIncludingDeletedAsync();
                foreach (var card in allCards)
                {
                    existingCardIdms.Add(card.CardIdm);
                }

                for (var i = 1; i < lines.Count; i++)
                {
                    var lineNumber = i + 1;
                    var line = lines[i];

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var fields = ParseCsvLine(line);

                    // 最低9列（日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考）が必要
                    if (fields.Count < 9)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "列数が不足しています（9列必要）",
                            Data = line
                        });
                        continue;
                    }

                    var dateStr = fields[0].Trim();
                    var cardIdm = fields[1].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                    // fields[2] は管理番号（参照用、インポート時は使用しない）
                    var summary = fields[3].Trim();
                    var incomeStr = fields[4].Trim();
                    var expenseStr = fields[5].Trim();
                    var balanceStr = fields[6].Trim();
                    var staffName = fields[7].Trim();
                    var note = fields[8].Trim();

                    // バリデーション: 日時
                    if (!DateTime.TryParse(dateStr, out var date))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "日時の形式が不正です",
                            Data = dateStr
                        });
                        continue;
                    }

                    // バリデーション: カードIDm
                    if (string.IsNullOrWhiteSpace(cardIdm))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "カードIDmは必須です",
                            Data = line
                        });
                        continue;
                    }

                    // カードの存在チェック
                    if (!existingCardIdms.Contains(cardIdm))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "該当するカードが登録されていません",
                            Data = cardIdm
                        });
                        continue;
                    }

                    // バリデーション: 摘要
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "摘要は必須です",
                            Data = line
                        });
                        continue;
                    }

                    // バリデーション: 残額
                    if (!int.TryParse(balanceStr, out var balance))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "残額の形式が不正です",
                            Data = balanceStr
                        });
                        continue;
                    }

                    // 受入金額（空なら0）
                    var income = 0;
                    if (!string.IsNullOrWhiteSpace(incomeStr) && !int.TryParse(incomeStr, out income))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "受入金額の形式が不正です",
                            Data = incomeStr
                        });
                        continue;
                    }

                    // 払出金額（空なら0）
                    var expense = 0;
                    if (!string.IsNullOrWhiteSpace(expenseStr) && !int.TryParse(expenseStr, out expense))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "払出金額の形式が不正です",
                            Data = expenseStr
                        });
                        continue;
                    }

                    var ledger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = date,
                        Summary = summary,
                        Income = income,
                        Expense = expense,
                        Balance = balance,
                        StaffName = string.IsNullOrWhiteSpace(staffName) ? null : staffName,
                        Note = string.IsNullOrWhiteSpace(note) ? null : note
                    };

                    validRecords.Add((lineNumber, ledger));
                }

                // バリデーションエラーがあれば中断
                if (errors.Count > 0)
                {
                    return new CsvImportResult
                    {
                        Success = false,
                        ImportedCount = 0,
                        SkippedCount = skippedCount,
                        ErrorCount = errors.Count,
                        Errors = errors
                    };
                }

                // レコードが0件の場合
                if (validRecords.Count == 0)
                {
                    return new CsvImportResult
                    {
                        Success = true,
                        ImportedCount = 0,
                        SkippedCount = skippedCount,
                        ErrorCount = 0
                    };
                }

                // Issue #334: 既存履歴の重複チェック用キーを取得
                var uniqueCardIdms = validRecords.Select(r => r.Ledger.CardIdm).Distinct();
                var existingLedgerKeys = await _ledgerRepository.GetExistingLedgerKeysAsync(uniqueCardIdms);

                // インポート実行（履歴はトランザクションなしで直接インポート）
                // 注: LedgerRepository.InsertAsyncはトランザクション対応していないため
                foreach (var (lineNumber, ledger) in validRecords)
                {
                    // 重複チェック: 同じ履歴が既に存在する場合はスキップ
                    var ledgerKey = (ledger.CardIdm, ledger.Date, ledger.Summary, ledger.Income, ledger.Expense, ledger.Balance);
                    if (existingLedgerKeys.Contains(ledgerKey))
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        var id = await _ledgerRepository.InsertAsync(ledger);
                        if (id > 0)
                        {
                            importedCount++;
                        }
                        else
                        {
                            errors.Add(new CsvImportError
                            {
                                LineNumber = lineNumber,
                                Message = "履歴の登録に失敗しました",
                                Data = ledger.CardIdm
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"履歴の登録中にエラーが発生しました: {ex.Message}",
                            Data = ledger.CardIdm
                        });
                    }
                }

                return new CsvImportResult
                {
                    Success = errors.Count == 0,
                    ImportedCount = importedCount,
                    SkippedCount = skippedCount,
                    ErrorCount = errors.Count,
                    Errors = errors
                };
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
        /// 履歴CSVのインポートプレビューを取得
        /// </summary>
        /// <param name="filePath">CSVファイルパス</param>
        public async Task<CsvImportPreviewResult> PreviewLedgersAsync(string filePath)
        {
            var errors = new List<CsvImportError>();
            var items = new List<CsvImportPreviewItem>();
            var newCount = 0;
            var skipCount = 0;

            try
            {
                var lines = await ReadCsvFileAsync(filePath);
                if (lines.Count < 2)
                {
                    return new CsvImportPreviewResult
                    {
                        IsValid = false,
                        ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                    };
                }

                // 全カードのIDmをキャッシュ
                var existingCardIdms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allCards = await _cardRepository.GetAllIncludingDeletedAsync();
                foreach (var card in allCards)
                {
                    existingCardIdms.Add(card.CardIdm);
                }

                // 仮バリデーションでカードIDmを収集（重複チェック用）
                var cardIdmsInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var validatedRecords = new List<(int LineNumber, string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>();

                for (var i = 1; i < lines.Count; i++)
                {
                    var lineNumber = i + 1;
                    var line = lines[i];

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var fields = ParseCsvLine(line);

                    if (fields.Count < 9)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "列数が不足しています（9列必要）",
                            Data = line
                        });
                        continue;
                    }

                    var dateStr = fields[0].Trim();
                    var cardIdm = fields[1].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                    // fields[2] は管理番号（参照用）
                    var summary = fields[3].Trim();
                    var incomeStr = fields[4].Trim();
                    var expenseStr = fields[5].Trim();
                    var balanceStr = fields[6].Trim();

                    // バリデーション: 日時
                    if (!DateTime.TryParse(dateStr, out var date))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "日時の形式が不正です",
                            Data = dateStr
                        });
                        continue;
                    }

                    // バリデーション: カードIDm
                    if (string.IsNullOrWhiteSpace(cardIdm))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "カードIDmは必須です",
                            Data = line
                        });
                        continue;
                    }

                    // カードの存在チェック
                    if (!existingCardIdms.Contains(cardIdm))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "該当するカードが登録されていません",
                            Data = cardIdm
                        });
                        continue;
                    }

                    // バリデーション: 摘要
                    if (string.IsNullOrWhiteSpace(summary))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "摘要は必須です",
                            Data = line
                        });
                        continue;
                    }

                    // バリデーション: 残額
                    if (!int.TryParse(balanceStr, out var balance))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "残額の形式が不正です",
                            Data = balanceStr
                        });
                        continue;
                    }

                    // 受入金額（空なら0）
                    var income = 0;
                    if (!string.IsNullOrWhiteSpace(incomeStr) && !int.TryParse(incomeStr, out income))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "受入金額の形式が不正です",
                            Data = incomeStr
                        });
                        continue;
                    }

                    // 払出金額（空なら0）
                    var expense = 0;
                    if (!string.IsNullOrWhiteSpace(expenseStr) && !int.TryParse(expenseStr, out expense))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "払出金額の形式が不正です",
                            Data = expenseStr
                        });
                        continue;
                    }

                    cardIdmsInFile.Add(cardIdm);
                    validatedRecords.Add((lineNumber, cardIdm, date, summary, income, expense, balance));
                }

                // Issue #334: 既存履歴の重複チェック用キーを取得
                var existingLedgerKeys = await _ledgerRepository.GetExistingLedgerKeysAsync(cardIdmsInFile);

                // プレビューアイテムを生成
                foreach (var (lineNumber, cardIdm, date, summary, income, expense, balance) in validatedRecords)
                {
                    var ledgerKey = (cardIdm, date, summary, income, expense, balance);
                    ImportAction action;

                    if (existingLedgerKeys.Contains(ledgerKey))
                    {
                        action = ImportAction.Skip;
                        skipCount++;
                    }
                    else
                    {
                        action = ImportAction.Insert;
                        newCount++;
                    }

                    items.Add(new CsvImportPreviewItem
                    {
                        LineNumber = lineNumber,
                        Idm = cardIdm,
                        Name = summary,
                        AdditionalInfo = date.ToString("yyyy-MM-dd HH:mm:ss"),
                        Action = action
                    });
                }

                return new CsvImportPreviewResult
                {
                    IsValid = errors.Count == 0,
                    NewCount = newCount,
                    UpdateCount = 0,
                    SkipCount = skipCount,
                    ErrorCount = errors.Count,
                    Errors = errors,
                    Items = items
                };
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
        /// CSVファイルを読み込み（UTF-8 BOM対応）
        /// </summary>
        private static async Task<List<string>> ReadCsvFileAsync(string filePath)
        {
            // UTF-8 with BOMに対応
            using var reader = new StreamReader(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
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
        /// カードデータの変更点を検出
        /// </summary>
        /// <param name="existingCard">既存のカード</param>
        /// <param name="newCardType">新しいカード種別</param>
        /// <param name="newCardNumber">新しい管理番号</param>
        /// <param name="changes">変更点リスト（検出結果が追加される）</param>
        private static void DetectCardChanges(
            IcCard existingCard,
            string newCardType,
            string newCardNumber,
            List<FieldChange> changes)
        {
            if (existingCard.CardType != newCardType)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "カード種別",
                    OldValue = existingCard.CardType ?? "(なし)",
                    NewValue = newCardType
                });
            }

            if (existingCard.CardNumber != newCardNumber)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "管理番号",
                    OldValue = existingCard.CardNumber ?? "(なし)",
                    NewValue = newCardNumber
                });
            }
        }

        /// <summary>
        /// 職員データの変更点を検出
        /// </summary>
        /// <param name="existingStaff">既存の職員</param>
        /// <param name="newName">新しい氏名</param>
        /// <param name="newNumber">新しい職員番号</param>
        /// <param name="changes">変更点リスト（検出結果が追加される）</param>
        private static void DetectStaffChanges(
            Staff existingStaff,
            string newName,
            string newNumber,
            List<FieldChange> changes)
        {
            if (existingStaff.Name != newName)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "氏名",
                    OldValue = existingStaff.Name ?? "(なし)",
                    NewValue = newName
                });
            }

            if (existingStaff.Number != newNumber)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "職員番号",
                    OldValue = existingStaff.Number ?? "(なし)",
                    NewValue = newNumber
                });
            }
        }

        #endregion

        /// <summary>
        /// CSV行をパース（ダブルクォート対応）
        /// </summary>
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
    }
}
