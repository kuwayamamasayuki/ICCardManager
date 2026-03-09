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
        public string ChangesHeader => Action == ImportAction.Insert ? "追加する内容:" : "変更内容の詳細:";
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
        public virtual async Task<CsvImportResult> ImportCardsAsync(string filePath, bool skipExisting = true)
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
        public virtual async Task<CsvImportResult> ImportStaffAsync(string filePath, bool skipExisting = true)
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
        /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
        /// <param name="targetCardIdm">CSV内のIDmが空の場合に使用するカードIDm（オプション）</param>
        /// <remarks>
        /// 新フォーマット: ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
        /// 旧フォーマット: 日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考
        /// 注意: LedgerDetailはインポートされません（エクスポート時に含まれないため）
        /// 注意: 管理番号は参照用で、実際のデータ識別はカードIDmで行います
        /// Issue #511: CSVのIDm列が空の場合、targetCardIdmが指定されていればそのIDmを使用
        /// </remarks>
        public virtual async Task<CsvImportResult> ImportLedgersAsync(string filePath, bool skipExisting = true, string? targetCardIdm = null)
        {
            var errors = new List<CsvImportError>();
            var importedCount = 0;
            var skippedCount = 0;
            var updatedCount = 0;

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

                // ヘッダー行を解析してID列の有無を判定
                var headerFields = ParseCsvLine(lines[0]);
                var hasIdColumn = headerFields.Count > 0 && headerFields[0].Trim().Equals("ID", StringComparison.OrdinalIgnoreCase);
                var minColumns = hasIdColumn ? 10 : 9;

                // バリデーションパス: まず全データをバリデーション
                // IsUpdate: 既存レコードを更新する場合true
                var validRecords = new List<(int LineNumber, Ledger Ledger, bool IsUpdate)>();
                // Issue #754: 残高整合性チェック用に全レコードを保持（スキップ分を含む）
                var allRecordsForValidation = new List<(int LineNumber, Ledger Ledger, bool IsUpdate)>();
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

                    if (fields.Count < minColumns)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"列数が不足しています（{minColumns}列必要）",
                            Data = line
                        });
                        continue;
                    }

                    // フィールドのインデックスを調整（ID列の有無による）
                    var offset = hasIdColumn ? 1 : 0;
                    var idStr = hasIdColumn ? fields[0].Trim() : "";
                    var dateStr = fields[0 + offset].Trim();
                    var cardIdm = fields[1 + offset].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                    // fields[2 + offset] は管理番号（参照用、インポート時は使用しない）
                    var summary = fields[3 + offset].Trim();
                    var incomeStr = fields[4 + offset].Trim();
                    var expenseStr = fields[5 + offset].Trim();
                    var balanceStr = fields[6 + offset].Trim();
                    var staffName = fields[7 + offset].Trim();
                    var note = fields[8 + offset].Trim();

                    // ID列がある場合、IDを解析
                    int? ledgerId = null;
                    if (hasIdColumn && !string.IsNullOrWhiteSpace(idStr))
                    {
                        if (!int.TryParse(idStr, out var parsedId))
                        {
                            errors.Add(new CsvImportError
                            {
                                LineNumber = lineNumber,
                                Message = "IDの形式が不正です",
                                Data = idStr
                            });
                            continue;
                        }
                        ledgerId = parsedId;
                    }

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
                    // Issue #511: IDmが空の場合、targetCardIdmを使用
                    if (string.IsNullOrWhiteSpace(cardIdm))
                    {
                        if (!string.IsNullOrWhiteSpace(targetCardIdm))
                        {
                            cardIdm = targetCardIdm.ToUpperInvariant();
                        }
                        else
                        {
                            errors.Add(new CsvImportError
                            {
                                LineNumber = lineNumber,
                                Message = "カードIDmは必須です（CSVで空欄の場合はインポート先カードを指定してください）",
                                Data = line
                            });
                            continue;
                        }
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

                    // 既存レコードの確認（IDがある場合）
                    var isUpdate = false;
                    Ledger existingLedgerForUpdate = null;
                    if (ledgerId.HasValue)
                    {
                        var existingLedger = await _ledgerRepository.GetByIdAsync(ledgerId.Value);
                        if (existingLedger != null)
                        {
                            // Issue #639: 金額・日付を含む全フィールドで変更点を検出
                            var hasChanges = existingLedger.Summary != summary ||
                                            (existingLedger.StaffName ?? "") != staffName ||
                                            (existingLedger.Note ?? "") != note ||
                                            existingLedger.Income != income ||
                                            existingLedger.Expense != expense ||
                                            existingLedger.Balance != balance ||
                                            existingLedger.Date != date;
                            if (hasChanges)
                            {
                                isUpdate = true;
                                existingLedgerForUpdate = existingLedger;
                            }
                            else if (skipExisting)
                            {
                                // Issue #903: skipExisting=trueの場合のみ、変更がないレコードをスキップ
                                // Issue #754: 残高整合性チェック用にはCSVの全レコードが必要
                                var skippedLedger = new Ledger
                                {
                                    Id = ledgerId.Value,
                                    CardIdm = cardIdm,
                                    Date = date,
                                    Summary = summary,
                                    Income = income,
                                    Expense = expense,
                                    Balance = balance
                                };
                                allRecordsForValidation.Add((lineNumber, skippedLedger, false));
                                skippedCount++;
                                continue;
                            }
                            else
                            {
                                // Issue #903: skipExisting=falseの場合、変更がなくても更新扱い
                                isUpdate = true;
                                existingLedgerForUpdate = existingLedger;
                            }
                        }
                    }

                    var ledger = new Ledger
                    {
                        Id = ledgerId ?? 0,
                        CardIdm = cardIdm,
                        Date = date,
                        Summary = summary,
                        Income = income,
                        Expense = expense,
                        Balance = balance,
                        StaffName = string.IsNullOrWhiteSpace(staffName) ? null : staffName,
                        Note = string.IsNullOrWhiteSpace(note) ? null : note,
                        // Issue #639: 更新時はCSVに含まれないフィールドを既存レコードから引き継ぐ
                        LenderIdm = existingLedgerForUpdate?.LenderIdm,
                        ReturnerIdm = existingLedgerForUpdate?.ReturnerIdm,
                        LentAt = existingLedgerForUpdate?.LentAt,
                        ReturnedAt = existingLedgerForUpdate?.ReturnedAt,
                        IsLentRecord = existingLedgerForUpdate?.IsLentRecord ?? false
                    };

                    validRecords.Add((lineNumber, ledger, isUpdate));
                    allRecordsForValidation.Add((lineNumber, ledger, isUpdate));
                }

                // Issue #907: カードごとにDB上の直前残高を取得（最初の行の整合性チェック用）
                var previousBalanceByCard = await GetPreviousBalanceByCardAsync(allRecordsForValidation
                    .GroupBy(r => r.Ledger.CardIdm, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Min(r => r.Ledger.Date)));

                // Issue #754: 残高整合性チェックはスキップ分を含む全レコードで実施
                // （スキップされたレコードを除外すると前後関係が崩れ、誤った前回残高でエラーになる）
                // Issue #907: 最初の行もDB上の直前残高と照合
                ValidateBalanceConsistencyForLedgers(allRecordsForValidation, errors, previousBalanceByCard);

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

                // Issue #334: 新規追加分のみ既存履歴の重複チェック用キーを取得
                // Issue #903: skipExisting=falseの場合は重複チェックを行わず全レコードを登録する
                var newRecords = validRecords.Where(r => !r.IsUpdate).ToList();
                var uniqueCardIdms = newRecords.Select(r => r.Ledger.CardIdm).Distinct();
                var existingLedgerKeys = skipExisting
                    ? await _ledgerRepository.GetExistingLedgerKeysAsync(uniqueCardIdms)
                    : new HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>();

                // インポート実行（履歴はトランザクションなしで直接インポート）
                foreach (var (lineNumber, ledger, isUpdate) in validRecords)
                {
                    try
                    {
                        if (isUpdate)
                        {
                            // 既存レコードを更新
                            var success = await _ledgerRepository.UpdateAsync(ledger);
                            if (success)
                            {
                                updatedCount++;
                            }
                            else
                            {
                                errors.Add(new CsvImportError
                                {
                                    LineNumber = lineNumber,
                                    Message = "履歴の更新に失敗しました",
                                    Data = ledger.CardIdm
                                });
                            }
                        }
                        else
                        {
                            // 重複チェック: skipExisting=trueの場合、同じ履歴が既に存在すればスキップ
                            var ledgerKey = (ledger.CardIdm, ledger.Date, ledger.Summary, ledger.Income, ledger.Expense, ledger.Balance);
                            if (existingLedgerKeys.Contains(ledgerKey))
                            {
                                skippedCount++;
                                continue;
                            }

                            // 新規登録
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
                    }
                    catch (Exception ex)
                    {
                        var action = isUpdate ? "更新" : "登録";
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"履歴の{action}中にエラーが発生しました: {ex.Message}",
                            Data = ledger.CardIdm
                        });
                    }
                }

                return new CsvImportResult
                {
                    Success = errors.Count == 0,
                    ImportedCount = importedCount + updatedCount,
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
        /// <param name="skipExisting">既存データをスキップするか（falseの場合は更新）</param>
        /// <param name="targetCardIdm">CSV内のIDmが空の場合に使用するカードIDm（オプション）</param>
        /// <remarks>Issue #511: CSVのIDm列が空の場合、targetCardIdmが指定されていればそのIDmを使用</remarks>
        public async Task<CsvImportPreviewResult> PreviewLedgersAsync(string filePath, bool skipExisting = true, string? targetCardIdm = null)
        {
            var errors = new List<CsvImportError>();
            var items = new List<CsvImportPreviewItem>();
            var newCount = 0;
            var updateCount = 0;
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

                // ヘッダー行を解析してID列の有無を判定
                var headerFields = ParseCsvLine(lines[0]);
                var hasIdColumn = headerFields.Count > 0 && headerFields[0].Trim().Equals("ID", StringComparison.OrdinalIgnoreCase);
                var minColumns = hasIdColumn ? 10 : 9;

                // 全カードのIDmをキャッシュ
                var existingCardIdms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cardNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var allCards = await _cardRepository.GetAllIncludingDeletedAsync();
                foreach (var card in allCards)
                {
                    existingCardIdms.Add(card.CardIdm);
                    cardNameMap[card.CardIdm] = $"{card.CardType} {card.CardNumber}".Trim();
                }

                // 仮バリデーションでカードIDmを収集（重複チェック用）
                var cardIdmsInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var validatedRecords = new List<(int LineNumber, int? LedgerId, string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance, string StaffName, string Note)>();

                for (var i = 1; i < lines.Count; i++)
                {
                    var lineNumber = i + 1;
                    var line = lines[i];

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var fields = ParseCsvLine(line);

                    if (fields.Count < minColumns)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"列数が不足しています（{minColumns}列必要）",
                            Data = line
                        });
                        continue;
                    }

                    // フィールドのインデックスを調整（ID列の有無による）
                    var offset = hasIdColumn ? 1 : 0;
                    var idStr = hasIdColumn ? fields[0].Trim() : "";
                    var dateStr = fields[0 + offset].Trim();
                    var cardIdm = fields[1 + offset].Trim().ToUpperInvariant(); // IDmは大文字に正規化
                    // fields[2 + offset] は管理番号（参照用）
                    var summary = fields[3 + offset].Trim();
                    var incomeStr = fields[4 + offset].Trim();
                    var expenseStr = fields[5 + offset].Trim();
                    var balanceStr = fields[6 + offset].Trim();
                    var staffName = fields[7 + offset].Trim();
                    var note = fields[8 + offset].Trim();

                    // ID列がある場合、IDを解析
                    int? ledgerId = null;
                    if (hasIdColumn && !string.IsNullOrWhiteSpace(idStr))
                    {
                        if (!int.TryParse(idStr, out var parsedId))
                        {
                            errors.Add(new CsvImportError
                            {
                                LineNumber = lineNumber,
                                Message = "IDの形式が不正です",
                                Data = idStr
                            });
                            continue;
                        }
                        ledgerId = parsedId;
                    }

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
                    // Issue #511: IDmが空の場合、targetCardIdmを使用
                    if (string.IsNullOrWhiteSpace(cardIdm))
                    {
                        if (!string.IsNullOrWhiteSpace(targetCardIdm))
                        {
                            cardIdm = targetCardIdm.ToUpperInvariant();
                        }
                        else
                        {
                            errors.Add(new CsvImportError
                            {
                                LineNumber = lineNumber,
                                Message = "カードIDmは必須です（CSVで空欄の場合はインポート先カードを指定してください）",
                                Data = line
                            });
                            continue;
                        }
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
                    validatedRecords.Add((lineNumber, ledgerId, cardIdm, date, summary, income, expense, balance, staffName, note));
                }

                // Issue #907: カードごとにDB上の直前残高を取得（最初の行の整合性チェック用）
                var previousBalanceByCard = await GetPreviousBalanceByCardAsync(validatedRecords
                    .GroupBy(r => r.CardIdm, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Min(r => r.Date)));

                // Issue #428: 金額の整合性チェック（カードごとに残高の連続性を検証）
                // Issue #907: 最初の行もDB上の直前残高と照合
                ValidateBalanceConsistency(validatedRecords, errors, previousBalanceByCard);

                // Issue #334: 既存履歴の重複チェック用キーを取得（新規追加分のみ）
                // Issue #903: skipExisting=falseの場合は重複チェックを行わない
                var existingLedgerKeys = skipExisting
                    ? await _ledgerRepository.GetExistingLedgerKeysAsync(cardIdmsInFile)
                    : new HashSet<(string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance)>();

                // プレビューアイテムを生成
                foreach (var (lineNumber, ledgerId, cardIdm, date, summary, income, expense, balance, staffName, note) in validatedRecords)
                {
                    ImportAction action;
                    var changes = new List<FieldChange>();

                    if (ledgerId.HasValue)
                    {
                        // IDがある場合は既存レコードを検索
                        var existingLedger = await _ledgerRepository.GetByIdAsync(ledgerId.Value);
                        if (existingLedger != null)
                        {
                            // Issue #639: 金額・日付を含む全フィールドで変更点を検出
                            DetectLedgerChanges(existingLedger, date, summary, income, expense, balance, staffName, note, changes);
                            if (changes.Count > 0)
                            {
                                // 変更がある場合は更新
                                action = ImportAction.Update;
                                updateCount++;
                            }
                            else if (skipExisting)
                            {
                                // Issue #903: skipExisting=trueの場合のみスキップ
                                action = ImportAction.Skip;
                                skipCount++;
                            }
                            else
                            {
                                // Issue #903: skipExisting=falseの場合、変更がなくても更新扱い
                                action = ImportAction.Update;
                                updateCount++;
                            }
                        }
                        else
                        {
                            // IDが指定されているがレコードが見つからない場合は新規追加
                            action = ImportAction.Insert;
                            newCount++;
                        }
                    }
                    else
                    {
                        // IDがない場合は従来の重複チェック
                        var ledgerKey = (cardIdm, date, summary, income, expense, balance);
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
                    }

                    // Issue #937: カード名も表示する
                    var cardDisplayIdm = cardNameMap.TryGetValue(cardIdm, out var displayName) && !string.IsNullOrEmpty(displayName)
                        ? $"{displayName} ({cardIdm})"
                        : cardIdm;

                    items.Add(new CsvImportPreviewItem
                    {
                        LineNumber = lineNumber,
                        Idm = cardDisplayIdm,
                        Name = summary,
                        AdditionalInfo = date.ToString("yyyy-MM-dd HH:mm:ss"),
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
        /// 利用履歴詳細CSVのインポートプレビューを取得
        /// </summary>
        /// <remarks>
        /// Issue #751対応: ledger_detailのCSVインポート。
        /// ledger_idごとにグループ化し、全置換（ReplaceDetailsAsync）で復元する。
        /// </remarks>
        /// <param name="filePath">CSVファイルパス</param>
        public async Task<CsvImportPreviewResult> PreviewLedgerDetailsAsync(string filePath)
        {
            var errors = new List<CsvImportError>();
            return await ExecutePreviewWithErrorHandlingAsync(
                () => PreviewLedgerDetailsInternalAsync(filePath, errors),
                errors);
        }

        /// <summary>
        /// 利用履歴詳細CSVプレビューの内部処理
        /// </summary>
        private async Task<CsvImportPreviewResult> PreviewLedgerDetailsInternalAsync(
            string filePath,
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

            // Issue #937: カード名表示のためにカード情報を取得
            var allCards = await _cardRepository.GetAllIncludingDeletedAsync();
            var cardNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in allCards)
            {
                cardNameMap[c.CardIdm] = $"{c.CardType} {c.CardNumber}".Trim();
            }

            // パースされた詳細をledger_idごとにグループ化（既存ledger向け）
            var detailsByLedgerId = new Dictionary<int, List<(int LineNumber, LedgerDetail Detail)>>();
            // 既存の詳細をキャッシュ（比較用）
            var existingDetailsByLedgerId = new Dictionary<int, List<LedgerDetail>>();
            // ledger_idからカードIDmへのマッピング（プレビュー表示用）
            var ledgerCardIdmMap = new Dictionary<int, string>();
            // Issue #906: 利用履歴ID空欄の新規詳細をカードIDm＋日付ごとにグループ化
            // Issue #918: カードIDmだけでなく日付でもグループ化し、日付ごとに個別のLedgerを作成
            var newDetailsByCardIdmAndDate = new Dictionary<(string CardIdm, DateTime Date), List<(int LineNumber, LedgerDetail Detail)>>();

            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                // 13列必要
                if (!ValidateColumnCount(fields, 13, lineNumber, line, errors))
                {
                    continue;
                }

                var detail = ParseLedgerDetailFields(fields, lineNumber, line, errors);
                if (detail == null)
                {
                    continue;
                }

                // Issue #906: 利用履歴ID空欄（LedgerId == 0）の場合は新規作成
                if (detail.LedgerId == 0)
                {
                    var cardIdm = fields[2].Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(cardIdm))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "利用履歴IDが空欄の場合、カードIDmは必須です",
                            Data = line
                        });
                        continue;
                    }

                    // カード存在チェック
                    var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
                    if (card == null)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"カードIDm {cardIdm} が登録されていません",
                            Data = cardIdm
                        });
                        continue;
                    }

                    // Issue #918: 日付でもグループ化（日付がない場合はDateTime.MinValueをキーにする）
                    var dateKey = detail.UseDate?.Date ?? DateTime.MinValue;
                    var groupKey = (cardIdm, dateKey);
                    if (!newDetailsByCardIdmAndDate.ContainsKey(groupKey))
                    {
                        newDetailsByCardIdmAndDate[groupKey] = new List<(int, LedgerDetail)>();
                    }
                    newDetailsByCardIdmAndDate[groupKey].Add((lineNumber, detail));
                    continue;
                }

                // 既存ledger_idの存在チェック
                if (!existingDetailsByLedgerId.ContainsKey(detail.LedgerId))
                {
                    var ledger = await _ledgerRepository.GetByIdAsync(detail.LedgerId);
                    if (ledger == null)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"利用履歴ID {detail.LedgerId} が存在しません",
                            Data = detail.LedgerId.ToString()
                        });
                        continue;
                    }
                    existingDetailsByLedgerId[detail.LedgerId] = ledger.Details ?? new List<LedgerDetail>();
                    ledgerCardIdmMap[detail.LedgerId] = ledger.CardIdm ?? "";
                }

                if (!detailsByLedgerId.ContainsKey(detail.LedgerId))
                {
                    detailsByLedgerId[detail.LedgerId] = new List<(int, LedgerDetail)>();
                }
                detailsByLedgerId[detail.LedgerId].Add((lineNumber, detail));
            }

            // Issue #906: 新規詳細（利用履歴ID空欄）のプレビューアイテム生成
            // Issue #918: カードIDm＋日付ごとにグループ化して表示
            foreach (var kvp in newDetailsByCardIdmAndDate.OrderBy(x => x.Key.CardIdm).ThenBy(x => x.Key.Date))
            {
                var cardIdm = kvp.Key.CardIdm;
                var date = kvp.Key.Date;
                var detailRows = kvp.Value;
                var dateStr = date == DateTime.MinValue ? "" : $" ({date:yyyy-MM-dd})";

                // Issue #937: カード名も表示する
                var cardDisplayName = cardNameMap.TryGetValue(cardIdm, out var newDetailCardName) && !string.IsNullOrEmpty(newDetailCardName)
                    ? $"{newDetailCardName} ({cardIdm})"
                    : cardIdm;

                // Issue #938: 追加する内容の詳細を表示
                var insertDetails = detailRows.Select(x => x.Detail).ToList();
                var insertChanges = CreateInsertDetailChanges(insertDetails);

                items.Add(new CsvImportPreviewItem
                {
                    LineNumber = detailRows.First().LineNumber,
                    Idm = "(自動付与)",
                    Name = cardDisplayName,
                    AdditionalInfo = $"{detailRows.Count}件{dateStr}",
                    Action = ImportAction.Insert,
                    Changes = insertChanges
                });
                newCount++;
            }

            // 既存ledger_idごとにプレビューアイテム生成
            foreach (var kvp in detailsByLedgerId.OrderBy(x => x.Key))
            {
                var ledgerId = kvp.Key;
                var detailRows = kvp.Value;
                var newDetails = detailRows.Select(x => x.Detail).ToList();
                var existingDetails = existingDetailsByLedgerId.TryGetValue(ledgerId, out var cached) ? cached : new List<LedgerDetail>();

                // 既存データとの変更検出
                var changes = new List<FieldChange>();
                DetectLedgerDetailChanges(existingDetails, newDetails, changes);

                ImportAction action;
                if (changes.Count > 0)
                {
                    action = ImportAction.Update;
                    updateCount++;
                }
                else
                {
                    action = ImportAction.Skip;
                    skipCount++;
                }

                var cardIdm = ledgerCardIdmMap.TryGetValue(ledgerId, out var idm) ? idm : "";

                // Issue #937: カード名も表示する
                var existingCardDisplayName = cardNameMap.TryGetValue(cardIdm, out var existingCardName) && !string.IsNullOrEmpty(existingCardName)
                    ? $"{existingCardName} ({cardIdm})"
                    : cardIdm;

                items.Add(new CsvImportPreviewItem
                {
                    LineNumber = detailRows.First().LineNumber,
                    Idm = ledgerId.ToString(),
                    Name = existingCardDisplayName,
                    AdditionalInfo = $"{detailRows.Count}件",
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
        /// 利用履歴詳細CSVをインポート
        /// </summary>
        /// <remarks>
        /// Issue #751対応: ledger_idごとにグループ化し、ReplaceDetailsAsyncで全置換する。
        /// </remarks>
        /// <param name="filePath">CSVファイルパス</param>
        public virtual async Task<CsvImportResult> ImportLedgerDetailsAsync(string filePath)
        {
            var errors = new List<CsvImportError>();
            return await ExecuteImportWithErrorHandlingAsync(
                () => ImportLedgerDetailsInternalAsync(filePath, errors),
                errors);
        }

        /// <summary>
        /// 利用履歴詳細CSVインポートの内部処理
        /// </summary>
        private async Task<CsvImportResult> ImportLedgerDetailsInternalAsync(
            string filePath,
            List<CsvImportError> errors)
        {
            var importedCount = 0;

            var lines = await ReadCsvFileAsync(filePath);
            if (lines.Count < 2)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            // パースされた詳細をledger_idごとにグループ化（既存ledger向け）
            var detailsByLedgerId = new Dictionary<int, List<(int LineNumber, LedgerDetail Detail)>>();
            // 既存の詳細をキャッシュ（変更検出用）
            var existingDetailsByLedgerId = new Dictionary<int, List<LedgerDetail>>();
            // Issue #906: 利用履歴ID空欄の新規詳細をカードIDm＋日付ごとにグループ化
            // Issue #918: カードIDmだけでなく日付でもグループ化し、日付ごとに個別のLedgerを作成
            var newDetailsByCardIdmAndDate = new Dictionary<(string CardIdm, DateTime Date), List<(int LineNumber, LedgerDetail Detail)>>();

            for (var i = 1; i < lines.Count; i++)
            {
                var lineNumber = i + 1;
                var line = lines[i];

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fields = ParseCsvLine(line);

                // 13列必要
                if (!ValidateColumnCount(fields, 13, lineNumber, line, errors))
                {
                    continue;
                }

                var detail = ParseLedgerDetailFields(fields, lineNumber, line, errors);
                if (detail == null)
                {
                    continue;
                }

                // Issue #906: 利用履歴ID空欄（LedgerId == 0）の場合は新規作成
                if (detail.LedgerId == 0)
                {
                    var cardIdm = fields[2].Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(cardIdm))
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = "利用履歴IDが空欄の場合、カードIDmは必須です",
                            Data = line
                        });
                        continue;
                    }

                    // カード存在チェック
                    var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true);
                    if (card == null)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"カードIDm {cardIdm} が登録されていません",
                            Data = cardIdm
                        });
                        continue;
                    }

                    // Issue #918: 日付でもグループ化（日付がない場合はDateTime.MinValueをキーにする）
                    var dateKey = detail.UseDate?.Date ?? DateTime.MinValue;
                    var groupKey = (cardIdm, dateKey);
                    if (!newDetailsByCardIdmAndDate.ContainsKey(groupKey))
                    {
                        newDetailsByCardIdmAndDate[groupKey] = new List<(int, LedgerDetail)>();
                    }
                    newDetailsByCardIdmAndDate[groupKey].Add((lineNumber, detail));
                    continue;
                }

                // 既存ledger_idの存在チェック
                if (!existingDetailsByLedgerId.ContainsKey(detail.LedgerId))
                {
                    var ledger = await _ledgerRepository.GetByIdAsync(detail.LedgerId);
                    if (ledger == null)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"利用履歴ID {detail.LedgerId} が存在しません",
                            Data = detail.LedgerId.ToString()
                        });
                        continue;
                    }
                    existingDetailsByLedgerId[detail.LedgerId] = ledger.Details ?? new List<LedgerDetail>();
                }

                if (!detailsByLedgerId.ContainsKey(detail.LedgerId))
                {
                    detailsByLedgerId[detail.LedgerId] = new List<(int, LedgerDetail)>();
                }
                detailsByLedgerId[detail.LedgerId].Add((lineNumber, detail));
            }

            // バリデーションエラーがあれば中断
            if (errors.Count > 0)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ImportedCount = 0,
                    ErrorCount = errors.Count,
                    Errors = errors
                };
            }

            // データがない場合
            if (detailsByLedgerId.Count == 0 && newDetailsByCardIdmAndDate.Count == 0)
            {
                return new CsvImportResult
                {
                    Success = false,
                    ErrorMessage = "インポートするデータがありません"
                };
            }

            // Issue #906: 新規詳細（利用履歴ID空欄）のLedger自動作成とインポート
            // Issue #918: カードIDm＋日付ごとにグループ化して個別のLedgerを作成
            foreach (var kvp in newDetailsByCardIdmAndDate)
            {
                var cardIdm = kvp.Key.CardIdm;
                var detailRows = kvp.Value;
                var firstLineNumber = detailRows.First().LineNumber;
                var detailList = detailRows.Select(r => r.Detail).ToList();

                try
                {
                    // SummaryGeneratorで摘要を自動生成
                    var summaryGenerator = new SummaryGenerator();
                    var summary = summaryGenerator.Generate(detailList);
                    if (string.IsNullOrEmpty(summary))
                    {
                        summary = "CSVインポート";
                    }

                    // LedgerSplitServiceと同じロジックで収支・残高を計算
                    var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(detailList);

                    // 日付はグループのキーから取得、DateTime.MinValueの場合は最も古い利用日時、なければ現在日時
                    var date = kvp.Key.Date;
                    if (date == DateTime.MinValue)
                    {
                        date = detailList
                            .Where(d => d.UseDate.HasValue)
                            .OrderBy(d => d.UseDate!.Value)
                            .Select(d => d.UseDate!.Value)
                            .FirstOrDefault();
                        if (date == default)
                        {
                            date = DateTime.Now;
                        }
                    }

                    // Ledgerレコードを自動作成
                    var newLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = date,
                        Summary = summary,
                        Income = income,
                        Expense = expense,
                        Balance = balance
                    };

                    var newLedgerId = await _ledgerRepository.InsertAsync(newLedger);

                    // 詳細をインサート
                    var success = await _ledgerRepository.InsertDetailsAsync(newLedgerId, detailList);

                    if (success)
                    {
                        importedCount += detailRows.Count;
                    }
                    else
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstLineNumber,
                            Message = $"カード {cardIdm} の新規詳細の挿入に失敗しました",
                            Data = cardIdm
                        });
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = firstLineNumber,
                        Message = $"カード {cardIdm} の利用履歴自動作成中にエラーが発生しました: {ex.Message}",
                        Data = cardIdm
                    });
                }
            }

            // 既存ledger_idごとにReplaceDetailsAsyncで全置換（変更がある場合のみ）
            var skippedCount = 0;
            foreach (var kvp in detailsByLedgerId)
            {
                var ledgerId = kvp.Key;
                var detailRows = kvp.Value;
                var firstLineNumber = detailRows.First().LineNumber;

                // 変更検出：既存データと同一ならスキップ
                var newDetails = detailRows.Select(r => r.Detail).ToList();
                var existingDetails = existingDetailsByLedgerId.TryGetValue(ledgerId, out var cached) ? cached : new List<LedgerDetail>();
                var changes = new List<FieldChange>();
                DetectLedgerDetailChanges(existingDetails, newDetails, changes);
                if (changes.Count == 0)
                {
                    skippedCount += detailRows.Count;
                    continue;
                }

                try
                {
                    var success = await _ledgerRepository.ReplaceDetailsAsync(ledgerId, newDetails);

                    if (success)
                    {
                        // Issue #918: 詳細置換後、親Ledgerの金額を再計算して更新
                        var ledger = await _ledgerRepository.GetByIdAsync(ledgerId);
                        if (ledger != null)
                        {
                            var summaryGenerator = new SummaryGenerator();
                            var summary = summaryGenerator.Generate(newDetails);
                            var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(newDetails);

                            ledger.Summary = !string.IsNullOrEmpty(summary) ? summary : ledger.Summary;
                            ledger.Income = income;
                            ledger.Expense = expense;
                            ledger.Balance = balance;
                            await _ledgerRepository.UpdateAsync(ledger);
                        }

                        importedCount += detailRows.Count;
                    }
                    else
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstLineNumber,
                            Message = $"利用履歴ID {ledgerId} の詳細の置換に失敗しました",
                            Data = ledgerId.ToString()
                        });
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = firstLineNumber,
                        Message = $"利用履歴ID {ledgerId} の詳細の置換中にエラーが発生しました: {ex.Message}",
                        Data = ledgerId.ToString()
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

        /// <summary>
        /// CSVフィールドからLedgerDetailをパース
        /// </summary>
        /// <param name="fields">パース済みフィールド（13列）</param>
        /// <param name="lineNumber">行番号（エラー報告用）</param>
        /// <param name="line">元の行データ（エラー報告用）</param>
        /// <param name="errors">エラーリスト</param>
        /// <returns>パース成功時はLedgerDetail、失敗時はnull</returns>
        private static LedgerDetail ParseLedgerDetailFields(
            List<string> fields,
            int lineNumber,
            string line,
            List<CsvImportError> errors)
        {
            // [0]利用履歴ID [1]利用日時 [2]カードIDm [3]管理番号 [4]乗車駅 [5]降車駅
            // [6]バス停 [7]金額 [8]残額 [9]チャージ [10]ポイント還元 [11]バス利用 [12]グループID

            var ledgerIdStr = fields[0].Trim();
            var useDateStr = fields[1].Trim();
            // fields[2] カードIDm（利用履歴ID空欄時の自動作成で使用）
            // fields[3] 管理番号（参照用）
            var entryStation = fields[4].Trim();
            var exitStation = fields[5].Trim();
            var busStops = fields[6].Trim();
            var amountStr = fields[7].Trim();
            var balanceStr = fields[8].Trim();
            var isChargeStr = fields[9].Trim();
            var isPointRedemptionStr = fields[10].Trim();
            var isBusStr = fields[11].Trim();
            var groupIdStr = fields[12].Trim();

            // 利用履歴ID: 空欄の場合は0（自動付与）、それ以外は整数
            int ledgerId = 0;
            if (!string.IsNullOrWhiteSpace(ledgerIdStr))
            {
                if (!int.TryParse(ledgerIdStr, out ledgerId))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "利用履歴IDの形式が不正です",
                        Data = ledgerIdStr
                    });
                    return null;
                }
            }

            // 利用日時: 任意（空欄=null）
            DateTime? useDate = null;
            if (!string.IsNullOrWhiteSpace(useDateStr))
            {
                if (!DateTime.TryParse(useDateStr, out var parsedDate))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "利用日時の形式が不正です",
                        Data = useDateStr
                    });
                    return null;
                }
                useDate = parsedDate;
            }

            // 金額: 任意（空欄=null）
            int? amount = null;
            if (!string.IsNullOrWhiteSpace(amountStr))
            {
                if (!int.TryParse(amountStr, out var parsedAmount))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "金額の形式が不正です",
                        Data = amountStr
                    });
                    return null;
                }
                amount = parsedAmount;
            }

            // 残額: 任意（空欄=null）
            int? balance = null;
            if (!string.IsNullOrWhiteSpace(balanceStr))
            {
                if (!int.TryParse(balanceStr, out var parsedBalance))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "残額の形式が不正です",
                        Data = balanceStr
                    });
                    return null;
                }
                balance = parsedBalance;
            }

            // チャージ: 0 or 1
            if (!ValidateBooleanField(isChargeStr, lineNumber, "チャージ", errors, out var isCharge))
            {
                return null;
            }

            // ポイント還元: 0 or 1
            if (!ValidateBooleanField(isPointRedemptionStr, lineNumber, "ポイント還元", errors, out var isPointRedemption))
            {
                return null;
            }

            // バス利用: 0 or 1
            if (!ValidateBooleanField(isBusStr, lineNumber, "バス利用", errors, out var isBus))
            {
                return null;
            }

            // グループID: 任意（空欄=null）
            int? groupId = null;
            if (!string.IsNullOrWhiteSpace(groupIdStr))
            {
                if (!int.TryParse(groupIdStr, out var parsedGroupId))
                {
                    errors.Add(new CsvImportError
                    {
                        LineNumber = lineNumber,
                        Message = "グループIDの形式が不正です",
                        Data = groupIdStr
                    });
                    return null;
                }
                groupId = parsedGroupId;
            }

            return new LedgerDetail
            {
                LedgerId = ledgerId,
                UseDate = useDate,
                EntryStation = string.IsNullOrWhiteSpace(entryStation) ? null : entryStation,
                ExitStation = string.IsNullOrWhiteSpace(exitStation) ? null : exitStation,
                BusStops = string.IsNullOrWhiteSpace(busStops) ? null : busStops,
                Amount = amount,
                Balance = balance,
                IsCharge = isCharge,
                IsPointRedemption = isPointRedemption,
                IsBus = isBus,
                GroupId = groupId
            };
        }

        /// <summary>
        /// ブール値フィールド（0/1）のバリデーション
        /// </summary>
        /// <param name="value">検証する値</param>
        /// <param name="lineNumber">行番号</param>
        /// <param name="fieldName">フィールド名</param>
        /// <param name="errors">エラーリスト</param>
        /// <param name="result">パース結果</param>
        /// <returns>バリデーション成功の場合true</returns>
        private static bool ValidateBooleanField(
            string value,
            int lineNumber,
            string fieldName,
            List<CsvImportError> errors,
            out bool result)
        {
            result = false;
            if (value == "0")
            {
                result = false;
                return true;
            }
            if (value == "1")
            {
                result = true;
                return true;
            }

            errors.Add(new CsvImportError
            {
                LineNumber = lineNumber,
                Message = $"{fieldName}は0または1で指定してください",
                Data = value
            });
            return false;
        }

        /// <summary>
        /// CSVファイルを読み込み（UTF-8 BOM対応）
        /// </summary>
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

        /// <summary>
        /// 履歴データの変更点を検出
        /// </summary>
        /// <param name="existingLedger">既存の履歴</param>
        /// <param name="newDate">新しい日付</param>
        /// <param name="newSummary">新しい摘要</param>
        /// <param name="newIncome">新しい受入金額</param>
        /// <param name="newExpense">新しい払出金額</param>
        /// <param name="newBalance">新しい残高</param>
        /// <param name="newStaffName">新しい利用者名</param>
        /// <param name="newNote">新しい備考</param>
        /// <param name="changes">変更点リスト（検出結果が追加される）</param>
        private static void DetectLedgerChanges(
            Ledger existingLedger,
            DateTime newDate,
            string newSummary,
            int newIncome,
            int newExpense,
            int newBalance,
            string newStaffName,
            string newNote,
            List<FieldChange> changes)
        {
            // Issue #639: 金額・日付フィールドの変更も検出
            if (existingLedger.Date != newDate)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "日時",
                    OldValue = existingLedger.Date.ToString("yyyy-MM-dd HH:mm:ss"),
                    NewValue = newDate.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }

            if (existingLedger.Summary != newSummary)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "摘要",
                    OldValue = existingLedger.Summary ?? "(なし)",
                    NewValue = newSummary
                });
            }

            if (existingLedger.Income != newIncome)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "受入金額",
                    OldValue = $"{existingLedger.Income}円",
                    NewValue = $"{newIncome}円"
                });
            }

            if (existingLedger.Expense != newExpense)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "払出金額",
                    OldValue = $"{existingLedger.Expense}円",
                    NewValue = $"{newExpense}円"
                });
            }

            if (existingLedger.Balance != newBalance)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "残額",
                    OldValue = $"{existingLedger.Balance}円",
                    NewValue = $"{newBalance}円"
                });
            }

            var existingStaffName = existingLedger.StaffName ?? "";
            if (existingStaffName != newStaffName)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "利用者",
                    OldValue = string.IsNullOrEmpty(existingStaffName) ? "(なし)" : existingStaffName,
                    NewValue = string.IsNullOrEmpty(newStaffName) ? "(なし)" : newStaffName
                });
            }

            var existingNote = existingLedger.Note ?? "";
            if (existingNote != newNote)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "備考",
                    OldValue = string.IsNullOrEmpty(existingNote) ? "(なし)" : existingNote,
                    NewValue = string.IsNullOrEmpty(newNote) ? "(なし)" : newNote
                });
            }
        }

        /// <summary>
        /// 利用履歴詳細の変更検出
        /// 既存の詳細リストとインポート対象の詳細リストを比較し、差分を検出する。
        /// </summary>
        private static void DetectLedgerDetailChanges(
            List<LedgerDetail> existingDetails,
            List<LedgerDetail> newDetails,
            List<FieldChange> changes)
        {
            if (existingDetails.Count != newDetails.Count)
            {
                changes.Add(new FieldChange
                {
                    FieldName = "詳細件数",
                    OldValue = $"{existingDetails.Count}件",
                    NewValue = $"{newDetails.Count}件"
                });
                return;
            }

            for (var i = 0; i < existingDetails.Count; i++)
            {
                var existing = existingDetails[i];
                var imported = newDetails[i];
                var rowLabel = $"[{i + 1}行目]";

                if (existing.UseDate != imported.UseDate)
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} 利用日時",
                        OldValue = existing.UseDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(なし)",
                        NewValue = imported.UseDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(なし)"
                    });
                }

                if ((existing.EntryStation ?? "") != (imported.EntryStation ?? ""))
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} 乗車駅",
                        OldValue = string.IsNullOrEmpty(existing.EntryStation) ? "(なし)" : existing.EntryStation,
                        NewValue = string.IsNullOrEmpty(imported.EntryStation) ? "(なし)" : imported.EntryStation
                    });
                }

                if ((existing.ExitStation ?? "") != (imported.ExitStation ?? ""))
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} 降車駅",
                        OldValue = string.IsNullOrEmpty(existing.ExitStation) ? "(なし)" : existing.ExitStation,
                        NewValue = string.IsNullOrEmpty(imported.ExitStation) ? "(なし)" : imported.ExitStation
                    });
                }

                if ((existing.BusStops ?? "") != (imported.BusStops ?? ""))
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} バス停",
                        OldValue = string.IsNullOrEmpty(existing.BusStops) ? "(なし)" : existing.BusStops,
                        NewValue = string.IsNullOrEmpty(imported.BusStops) ? "(なし)" : imported.BusStops
                    });
                }

                if (existing.Amount != imported.Amount)
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} 金額",
                        OldValue = existing.Amount?.ToString() ?? "(なし)",
                        NewValue = imported.Amount?.ToString() ?? "(なし)"
                    });
                }

                if (existing.Balance != imported.Balance)
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} 残額",
                        OldValue = existing.Balance?.ToString() ?? "(なし)",
                        NewValue = imported.Balance?.ToString() ?? "(なし)"
                    });
                }

                if (existing.IsCharge != imported.IsCharge)
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} チャージ",
                        OldValue = existing.IsCharge ? "1" : "0",
                        NewValue = imported.IsCharge ? "1" : "0"
                    });
                }

                if (existing.IsPointRedemption != imported.IsPointRedemption)
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} ポイント還元",
                        OldValue = existing.IsPointRedemption ? "1" : "0",
                        NewValue = imported.IsPointRedemption ? "1" : "0"
                    });
                }

                if (existing.IsBus != imported.IsBus)
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} バス利用",
                        OldValue = existing.IsBus ? "1" : "0",
                        NewValue = imported.IsBus ? "1" : "0"
                    });
                }

                if (existing.GroupId != imported.GroupId)
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} グループID",
                        OldValue = existing.GroupId?.ToString() ?? "(なし)",
                        NewValue = imported.GroupId?.ToString() ?? "(なし)"
                    });
                }
            }
        }

        /// <summary>
        /// Issue #938: 新規追加する利用履歴詳細の内容をFieldChangeリストとして生成する。
        /// Insert行の詳細表示用。
        /// </summary>
        internal static List<FieldChange> CreateInsertDetailChanges(List<LedgerDetail> details)
        {
            var changes = new List<FieldChange>();

            for (var i = 0; i < details.Count; i++)
            {
                var detail = details[i];
                var rowLabel = $"[{i + 1}行目]";

                // 利用内容を組み立て
                var description = FormatDetailDescription(detail);

                changes.Add(new FieldChange
                {
                    FieldName = rowLabel,
                    OldValue = "(新規追加)",
                    NewValue = description
                });
            }

            return changes;
        }

        /// <summary>
        /// 利用履歴詳細1件の内容を表示用の文字列にフォーマットする。
        /// </summary>
        internal static string FormatDetailDescription(LedgerDetail detail)
        {
            var parts = new List<string>();

            // 利用日時
            if (detail.UseDate.HasValue)
            {
                parts.Add(detail.UseDate.Value.ToString("yyyy-MM-dd HH:mm"));
            }

            // 区間情報
            if (detail.IsCharge)
            {
                parts.Add("チャージ");
            }
            else if (detail.IsPointRedemption)
            {
                parts.Add("ポイント還元");
            }
            else if (detail.IsBus)
            {
                var busStop = !string.IsNullOrEmpty(detail.BusStops) ? $"バス（{detail.BusStops}）" : "バス";
                parts.Add(busStop);
            }
            else
            {
                var entry = !string.IsNullOrEmpty(detail.EntryStation) ? detail.EntryStation : "?";
                var exit = !string.IsNullOrEmpty(detail.ExitStation) ? detail.ExitStation : "?";
                if (!string.IsNullOrEmpty(detail.EntryStation) || !string.IsNullOrEmpty(detail.ExitStation))
                {
                    parts.Add($"{entry}→{exit}");
                }
            }

            // 金額・残額
            if (detail.Amount.HasValue)
            {
                parts.Add($"{detail.Amount.Value}円");
            }
            if (detail.Balance.HasValue)
            {
                parts.Add($"残額{detail.Balance.Value}円");
            }

            return string.Join(" ", parts);
        }

        /// <summary>
        /// 残高整合性チェック（プレビュー用）
        /// カードごとに日時順で残高の連続性を検証します。
        /// 計算式: 前の残高 + 受入金額 - 払出金額 = 今回の残高
        /// Issue #907: 最初の行もDB上の直前残高と照合します。
        /// </summary>
        /// <param name="records">検証対象レコード（LineNumber, LedgerId, CardIdm, Date, Summary, Income, Expense, Balance, StaffName, Note）</param>
        /// <param name="errors">エラーリスト</param>
        /// <param name="previousBalanceByCard">カードIDmごとのDB上の直前残高（存在しない場合はキーなし）</param>
        internal static void ValidateBalanceConsistency(
            List<(int LineNumber, int? LedgerId, string CardIdm, DateTime Date, string Summary, int Income, int Expense, int Balance, string StaffName, string Note)> records,
            List<CsvImportError> errors,
            Dictionary<string, int> previousBalanceByCard = null)
        {
            if (records.Count == 0) return;

            // カードごとにグループ化して日時順にソート
            var groupedByCard = records
                .GroupBy(r => r.CardIdm, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Date).ThenBy(r => r.LineNumber).ToList());

            foreach (var kvp in groupedByCard)
            {
                var cardIdm = kvp.Key;
                var cardRecords = kvp.Value;

                // Issue #907: 最初の行をDB上の直前残高と照合
                if (cardRecords.Count > 0 && previousBalanceByCard != null &&
                    previousBalanceByCard.TryGetValue(cardIdm.ToUpperInvariant(), out var prevDbBalance))
                {
                    var firstRecord = cardRecords[0];
                    var expectedBalance = prevDbBalance + firstRecord.Income - firstRecord.Expense;
                    if (expectedBalance != firstRecord.Balance)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstRecord.LineNumber,
                            Message = $"残高が一致しません（期待値: {expectedBalance}円、実際: {firstRecord.Balance}円）。" +
                                      $"前回残高（DB）: {prevDbBalance}円 + 受入: {firstRecord.Income}円 - 払出: {firstRecord.Expense}円",
                            Data = cardIdm
                        });
                    }
                }

                for (var i = 1; i < cardRecords.Count; i++)
                {
                    var prevRecord = cardRecords[i - 1];
                    var currentRecord = cardRecords[i];

                    // 期待される残高: 前の残高 + 受入金額 - 払出金額
                    var expectedBalance = prevRecord.Balance + currentRecord.Income - currentRecord.Expense;

                    if (expectedBalance != currentRecord.Balance)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = currentRecord.LineNumber,
                            Message = $"残高が一致しません（期待値: {expectedBalance}円、実際: {currentRecord.Balance}円）。" +
                                      $"前回残高: {prevRecord.Balance}円 + 受入: {currentRecord.Income}円 - 払出: {currentRecord.Expense}円",
                            Data = cardIdm
                        });
                    }
                }
            }
        }

        /// <summary>
        /// 残高整合性チェック（インポート用）
        /// カードごとに日時順で残高の連続性を検証します。
        /// 計算式: 前の残高 + 受入金額 - 払出金額 = 今回の残高
        /// Issue #907: 最初の行もDB上の直前残高と照合します。
        /// </summary>
        /// <param name="records">検証対象レコード（LineNumber, Ledger, IsUpdate）</param>
        /// <param name="errors">エラーリスト</param>
        /// <param name="previousBalanceByCard">カードIDmごとのDB上の直前残高（存在しない場合はキーなし）</param>
        internal static void ValidateBalanceConsistencyForLedgers(
            List<(int LineNumber, Ledger Ledger, bool IsUpdate)> records,
            List<CsvImportError> errors,
            Dictionary<string, int> previousBalanceByCard = null)
        {
            if (records.Count == 0) return;

            // カードごとにグループ化して日時順にソート
            var groupedByCard = records
                .GroupBy(r => r.Ledger.CardIdm, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Ledger.Date).ThenBy(r => r.LineNumber).ToList());

            foreach (var kvp in groupedByCard)
            {
                var cardIdm = kvp.Key;
                var cardRecords = kvp.Value;

                // Issue #907: 最初の行をDB上の直前残高と照合
                if (cardRecords.Count > 0 && previousBalanceByCard != null &&
                    previousBalanceByCard.TryGetValue(cardIdm.ToUpperInvariant(), out var prevDbBalance))
                {
                    var firstRecord = cardRecords[0];
                    var expectedBalance = prevDbBalance + firstRecord.Ledger.Income - firstRecord.Ledger.Expense;
                    if (expectedBalance != firstRecord.Ledger.Balance)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstRecord.LineNumber,
                            Message = $"残高が一致しません（期待値: {expectedBalance}円、実際: {firstRecord.Ledger.Balance}円）。" +
                                      $"前回残高（DB）: {prevDbBalance}円 + 受入: {firstRecord.Ledger.Income}円 - 払出: {firstRecord.Ledger.Expense}円",
                            Data = cardIdm
                        });
                    }
                }

                for (var i = 1; i < cardRecords.Count; i++)
                {
                    var prevRecord = cardRecords[i - 1];
                    var currentRecord = cardRecords[i];

                    // 期待される残高: 前の残高 + 受入金額 - 払出金額
                    var expectedBalance = prevRecord.Ledger.Balance + currentRecord.Ledger.Income - currentRecord.Ledger.Expense;

                    if (expectedBalance != currentRecord.Ledger.Balance)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = currentRecord.LineNumber,
                            Message = $"残高が一致しません（期待値: {expectedBalance}円、実際: {currentRecord.Ledger.Balance}円）。" +
                                      $"前回残高: {prevRecord.Ledger.Balance}円 + 受入: {currentRecord.Ledger.Income}円 - 払出: {currentRecord.Ledger.Expense}円",
                            Data = cardIdm
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Issue #907: カードごとにDB上の直前残高を取得する
        /// CSVの各カードの最も古い日付より前のledgerレコードの残高を返します。
        /// DB上に該当レコードがない場合はキーに含まれません。
        /// </summary>
        /// <param name="earliestDateByCard">カードIDmごとのCSV内の最小日付</param>
        /// <returns>カードIDm（大文字）→直前残高のディクショナリ</returns>
        private async Task<Dictionary<string, int>> GetPreviousBalanceByCardAsync(
            Dictionary<string, DateTime> earliestDateByCard)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in earliestDateByCard)
            {
                var cardIdm = kvp.Key;
                var earliestDate = kvp.Value;

                var previousLedger = await _ledgerRepository.GetLatestBeforeDateAsync(cardIdm, earliestDate);
                if (previousLedger != null)
                {
                    result[cardIdm.ToUpperInvariant()] = previousLedger.Balance;
                }
            }

            return result;
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
