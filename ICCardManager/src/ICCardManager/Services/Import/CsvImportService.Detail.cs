using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using ICCardManager.Services.Import.Builders;
using ICCardManager.Services.Import.Parsers;
using Microsoft.Extensions.Logging;

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
                errors,
                "利用明細CSVの取り込み内容の確認").ConfigureAwait(false);
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

            var lines = await ReadCsvFileAsync(filePath, _logger).ConfigureAwait(false);
            if (lines.Count < 2)
            {
                return new CsvImportPreviewResult
                {
                    IsValid = false,
                    ErrorMessage = "CSVファイルにデータがありません（ヘッダー行のみ）"
                };
            }

            // Issue #937: カード名表示のためにカード情報を取得
            var allCards = await _cardRepository.GetAllIncludingDeletedAsync().ConfigureAwait(false);
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

                var detail = LedgerDetailCsvRowParser.ParseFields(fields, lineNumber, line, errors);
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
                    var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true).ConfigureAwait(false);
                    if (card == null)
                    {
                        // 生の IDm はここで「マスク済みの値」「形式が妥当か」「文字数」へ畳み、
                        // 以降（文言の組み立て）へは渡さない（Issue #1986）。
                        var idmWellFormed = IsIdmWellFormed(cardIdm);
                        var maskedIdm = IdmMasker.Mask(cardIdm);
                        var idmLength = cardIdm?.Length ?? 0;

                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            // Issue #1986: IDm は本システム唯一の認証要素であり、エラー文言は
                            // 画面に出て職員の目に触れるためマスクを通す（#1852）。
                            // Data は突き合わせ用の内部キーで画面にもログにも出ないため生のまま。
                            // ファクトリへ生の IDm を渡さない（構造的に露出できなくする）。
                            Message = idmWellFormed
                                ? BuildUnregisteredCardMessage(maskedIdm)
                                : BuildMalformedIdmMessage(idmLength),
                            Data = cardIdm
                        });
                        continue;
                    }

                    // Issue #918: 日付でもグループ化（日付がない場合はDateTime.MinValueをキーにする）
                    var dateKey = detail.UseDate?.Date ?? DateTime.MinValue;
                    var groupKey = (cardIdm, dateKey);
                    if (!newDetailsByCardIdmAndDate.TryGetValue(groupKey, out var dateGroup))
                    {
                        dateGroup = new List<(int, LedgerDetail)>();
                        newDetailsByCardIdmAndDate[groupKey] = dateGroup;
                    }
                    dateGroup.Add((lineNumber, detail));
                    continue;
                }

                // 既存ledger_idの存在チェック
                if (!existingDetailsByLedgerId.ContainsKey(detail.LedgerId))
                {
                    var ledger = await _ledgerRepository.GetByIdAsync(detail.LedgerId).ConfigureAwait(false);
                    if (ledger == null)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"利用履歴ID {detail.LedgerId} が存在しません",
                            Data = detail.LedgerId.ToString(CultureInfo.InvariantCulture)
                        });
                        continue;
                    }
                    existingDetailsByLedgerId[detail.LedgerId] = ledger.Details ?? new List<LedgerDetail>();
                    ledgerCardIdmMap[detail.LedgerId] = ledger.CardIdm ?? "";
                }

                if (!detailsByLedgerId.TryGetValue(detail.LedgerId, out var ledgerGroup))
                {
                    ledgerGroup = new List<(int, LedgerDetail)>();
                    detailsByLedgerId[detail.LedgerId] = ledgerGroup;
                }
                ledgerGroup.Add((lineNumber, detail));
            }

            // Issue #906: 新規詳細（利用履歴ID空欄）のプレビューアイテム生成
            // Issue #918: カードIDm＋日付ごとにグループ化して表示
            // Issue #1053: チャージ/ポイント還元境界で分割してセグメントごとに表示
            foreach (var kvp in newDetailsByCardIdmAndDate.OrderBy(x => x.Key.CardIdm).ThenBy(x => x.Key.Date))
            {
                var cardIdm = kvp.Key.CardIdm;
                var date = kvp.Key.Date;
                var detailRows = kvp.Value;
                var dateStr = date == DateTime.MinValue ? "" : $" ({date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})";

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
                    // Issue #1379: CSV 行数ベースでカウント（インポート結果と一致させる）
                    newCount += detailList.Count;
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
                        // Issue #1379: CSV 行数ベースでカウント（インポート結果と一致させる）
                        newCount += segment.Details.Count;
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
                    // Issue #1379: CSV 行数ベースでカウント（インポート結果と一致させる）
                    updateCount += detailRows.Count;
                }
                else
                {
                    action = ImportAction.Skip;
                    // Issue #1379: CSV 行数ベースでカウント（インポート結果と一致させる）
                    skipCount += detailRows.Count;
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
                    Idm = ledgerId.ToString(CultureInfo.InvariantCulture),
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
                errors,
                "利用明細CSVの取り込み").ConfigureAwait(false);
        }

        /// <summary>
        /// 利用履歴詳細CSVインポートの内部処理
        /// </summary>
        private async Task<CsvImportResult> ImportLedgerDetailsInternalAsync(
            string filePath,
            List<CsvImportError> errors)
        {
            var importedCount = 0;

            var lines = await ReadCsvFileAsync(filePath, _logger).ConfigureAwait(false);
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

                var detail = LedgerDetailCsvRowParser.ParseFields(fields, lineNumber, line, errors);
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
                    var card = await _cardRepository.GetByIdmAsync(cardIdm, includeDeleted: true).ConfigureAwait(false);
                    if (card == null)
                    {
                        // 生の IDm はここで「マスク済みの値」「形式が妥当か」「文字数」へ畳み、
                        // 以降（文言の組み立て）へは渡さない（Issue #1986）。
                        var idmWellFormed = IsIdmWellFormed(cardIdm);
                        var maskedIdm = IdmMasker.Mask(cardIdm);
                        var idmLength = cardIdm?.Length ?? 0;

                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            // Issue #1986: IDm は本システム唯一の認証要素であり、エラー文言は
                            // 画面に出て職員の目に触れるためマスクを通す（#1852）。
                            // Data は突き合わせ用の内部キーで画面にもログにも出ないため生のまま。
                            // ファクトリへ生の IDm を渡さない（構造的に露出できなくする）。
                            Message = idmWellFormed
                                ? BuildUnregisteredCardMessage(maskedIdm)
                                : BuildMalformedIdmMessage(idmLength),
                            Data = cardIdm
                        });
                        continue;
                    }

                    // Issue #918: 日付でもグループ化（日付がない場合はDateTime.MinValueをキーにする）
                    var dateKey = detail.UseDate?.Date ?? DateTime.MinValue;
                    var groupKey = (cardIdm, dateKey);
                    if (!newDetailsByCardIdmAndDate.TryGetValue(groupKey, out var dateGroup))
                    {
                        dateGroup = new List<(int, LedgerDetail)>();
                        newDetailsByCardIdmAndDate[groupKey] = dateGroup;
                    }
                    dateGroup.Add((lineNumber, detail));
                    continue;
                }

                // 既存ledger_idの存在チェック
                if (!existingDetailsByLedgerId.ContainsKey(detail.LedgerId))
                {
                    var ledger = await _ledgerRepository.GetByIdAsync(detail.LedgerId).ConfigureAwait(false);
                    if (ledger == null)
                    {
                        errors.Add(new CsvImportError
                        {
                            LineNumber = lineNumber,
                            Message = $"利用履歴ID {detail.LedgerId} が存在しません",
                            Data = detail.LedgerId.ToString(CultureInfo.InvariantCulture)
                        });
                        continue;
                    }
                    existingDetailsByLedgerId[detail.LedgerId] = ledger.Details ?? new List<LedgerDetail>();
                }

                if (!detailsByLedgerId.TryGetValue(detail.LedgerId, out var ledgerGroup))
                {
                    ledgerGroup = new List<(int, LedgerDetail)>();
                    detailsByLedgerId[detail.LedgerId] = ledgerGroup;
                }
                ledgerGroup.Add((lineNumber, detail));
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

            // Issue #906: 新規詳細（利用履歴ID空欄）の Ledger 自動作成とインポート
            // Issue #918: カードIDm＋日付ごとにグループ化して個別の Ledger を作成
            // Issue #1053: チャージ/ポイント還元境界で分割し、セグメントごとに Ledger を作成
            // Issue #1284: NewLedgerFromSegmentsBuilder に責務分離
            // Issue #1955: 摘要の再生成は DB に保存された部署種別に従う（新規作成・既存更新で同じ
            // インスタンスを使い、「経路によって設定が効いたり効かなかったり」する形を残さない）
            var summaryGenerator = await CreateSummaryGeneratorAsync().ConfigureAwait(false);
            // Issue #2176: 新規作成もグループ（カード IDm＋日付）ごとに 1 トランザクションで確定する
            var newLedgerBuilder = new NewLedgerFromSegmentsBuilder(_ledgerRepository, _dbContext, summaryGenerator, _logger);
            foreach (var kvp in newDetailsByCardIdmAndDate)
            {
                importedCount += await newLedgerBuilder.BuildAndInsertAsync(
                    kvp.Key.CardIdm,
                    kvp.Key.Date,
                    kvp.Value,
                    errors).ConfigureAwait(false);
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

                // Issue #2155: 1 件の利用履歴について「明細の置換」と「親の摘要・金額の更新」を
                // 1 つのトランザクションで確定させる。旧実装は ReplaceDetailsAsync が自前 tx で確定し、
                // 親の UpdateAsync が別 tx だったため、後者が失敗すると「明細は差し替わったが、
                // 親の摘要・金額は旧値」という食い違いが 6 年保存の台帳に残った。しかも再インポートは
                // 明細が一致するため「変更なし」でスキップされ、取り込み直しでは直せなかった。
                //
                // 単位は利用履歴 ID ごと（ループ全体ではない）。失敗した履歴だけを行番号付きの
                // エラーとして報告し、他の履歴は取り込む従来の報告形を保つため。新規作成側
                // （NewLedgerFromSegmentsBuilder）もグループ単位で確定するので、ループ全体を
                // 1 tx にしても取り込み全体の「全か無か」にはならない。
                //
                // tx は scope.Transaction を明示的に引き渡す（db-write-conventions.md の「①」）。
                // BeginTransactionAsync は SemaphoreSlim(1,1) を取るため、リポジトリ側で入れ子に
                // 開くと自己デッドロックする（Issue #1575）。暗黙参加（②）は backstop（Issue #1737）。
                TransactionScope scope = null;
                try
                {
                    scope = await _dbContext.BeginTransactionAsync().ConfigureAwait(false);
                    var failureMessage = await ReplaceDetailsAndUpdateParentAsync(
                        ledgerId, newDetails, summaryGenerator, scope.Transaction).ConfigureAwait(false);

                    if (failureMessage == null)
                    {
                        scope.Commit();
                        importedCount += detailRows.Count;
                    }
                    else
                    {
                        // 業務的な失敗（競合・0 行）での巻き戻し。tx は有効なので素の Rollback でも
                        // 失敗しないが、手段は 1 つに寄せる（Issue #1831）
                        TryRollbackImportTransaction(scope);
                        errors.Add(new CsvImportError
                        {
                            LineNumber = firstLineNumber,
                            Message = failureMessage,
                            Data = ledgerId.ToString(CultureInfo.InvariantCulture)
                        });
                    }
                }
                catch (Exception ex)
                {
                    // 生の ex.Message は UI へ出さずログへ逃がす（Issue #1614）。
                    // ログは巻き戻しより先に書く。ROLLBACK 自体が失敗すると二次例外が
                    // 本来の失敗要因を置き換えて抜けるため（Issue #1745）。
                    // トランザクションの開始自体が失敗した（scope == null）ときは巻き戻す対象が無いので、
                    // ログでも「巻き戻した」と述べない。
                    if (scope != null)
                    {
                        _logger?.LogError(ex,
                            "Failed to import ledger details for ledger {LedgerId} (line {LineNumber}); rolling back the transaction",
                            ledgerId, firstLineNumber);
                        TryRollbackImportTransaction(scope);
                    }
                    else
                    {
                        _logger?.LogError(ex,
                            "Failed to begin the transaction to import ledger details for ledger {LedgerId} (line {LineNumber})",
                            ledgerId, firstLineNumber);
                    }
                    errors.Add(new CsvImportError
                    {
                        LineNumber = firstLineNumber,
                        Message = BuildDetailReplaceFailureMessage(ledgerId, ex),
                        Data = ledgerId.ToString(CultureInfo.InvariantCulture)
                    });
                }
                finally
                {
                    // 未コミットの tx は Dispose で必ず巻き戻る（TryRollbackImportTransaction の remarks）
                    scope?.Dispose();
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
        /// 1 件の利用履歴について、明細の置換と親 Ledger（摘要・金額）の更新を
        /// 呼び出し元のトランザクション上で行う（Issue #2155）。
        /// </summary>
        /// <returns>
        /// 成功なら <c>null</c>。業務的な失敗（親が見つからない・影響行数 0）なら利用者向けの文言。
        /// 失敗時の巻き戻しと、例外時の扱いは呼び出し元が持つ（commit/rollback には介入しない）。
        /// </returns>
        private async Task<string> ReplaceDetailsAndUpdateParentAsync(
            int ledgerId,
            List<LedgerDetail> newDetails,
            SummaryGenerator summaryGenerator,
            SQLiteTransaction transaction)
        {
            // 親を先に読む（#1760「読み取りが null なら、書き込みも行わない」）。
            // DbContext は接続を 1 本しか持たないため、この読み取りは同じ接続上の tx の内側で行われる。
            // Issue #1808: 他 PC や別操作で履歴が削除されていた競合は、エラーとして報告し
            // インポート件数に含めない。
            var ledger = await _ledgerRepository.GetByIdAsync(ledgerId).ConfigureAwait(false);
            if (ledger == null)
            {
                return BuildParentLedgerConflictMessage(ledgerId);
            }

            // Issue #1913: CSV の明細行は CsvExportService と同じ時系列昇順（古い→新しい）で
            // 並ぶ。ReplaceDetailsAsync は DELETE + INSERT で id を再採番するため、昇順のまま
            // 渡すと LedgerDetail.SequenceNumber の規約（FeliCa 互換で小さい id ＝ 新しい）が
            // 反転する。新しい順にしてから渡す（LedgerSplitService / LendingService と同じ）。
            // 摘要生成・金額再計算（下の Generate / CalculateGroupFinancials）は昇順のまま使う。
            var replaced = await _ledgerRepository.ReplaceDetailsAsync(
                ledgerId, newDetails.AsEnumerable().Reverse(), transaction).ConfigureAwait(false);
            if (!replaced)
            {
                return BuildDetailInsertShortfallMessage(ledgerId);
            }

            // Issue #918: 詳細置換後、親Ledgerの金額を再計算して更新
            var summary = summaryGenerator.Generate(newDetails);
            var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(newDetails);

            ledger.Summary = !string.IsNullOrEmpty(summary) ? summary : ledger.Summary;
            ledger.Income = income;
            ledger.Expense = expense;
            ledger.Balance = balance;

            // Issue #1753 / #1808: 0 行は「その id の行が無い」競合。戻り値を捨てない
            var parentUpdated = await _ledgerRepository.UpdateAsync(ledger, transaction).ConfigureAwait(false);
            return parentUpdated ? null : BuildParentLedgerConflictMessage(ledgerId);
        }

        /// <summary>
        /// 親 Ledger が見つからない／UPDATE が 0 行だったときのエラー文言を組み立てる（Issue #1808）。
        /// </summary>
        /// <remarks>
        /// <c>LedgerRepository.UpdateAsync</c> の WHERE は <c>id = @id</c> だけなので、0 行は
        /// 「その id の行が無い」ことに特定できる（Issue #1759「影響行数 0 は競合 — 原因を名指しできる」）。
        /// ただし共有モードでもローカルモードでも起こり得るため、モード中立に「他のパソコンや別の操作」と
        /// 「可能性があります」で述べる。
        /// <para>
        /// Issue #2155: 明細の置換と親の更新は同じトランザクションで巻き戻るため、
        /// 「置き換えた明細も履歴と一緒に削除されています」という途中状態の説明は不要になった。
        /// </para>
        /// </remarks>
        private static string BuildParentLedgerConflictMessage(int ledgerId)
            => $"利用履歴ID {ledgerId} の明細を取り込めませんでした。" +
               "他のパソコンや別の操作でこの履歴が削除された可能性があります。" +
               "履歴画面でこの履歴の有無を確認し、必要な場合は、この利用履歴IDの行だけを残して利用履歴IDを空欄にした明細CSVを再度インポートし、新規の履歴として登録してください。";

        /// <summary>
        /// 明細の INSERT が 0 行だった（<c>ReplaceDetailsAsync</c> が <c>false</c>）ときのエラー文言。
        /// </summary>
        /// <remarks>
        /// 旧実装は「詳細の置換に失敗しました」の一文で、「なぜ」「どうすれば」を欠いていた
        /// （error-messages.md の 3 要素）。原因は特定できないが、トランザクションごと巻き戻すので
        /// 「何も変更されていない」ことは断定でき、取り込みのやり直しを案内できる。
        /// </remarks>
        private static string BuildDetailInsertShortfallMessage(int ledgerId)
            => $"利用履歴ID {ledgerId} の明細を置き換えられませんでした。" +
               "明細の一部を登録できなかったため、この履歴の明細・摘要・金額は変更されていません。" +
               // Issue #2176: CSV 全体を取り込み直すと、取り込めた利用履歴 ID 空欄の行（重複を判定しない。#1781）が二重になる
               "しばらく待ってから、この利用履歴IDの行だけを残したCSVで、もう一度インポートしてください。";

        /// <summary>カード IDm の桁数（16進16文字）。</summary>
        private const int IdmLength = 16;

        /// <summary>
        /// カード IDm が 16 進 <see cref="IdmLength"/> 文字の形式を満たすか。
        /// （メソッド名を <c>…Idm</c> で終わらせない — 静的検査が IDm を保持する識別子として拾うため）
        /// </summary>
        private static bool IsIdmWellFormed(string cardIdm)
            => cardIdm != null && cardIdm.Length == IdmLength && cardIdm.All(Uri.IsHexDigit);

        /// <summary>
        /// 明細 CSV のカード IDm が（形式は正しいが）登録されていないときのエラー文言。
        /// </summary>
        /// <param name="maskedIdm">
        /// <see cref="IdmMasker.Mask"/> を通した IDm。
        /// <b>生の IDm を受け取らない</b> — 引数の型で「マスクを通していない値は渡せない」ようにすると、
        /// 規約ではなく構造が露出を防ぐ（<c>development-conventions.md</c> #1883
        /// 「食い違った状態を表現できなくする」）。
        /// </param>
        private static string BuildUnregisteredCardMessage(string maskedIdm)
            => $"カードIDm {maskedIdm} が登録されていません。"
               + "この IDm のカードはカード管理に存在しません。"
               + "カード管理画面（F2）でカードを登録してから、もう一度取り込んでください。";

        /// <summary>
        /// 明細 CSV のカード IDm が 16 進 <see cref="IdmLength"/> 文字でないときのエラー文言。
        /// </summary>
        /// <param name="rawLength">CSV に書かれていた値の文字数（値そのものは渡さない）。</param>
        /// <remarks>
        /// <para>
        /// Issue #1986（コードレビューで検出）: <see cref="IdmMasker.Mask"/> は 16 文字未満の入力を
        /// <b>全部 <c>*</c> に置き換える</b>（短いクレデンシャルを部分露出させないため）。
        /// 壊れた IDm をそのまま「登録されていません」と案内すると、<b>職員には値が一切見えず、
        /// しかも案内（「カードを登録してください」）が実際の原因と食い違う</b>。
        /// </para>
        /// <para>
        /// この列は Excel で編集されることが多く、先頭の <c>0</c> が失われる・指数表記になる
        /// といった破損が「登録されていません」の最も多い実原因である。形式不正は<b>別の原因</b>として
        /// 名指しし、調査の手掛かりに文字数を添える（<c>error-messages.md</c>「実際の入力値を含める」。
        /// 値そのものは出さないので露出は増えない）。
        /// </para>
        /// </remarks>
        private static string BuildMalformedIdmMessage(int rawLength)
            => $"カードIDm の形式が正しくありません（{rawLength.ToString(CultureInfo.InvariantCulture)}文字）。"
               + $"カードIDm は 16進{IdmLength.ToString(CultureInfo.InvariantCulture)}文字である必要があります。"
               + "Excel で開くと先頭の 0 が失われたり指数表記になることがあります。"
               + "CSV のカードIDm 列を文字列として確認してから、もう一度取り込んでください。";

        /// <summary>
        /// 明細の置換／親 Ledger の更新が例外で中断したときのエラー文言を組み立てる（Issue #1614 / #1808）。
        /// </summary>
        /// <param name="ledgerId">対象の利用履歴ID</param>
        /// <param name="ex">捕捉した例外</param>
        /// <remarks>
        /// <c>SQLiteErrorCode.Constraint</c>（明細 INSERT の FOREIGN KEY 制約違反）は「親の履歴が消えた」
        /// 競合と同じ原因なので、<see cref="BuildParentLedgerConflictMessage"/> と同じ文言にする。
        /// ただし Issue #2155 以降は親を同じトランザクションの内側で先に読むため、この分岐へ来ることは
        /// ほとんど無い（<c>DbContext.BeginTransactionAsync</c> は引数なしの <c>BeginTransaction()</c> ＝
        /// System.Data.SQLite の既定で <c>BEGIN IMMEDIATE</c> を使い、書き込みロックを先に取るため、
        /// 読み取りから置換までの間に他 PC が削除することはできない）。防御として分岐は残す。
        /// 文言は「可能性があります」で述べており、他の原因の制約違反でも案内（履歴の有無を確認）は実行できる。
        /// それ以外は <see cref="ExceptionMessageFormatter.ToReason"/> へ寄せる。
        /// <para>
        /// Issue #2155: 明細の置換と親の更新は同じトランザクションで巻き戻るため、どの段階で
        /// 例外になっても<b>この履歴は何も変更されていない</b>。旧実装が持っていた「明細は置き換えたが
        /// 親の摘要・金額は旧値」という分岐（#1991 の <c>detailsReplaced</c>）と、その分岐に必要だった
        /// 「履歴画面で修正してください」という行動指示は、案内すべき状態ごと無くなった。
        /// COMMIT 自体が失敗した場合も、SQLite は自動で巻き戻し、未確定の tx は Dispose で破棄される。
        /// </para>
        /// <para>
        /// Issue #1991: <b>埋め込むのは「なぜ」だけにする</b>。<c>ToUserMessage</c> の完全な文を
        /// 埋め込むと「何が」が二重になり、行動指示も重複する。「どうすれば」はこのメソッドが持つ。
        /// </para>
        /// <para>
        /// この経路は直前で <c>_logger?.LogError</c> を出しているため、ログの併設は行わない（#1817）。
        /// </para>
        /// </remarks>
        private static string BuildDetailReplaceFailureMessage(int ledgerId, Exception ex)
        {
            if (ex is SQLiteException { ResultCode: SQLiteErrorCode.Constraint })
            {
                return BuildParentLedgerConflictMessage(ledgerId);
            }

            // Issue #1991: SQLite かどうかで分けない。ToReason は「なぜ」だけを返し、
            // SQLite の失敗も原因を名指しする（#1986 の分岐）。
            var reason = ExceptionMessageFormatter.ToReason(ex);

            return $"利用履歴ID {ledgerId} の明細を置き換えられませんでした。{reason}" +
                   "この履歴の明細・摘要・金額は変更されていません。" +
                   // Issue #2176: CSV 全体を取り込み直すと、取り込めた利用履歴 ID 空欄の行（#1781）が二重になる
                   "しばらく待ってから、この利用履歴IDの行だけを残したCSVで、もう一度インポートしてください。";
        }

        // 差分の値（OldValue / NewValue）はプレビューに表示するが、CSV のセルと同じ書式で見せるため
        // CSV の出力・取込と同じ InvariantCulture で整形する（Issue #2162。表示用の CurrentCulture ではない）。
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
                        OldValue = SqliteDateTimeFormat.ToText(existing.UseDate) ?? "(なし)",
                        NewValue = SqliteDateTimeFormat.ToText(imported.UseDate) ?? "(なし)"
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
                        OldValue = existing.Amount?.ToString(CultureInfo.InvariantCulture) ?? "(なし)",
                        NewValue = imported.Amount?.ToString(CultureInfo.InvariantCulture) ?? "(なし)"
                    });
                }

                if (existing.Balance != imported.Balance)
                {
                    changes.Add(new FieldChange
                    {
                        FieldName = $"{rowLabel} 残額",
                        OldValue = existing.Balance?.ToString(CultureInfo.InvariantCulture) ?? "(なし)",
                        NewValue = imported.Balance?.ToString(CultureInfo.InvariantCulture) ?? "(なし)"
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
                        OldValue = existing.GroupId?.ToString(CultureInfo.InvariantCulture) ?? "(なし)",
                        NewValue = imported.GroupId?.ToString(CultureInfo.InvariantCulture) ?? "(なし)"
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
            DateTime date, string summary, int income, int expense, int balance, string staffName, string note, int? companionCount = null)
        {
            var changes = new List<FieldChange>
            {
                new FieldChange { FieldName = "日付", NewValue = SqliteDateTimeFormat.ToText(date), IsDisplayOnly = true },
                new FieldChange { FieldName = "摘要", NewValue = summary, IsDisplayOnly = true }
            };
            if (income > 0)
            {
                changes.Add(new FieldChange { FieldName = "受入金額", NewValue = $"{income:#,0}円", IsDisplayOnly = true });
            }

            if (expense > 0)
            {
                changes.Add(new FieldChange { FieldName = "払出金額", NewValue = $"{expense:#,0}円", IsDisplayOnly = true });
            }

            changes.Add(new FieldChange { FieldName = "残高", NewValue = $"{balance:#,0}円", IsDisplayOnly = true });
            if (!string.IsNullOrEmpty(staffName))
            {
                changes.Add(new FieldChange { FieldName = "職員名", NewValue = staffName, IsDisplayOnly = true });
            }

            if (!string.IsNullOrEmpty(note))
            {
                changes.Add(new FieldChange { FieldName = "備考", NewValue = note, IsDisplayOnly = true });
            }

            if (companionCount.GetValueOrDefault() > 0)
            {
                changes.Add(new FieldChange { FieldName = "同行者数", NewValue = $"{companionCount.Value}名", IsDisplayOnly = true });
            }

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
                parts.Add(detail.UseDate.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
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
                // Issue #1818: バスラベルは組織設定（SummaryText.BusLabel）由来のため直書きしない
                var busStop = !string.IsNullOrEmpty(detail.BusStops)
                    ? SummaryGenerator.FormatBusSummary(detail.BusStops)
                    : SummaryGenerator.BusLabel;
                parts.Add(busStop);
            }
            else
            {
                // Issue #1735: 摘要生成側（SummaryGenerator）と同じプレースホルダを共有する
                var entry = !string.IsNullOrEmpty(detail.EntryStation) ? detail.EntryStation : SummaryGenerator.UnknownStationPlaceholder;
                var exit = !string.IsNullOrEmpty(detail.ExitStation) ? detail.ExitStation : SummaryGenerator.UnknownStationPlaceholder;
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
    }
}
