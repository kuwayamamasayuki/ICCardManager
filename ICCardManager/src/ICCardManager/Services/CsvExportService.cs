using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services
{
/// <summary>
    /// CSVエクスポート結果
    /// </summary>
    public class CsvExportResult
    {
        /// <summary>成功したか</summary>
        public bool Success { get; set; }

        /// <summary>エクスポートした件数</summary>
        public int ExportedCount { get; set; }

        /// <summary>出力ファイルパス</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>エラーメッセージ</summary>
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// CSVエクスポートサービス
    /// </summary>
    public class CsvExportService
    {
        private readonly ICardRepository _cardRepository;
        private readonly IStaffRepository _staffRepository;
        private readonly ILedgerRepository _ledgerRepository;

        // UTF-8 with BOM (Excel対応)
        private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

        public CsvExportService(
            ICardRepository cardRepository,
            IStaffRepository staffRepository,
            ILedgerRepository ledgerRepository)
        {
            _cardRepository = cardRepository;
            _staffRepository = staffRepository;
            _ledgerRepository = ledgerRepository;
        }

        /// <summary>
        /// カード一覧をCSVエクスポート
        /// </summary>
        public async Task<CsvExportResult> ExportCardsAsync(string filePath, bool includeDeleted = false)
        {
            try
            {
                var cards = includeDeleted
                    ? await _cardRepository.GetAllIncludingDeletedAsync()
                    : await _cardRepository.GetAllAsync();

                var lines = new List<string>
                {
                    // ヘッダー行
                    "カードIDm,カード種別,管理番号,備考,削除済み"
                };

                foreach (var card in cards.OrderBy(c => c.CardType).ThenBy(c => c.CardNumber))
                {
                    lines.Add(string.Join(",",
                        EscapeCsvField(card.CardIdm),
                        EscapeCsvField(card.CardType),
                        EscapeCsvField(card.CardNumber),
                        EscapeCsvField(card.Note ?? ""),
                        card.IsDeleted ? "1" : "0"
                    ));
                }

                // .NET Framework 4.8ではFile.WriteAllLinesAsyncがないためTask.Runで同期版を使用
                await Task.Run(() => File.WriteAllLines(filePath, lines, CsvEncoding));

                return new CsvExportResult
                {
                    Success = true,
                    ExportedCount = cards.Count(),
                    FilePath = filePath
                };
            }
            catch (Exception ex)
            {
                return new CsvExportResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    FilePath = filePath
                };
            }
        }

        /// <summary>
        /// 職員一覧をCSVエクスポート
        /// </summary>
        public async Task<CsvExportResult> ExportStaffAsync(string filePath, bool includeDeleted = false)
        {
            try
            {
                var staffList = includeDeleted
                    ? await _staffRepository.GetAllIncludingDeletedAsync()
                    : await _staffRepository.GetAllAsync();

                var lines = new List<string>
                {
                    // ヘッダー行
                    "職員IDm,氏名,職員番号,備考,削除済み"
                };

                foreach (var staff in staffList.OrderBy(s => s.Number).ThenBy(s => s.Name))
                {
                    lines.Add(string.Join(",",
                        EscapeCsvField(staff.StaffIdm),
                        EscapeCsvField(staff.Name),
                        EscapeCsvField(staff.Number ?? ""),
                        EscapeCsvField(staff.Note ?? ""),
                        staff.IsDeleted ? "1" : "0"
                    ));
                }

                // .NET Framework 4.8ではFile.WriteAllLinesAsyncがないためTask.Runで同期版を使用
                await Task.Run(() => File.WriteAllLines(filePath, lines, CsvEncoding));

                return new CsvExportResult
                {
                    Success = true,
                    ExportedCount = staffList.Count(),
                    FilePath = filePath
                };
            }
            catch (Exception ex)
            {
                return new CsvExportResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    FilePath = filePath
                };
            }
        }

        /// <summary>
        /// 履歴データをCSVエクスポート（期間指定）
        /// </summary>
        public async Task<CsvExportResult> ExportLedgersAsync(
            string filePath,
            DateTime startDate,
            DateTime endDate,
            string cardIdm = null)
        {
            try
            {
                // cardIdmがnullの場合は全カードの履歴を取得
                var ledgers = await _ledgerRepository.GetByDateRangeAsync(cardIdm, startDate, endDate);

                // カードIDmから管理番号へのマッピングを作成
                var allCards = await _cardRepository.GetAllIncludingDeletedAsync();
                var cardNumberMap = allCards.ToDictionary(c => c.CardIdm, c => c.CardNumber ?? "");

                var lines = new List<string>
                {
                    // ヘッダー行（Issue #265: 管理番号列追加、Issue #266: 日付→日時、Issue #342: ID列追加）
                    "ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考"
                };

                foreach (var ledger in ledgers.OrderBy(l => l.Date).ThenBy(l => l.Id))
                {
                    // 管理番号を取得（見つからない場合は空文字）
                    var cardNumber = cardNumberMap.TryGetValue(ledger.CardIdm, out var num) ? num : "";

                    lines.Add(string.Join(",",
                        ledger.Id.ToString(),
                        ledger.Date.ToString("yyyy-MM-dd HH:mm:ss"),
                        EscapeCsvField(ledger.CardIdm),
                        EscapeCsvField(cardNumber),
                        EscapeCsvField(ledger.Summary),
                        ledger.Income > 0 ? ledger.Income.ToString() : "",
                        ledger.Expense > 0 ? ledger.Expense.ToString() : "",
                        ledger.Balance.ToString(),
                        EscapeCsvField(ledger.StaffName ?? ""),
                        EscapeCsvField(ledger.Note ?? "")
                    ));
                }

                // .NET Framework 4.8ではFile.WriteAllLinesAsyncがないためTask.Runで同期版を使用
                await Task.Run(() => File.WriteAllLines(filePath, lines, CsvEncoding));

                return new CsvExportResult
                {
                    Success = true,
                    ExportedCount = ledgers.Count(),
                    FilePath = filePath
                };
            }
            catch (Exception ex)
            {
                return new CsvExportResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    FilePath = filePath
                };
            }
        }

        /// <summary>
        /// CSVフィールドをエスケープ
        /// </summary>
        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
            {
                return "";
            }

            // カンマ、ダブルクォート、改行が含まれる場合はダブルクォートで囲む
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            {
                // ダブルクォートは二重にエスケープ
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }

            return field;
        }
    }
}
