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
                errors,
                "カードCSVの取り込み").ConfigureAwait(false);
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

            var lines = await ReadCsvFileAsync(filePath, _logger).ConfigureAwait(false);
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
                // Issue #1808: テキスト列はエクスポート由来の先頭 ' を取り除いて自然な値で保存する
                //（式インジェクション対策は sink 側のエクスポート／帳票出力が担う。ReadTextField を参照）
                var cardType = ReadTextField(fields, 1);
                var cardNumber = ReadTextField(fields, 2);
                var note = ReadTextField(fields, 3);

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
                        existingCard.Note = NormalizeOptionalText(note);
                        validRecords.Add((lineNumber, existingCard, true, true)); // isRestore = true
                    }
                    else
                    {
                        // Issue #1376: skipExisting=true でも「全項目一致」のみスキップ。
                        // 備考を含むいずれかのフィールドに差分があれば更新する
                        // （Preview 側と同じ判定ロジックを使う）
                        var changes = new List<FieldChange>();
                        DetectCardChanges(existingCard, cardType, cardNumber, note, changes);
                        if (skipExisting && changes.Count == 0)
                        {
                            skippedCount++;
                            continue;
                        }
                        existingCard.CardType = cardType;
                        existingCard.CardNumber = cardNumber;
                        existingCard.Note = NormalizeOptionalText(note);
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
                        Note = NormalizeOptionalText(note)
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
                    try
                    {
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
                    }
                    catch (DuplicateCardNumberException duplicate)
                    {
                        // Issue #1757: 管理番号の重複は CSV の内容に起因する復旧可能な入力ミス。
                        // 他のバリデーションエラーと同じ「行番号 ＋ 行動指示付きの文言」で報告する。
                        // 捕捉しないと下の catch (Exception) から再スローされ、UI には
                        // 何行目が悪いか分からないまま結果全体のエラーとして出る。
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = duplicate.UserFriendlyMessage,
                            Data = card.CardIdm
                        });
                        continue;
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
                    TryRollbackImportTransaction(scope);
                    importedCount = 0;
                }
            }
            catch (SQLiteException ex)
            {
                // ログはロールバックより先に書く（TryRollbackImportTransaction の remarks 参照）
                // Issue #1282: SQLiteException は DatabaseException へラップして詳細を保持
                _logger?.LogError(ex,
                    "カードCSVインポートのトランザクション中に SQLite エラーが発生しロールバック");
                TryRollbackImportTransaction(scope);
                throw MarkLogged(DatabaseException.QueryFailed("CSV import transaction", ex));
            }
            catch (Exception ex)
            {
                // Issue #1282: 想定外の例外も握りつぶさずログに記録してから再スロー
                _logger?.LogError(ex,
                    "カードCSVインポートのトランザクション中に想定外の例外が発生しロールバック");
                TryRollbackImportTransaction(scope);
                MarkLogged(ex);
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
                errors,
                "カードCSVの取り込み内容の確認").ConfigureAwait(false);
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

            var lines = await ReadCsvFileAsync(filePath, _logger).ConfigureAwait(false);
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
                // Issue #1370: プレビューでも備考差分検出のため note を読み取る
                // Issue #1808: インポート本体と同じ ReadTextField（先頭 ' の除去）で読む
                var cardType = ReadTextField(fields, 1);
                var cardNumber = ReadTextField(fields, 2);
                var note = ReadTextField(fields, 3);

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
                        DetectCardChanges(existingCard, cardType, cardNumber, note, changes);
                        changes.Insert(0, new FieldChange
                        {
                            FieldName = "状態",
                            OldValue = "削除済み",
                            NewValue = "有効"
                        });
                    }
                    else
                    {
                        // Issue #1376: skipExisting の有無に関わらず全項目比較で差分を検出。
                        // 差分あり → Update、差分なしかつ skipExisting=true → Skip、
                        // 差分なしかつ skipExisting=false → Update（no-op 更新）
                        DetectCardChanges(existingCard, cardType, cardNumber, note, changes);
                        if (changes.Count == 0 && skipExisting)
                        {
                            action = ImportAction.Skip;
                            skipCount++;
                        }
                        else
                        {
                            action = ImportAction.Update;
                            updateCount++;
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
        /// <param name="newNote">新しい備考</param>
        /// <param name="changes">変更点リスト（検出結果が追加される）</param>
        private static void DetectCardChanges(
            IcCard existingCard,
            string newCardType,
            string newCardNumber,
            string newNote,
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

            // Issue #1370: 備考の差分検出
            // インポート本体と同じ正規化（空白のみは null 扱い）で比較することで、
            // Preview の判定と実インポートの結果を一致させる
            AddOptionalTextChangeIfDiffers("備考", existingCard.Note, newNote, changes);
        }
    }
}
