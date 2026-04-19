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
using Microsoft.Extensions.Logging;
using System.Data.SQLite;

namespace ICCardManager.Services
{
    public partial class CsvImportService
    {
        // === 職員CSVインポート・プレビュー ===

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
                // Issue #1267: note はユーザー自由記述のため式インジェクション対策を適用
                var note = fields.Count > 3
                    ? Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[3].Trim())
                    : "";

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
            using var scope = await _dbContext.BeginTransactionAsync();
            try
            {
                foreach (var (lineNumber, staff, isUpdate, isRestore) in validRecords)
                {
                    bool success;
                    if (isRestore)
                    {
                        // 削除済み職員を復元してから更新（トランザクション内）
                        success = await _staffRepository.RestoreAsync(staff.StaffIdm, scope.Transaction);
                        if (success)
                        {
                            success = await _staffRepository.UpdateAsync(staff, scope.Transaction);
                        }
                    }
                    else if (isUpdate)
                    {
                        success = await _staffRepository.UpdateAsync(staff, scope.Transaction);
                    }
                    else
                    {
                        success = await _staffRepository.InsertAsync(staff, scope.Transaction);
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
                    scope.Commit();
                    // コミット後にキャッシュを無効化
                    _cacheService.InvalidateByPrefix(CacheKeys.StaffPrefixForInvalidation);
                }
                else
                {
                    scope.Rollback();
                    importedCount = 0;
                }
            }
            catch (SQLiteException ex)
            {
                scope.Rollback();
                // Issue #1282: SQLiteException は DatabaseException へラップして詳細を保持
                _logger?.LogError(ex,
                    "職員CSVインポートのトランザクション中に SQLite エラーが発生しロールバック");
                throw DatabaseException.QueryFailed("CSV import transaction", ex);
            }
            catch (Exception ex)
            {
                scope.Rollback();
                // Issue #1282: 想定外の例外（IO例外・DB接続断・仮想テーブル解決失敗等）も
                // 握りつぶさずログに痕跡を残してから再スローする。throw; で
                // スタックトレースを保持したまま呼び出し元に伝搬する。
                _logger?.LogError(ex,
                    "職員CSVインポートのトランザクション中に想定外の例外が発生しロールバック");
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
    }
}
