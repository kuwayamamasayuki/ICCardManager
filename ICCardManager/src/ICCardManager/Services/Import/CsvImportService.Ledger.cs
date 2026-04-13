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

        /// <summary>
        /// 出納記録の変更点を検出
        /// </summary>
        /// <param name="existingLedger">既存の出納記録</param>
        /// <param name="newDate">新しい日付</param>
        /// <param name="newSummary">新しい摘要</param>
        /// <param name="newIncome">新しい受入金額</param>
        /// <param name="newExpense">新しい払出金額</param>
        /// <param name="newBalance">新しい残額</param>
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

    }
}
