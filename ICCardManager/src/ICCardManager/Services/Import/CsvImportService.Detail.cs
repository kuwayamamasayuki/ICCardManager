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
        // === 利用履歴詳細CSVインポート・プレビュー ===

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
            // Issue #1053: チャージ/ポイント還元境界で分割してセグメントごとに表示
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

                // チャージ/ポイント還元境界で分割
                var detailList = detailRows.Select(x => x.Detail).ToList();
                var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(detailList);

                if (segments.Count <= 1)
                {
                    // 分割不要：従来通り1アイテムとして表示
                    // Issue #938: 追加する内容の詳細を表示
                    var insertChanges = CreateInsertDetailChanges(detailList);

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
                else
                {
                    // 分割あり：セグメントごとにプレビューアイテムを生成
                    foreach (var segment in segments)
                    {
                        var segmentChanges = CreateInsertDetailChanges(segment.Details);
                        var segmentType = segment.IsCharge ? "チャージ"
                            : segment.IsPointRedemption ? "ポイント還元"
                            : "利用";

                        items.Add(new CsvImportPreviewItem
                        {
                            LineNumber = detailRows.First().LineNumber,
                            Idm = "(自動付与)",
                            Name = cardDisplayName,
                            AdditionalInfo = $"{segmentType} {segment.Details.Count}件{dateStr}",
                            Action = ImportAction.Insert,
                            Changes = segmentChanges
                        });
                        newCount++;
                    }
                }
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
                    // Issue #969: スキップ時も既存データの内容を表示
                    changes = CreateSkipDetailChanges(existingDetails);
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
            // Issue #1053: チャージ/ポイント還元境界で分割し、セグメントごとにLedgerを作成
            foreach (var kvp in newDetailsByCardIdmAndDate)
            {
                var cardIdm = kvp.Key.CardIdm;
                var detailRows = kvp.Value;
                var firstLineNumber = detailRows.First().LineNumber;
                var detailList = detailRows.Select(r => r.Detail).ToList();

                try
                {
                    // チャージ/ポイント還元の位置で利用グループを分割
                    var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(detailList);

                    // セグメントがない場合（空リスト対策）は元のリストで1セグメントとして扱う
                    if (segments.Count == 0)
                    {
                        segments = new List<LendingHistoryAnalyzer.DailySegment>
                        {
                            new LendingHistoryAnalyzer.DailySegment
                            {
                                IsCharge = false,
                                IsPointRedemption = false,
                                Details = detailList
                            }
                        };
                    }

                    var summaryGenerator = new SummaryGenerator();
                    var segmentFailed = false;

                    foreach (var segment in segments)
                    {
                        var segmentDetails = segment.Details;

                        // SummaryGeneratorで摘要を自動生成
                        var summary = summaryGenerator.Generate(segmentDetails);
                        if (string.IsNullOrEmpty(summary))
                        {
                            summary = "CSVインポート";
                        }

                        // LedgerSplitServiceと同じロジックで収支・残高を計算
                        var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(segmentDetails);

                        // 日付はグループのキーから取得、DateTime.MinValueの場合は最も古い利用日時、なければ現在日時
                        var date = kvp.Key.Date;
                        if (date == DateTime.MinValue)
                        {
                            date = segmentDetails
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
                        var success = await _ledgerRepository.InsertDetailsAsync(newLedgerId, segmentDetails);

                        if (!success)
                        {
                            segmentFailed = true;
                            errors.Add(new CsvImportError
                            {
                                LineNumber = firstLineNumber,
                                Message = $"カード {cardIdm} の新規詳細の挿入に失敗しました",
                                Data = cardIdm
                            });
                        }
                    }

                    if (!segmentFailed)
                    {
                        importedCount += detailRows.Count;
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
        /// 利用履歴の追加・スキップ時に表示する内容を生成する。
        /// Issue #969対応。
        /// </summary>
        internal static List<FieldChange> CreateLedgerDisplayChanges(
            DateTime date, string summary, int income, int expense, int balance, string staffName, string note)
        {
            var changes = new List<FieldChange>
            {
                new FieldChange { FieldName = "日付", NewValue = date.ToString("yyyy-MM-dd HH:mm:ss"), IsDisplayOnly = true },
                new FieldChange { FieldName = "摘要", NewValue = summary, IsDisplayOnly = true }
            };
            if (income > 0)
                changes.Add(new FieldChange { FieldName = "受入金額", NewValue = $"{income:#,0}円", IsDisplayOnly = true });
            if (expense > 0)
                changes.Add(new FieldChange { FieldName = "払出金額", NewValue = $"{expense:#,0}円", IsDisplayOnly = true });
            changes.Add(new FieldChange { FieldName = "残高", NewValue = $"{balance:#,0}円", IsDisplayOnly = true });
            if (!string.IsNullOrEmpty(staffName))
                changes.Add(new FieldChange { FieldName = "職員名", NewValue = staffName, IsDisplayOnly = true });
            if (!string.IsNullOrEmpty(note))
                changes.Add(new FieldChange { FieldName = "備考", NewValue = note, IsDisplayOnly = true });
            return changes;
        }

        /// <summary>
        /// 利用履歴詳細のスキップ時に既存データの内容を表示する。
        /// Issue #969対応。
        /// </summary>
        internal static List<FieldChange> CreateSkipDetailChanges(List<LedgerDetail> existingDetails)
        {
            var changes = new List<FieldChange>();
            for (var i = 0; i < existingDetails.Count; i++)
            {
                var detail = existingDetails[i];
                var description = FormatDetailDescription(detail);
                changes.Add(new FieldChange
                {
                    FieldName = $"[{i + 1}行目]",
                    NewValue = description,
                    IsDisplayOnly = true
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
    }
}
