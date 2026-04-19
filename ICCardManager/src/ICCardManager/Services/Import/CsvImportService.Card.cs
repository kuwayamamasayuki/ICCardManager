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
        // === カードCSVインポート・プレビュー ===

        public virtual async Task<CsvImportResult> ImportCardsAsync(string filePath, bool skipExisting = true)
        {
            var errors = new List<CsvImportError>();
            return await ExecuteImportWithErrorHandlingAsync(
                () => ImportCardsInternalAsync(filePath, skipExisting, errors),
                errors).ConfigureAwait(false);
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

            var lines = await ReadCsvFileAsync(filePath).ConfigureAwait(false);
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
                // Issue #1267: note はユーザー自由記述のため式インジェクション対策を適用
                var note = fields.Count > 3
                    ? Infrastructure.Security.FormulaInjectionSanitizer.Sanitize(fields[3].Trim())
                    : "";

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
                var existingCard = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true).ConfigureAwait(false);
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
            using var scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
            try
            {
                foreach (var (lineNumber, card, isUpdate, isRestore) in validRecords)
                {
                    bool success;
                    if (isRestore)
                    {
                        // 削除済みカードを復元してから更新（トランザクション内）
                        success = await _cardRepository.RestoreAsync(card.CardIdm, scope.Transaction).ConfigureAwait(false);
                        if (success)
                        {
                            success = await _cardRepository.UpdateAsync(card, scope.Transaction).ConfigureAwait(false);
                        }
                    }
                    else if (isUpdate)
                    {
                        success = await _cardRepository.UpdateAsync(card, scope.Transaction).ConfigureAwait(false);
                    }
                    else
                    {
                        success = await _cardRepository.InsertAsync(card, scope.Transaction).ConfigureAwait(false);
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
                    scope.Commit();
                    // コミット後にキャッシュを無効化
                    _cacheService.InvalidateByPrefix(CacheKeys.CardPrefixForInvalidation);
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
                    "カードCSVインポートのトランザクション中に SQLite エラーが発生しロールバック");
                throw DatabaseException.QueryFailed("CSV import transaction", ex);
            }
            catch (Exception ex)
            {
                scope.Rollback();
                // Issue #1282: 想定外の例外も握りつぶさずログに記録してから再スロー
                _logger?.LogError(ex,
                    "カードCSVインポートのトランザクション中に想定外の例外が発生しロールバック");
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

        public async Task<CsvImportPreviewResult> PreviewCardsAsync(string filePath, bool skipExisting = true)
        {
            var errors = new List<CsvImportError>();
            return await ExecutePreviewWithErrorHandlingAsync(
                () => PreviewCardsInternalAsync(filePath, skipExisting, errors),
                errors).ConfigureAwait(false);
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

            var lines = await ReadCsvFileAsync(filePath).ConfigureAwait(false);
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
                var existingCard = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true).ConfigureAwait(false);
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
    }
}
