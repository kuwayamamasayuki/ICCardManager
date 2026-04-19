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
using ICCardManager.Services.Import.Parsers;
using System.Data.SQLite;

namespace ICCardManager.Services
{
    public partial class CsvImportService
    {
        // === 利用履歴CSVインポート・プレビュー ===

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
                    var parsed = LedgerCsvRowParser.TryParseRow(
                        fields, lineNumber, line, hasIdColumn, minColumns,
                        existingCardIdms, targetCardIdm, errors);
                    if (parsed == null)
                    {
                        continue;
                    }

                    // 既存レコードの確認（IDがある場合）
                    var isUpdate = false;
                    Ledger existingLedgerForUpdate = null;
                    if (parsed.LedgerId.HasValue)
                    {
                        var existingLedger = await _ledgerRepository.GetByIdAsync(parsed.LedgerId.Value);
                        if (existingLedger != null)
                        {
                            var hasChanges = existingLedger.Summary != parsed.Summary ||
                                            (existingLedger.StaffName ?? "") != parsed.StaffName ||
                                            (existingLedger.Note ?? "") != parsed.Note ||
                                            existingLedger.Income != parsed.Income ||
                                            existingLedger.Expense != parsed.Expense ||
                                            existingLedger.Balance != parsed.Balance ||
                                            existingLedger.Date != parsed.Date;
                            if (hasChanges)
                            {
                                isUpdate = true;
                                existingLedgerForUpdate = existingLedger;
                            }
                            else if (skipExisting)
                            {
                                var skippedLedger = new Ledger
                                {
                                    Id = parsed.LedgerId.Value,
                                    CardIdm = parsed.CardIdm,
                                    Date = parsed.Date,
                                    Summary = parsed.Summary,
                                    Income = parsed.Income,
                                    Expense = parsed.Expense,
                                    Balance = parsed.Balance
                                };
                                allRecordsForValidation.Add((lineNumber, skippedLedger, false));
                                skippedCount++;
                                continue;
                            }
                            else
                            {
                                isUpdate = true;
                                existingLedgerForUpdate = existingLedger;
                            }
                        }
                    }

                    var ledger = new Ledger
                    {
                        Id = parsed.LedgerId ?? 0,
                        CardIdm = parsed.CardIdm,
                        Date = parsed.Date,
                        Summary = parsed.Summary,
                        Income = parsed.Income,
                        Expense = parsed.Expense,
                        Balance = parsed.Balance,
                        StaffName = string.IsNullOrWhiteSpace(parsed.StaffName) ? null : parsed.StaffName,
                        Note = string.IsNullOrWhiteSpace(parsed.Note) ? null : parsed.Note,
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
                    var parsed = LedgerCsvRowParser.TryParseRow(
                        fields, lineNumber, line, hasIdColumn, minColumns,
                        existingCardIdms, targetCardIdm, errors);
                    if (parsed == null)
                    {
                        continue;
                    }

                    cardIdmsInFile.Add(parsed.CardIdm);
                    validatedRecords.Add((
                        parsed.LineNumber, parsed.LedgerId, parsed.CardIdm,
                        parsed.Date, parsed.Summary, parsed.Income, parsed.Expense,
                        parsed.Balance, parsed.StaffName, parsed.Note));
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
                                // Issue #969: スキップ時もデータ内容を表示
                                changes = CreateLedgerDisplayChanges(date, summary, income, expense, balance, staffName, note);
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
                            // Issue #969: 追加時もデータ内容を表示
                            changes = CreateLedgerDisplayChanges(date, summary, income, expense, balance, staffName, note);
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
                            // Issue #969: スキップ時もデータ内容を表示
                            changes = CreateLedgerDisplayChanges(date, summary, income, expense, balance, staffName, note);
                        }
                        else
                        {
                            action = ImportAction.Insert;
                            newCount++;
                            // Issue #969: 追加時もデータ内容を表示
                            changes = CreateLedgerDisplayChanges(date, summary, income, expense, balance, staffName, note);
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

    }
}
