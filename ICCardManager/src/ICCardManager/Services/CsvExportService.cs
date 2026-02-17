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

                // Issue #592: カード種別・管理番号順のソートキーを作成（同一カードの履歴をまとめる）
                var cardSortKeyMap = allCards.ToDictionary(
                    c => c.CardIdm,
                    c => $"{c.CardType ?? ""}\t{c.CardNumber ?? ""}");

                var lines = new List<string>
                {
                    // ヘッダー行（Issue #265: 管理番号列追加、Issue #266: 日付→日時、Issue #342: ID列追加）
                    "ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考"
                };

                // Issue #592: 同一カードの履歴をまとめて出力
                // カード種別・管理番号順でグループ化し、各カード内は日付順・ID順を維持
                foreach (var ledger in ledgers
                    .OrderBy(l => cardSortKeyMap.TryGetValue(l.CardIdm, out var key) ? key : l.CardIdm)
                    .ThenBy(l => l.Date)
                    .ThenBy(l => l.Id))
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
        /// 利用履歴詳細をCSVエクスポート（期間指定）
        /// </summary>
        /// <remarks>
        /// Issue #751対応: ledger_detailテーブルのデータをCSVに出力する。
        /// カードIDm・管理番号はledger→ic_cardのJOINで参照用に付与する。
        /// </remarks>
        public async Task<CsvExportResult> ExportLedgerDetailsAsync(
            string filePath,
            DateTime startDate,
            DateTime endDate)
        {
            try
            {
                // 期間内の全詳細を取得
                var details = await _ledgerRepository.GetAllDetailsInDateRangeAsync(startDate, endDate);

                // ledger_id→カードIDmマッピング用に期間内のledgerを取得
                var ledgers = await _ledgerRepository.GetByDateRangeAsync(null, startDate, endDate);
                var ledgerCardMap = ledgers.ToDictionary(l => l.Id, l => l.CardIdm);

                // カードIDmから管理番号へのマッピング
                var allCards = await _cardRepository.GetAllIncludingDeletedAsync();
                var cardNumberMap = allCards.ToDictionary(c => c.CardIdm, c => c.CardNumber ?? "");

                // ソートキー：カード種別・管理番号順
                var cardSortKeyMap = allCards.ToDictionary(
                    c => c.CardIdm,
                    c => $"{c.CardType ?? ""}\t{c.CardNumber ?? ""}");

                var lines = new List<string>
                {
                    // ヘッダー行
                    "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID"
                };

                // カード種別・管理番号 → 日付 → ledger_id → rowid 順でソート
                foreach (var detail in details
                    .OrderBy(d =>
                    {
                        if (ledgerCardMap.TryGetValue(d.LedgerId, out var idm) &&
                            cardSortKeyMap.TryGetValue(idm, out var key))
                            return key;
                        return "";
                    })
                    .ThenBy(d => d.UseDate)
                    .ThenBy(d => d.LedgerId)
                    .ThenBy(d => d.SequenceNumber))
                {
                    // 参照用のカードIDmと管理番号を取得
                    var cardIdm = ledgerCardMap.TryGetValue(detail.LedgerId, out var idmVal) ? idmVal : "";
                    var cardNumber = !string.IsNullOrEmpty(cardIdm) && cardNumberMap.TryGetValue(cardIdm, out var num) ? num : "";

                    lines.Add(string.Join(",",
                        detail.LedgerId.ToString(),
                        detail.UseDate.HasValue ? detail.UseDate.Value.ToString("yyyy-MM-dd HH:mm:ss") : "",
                        EscapeCsvField(cardIdm),
                        EscapeCsvField(cardNumber),
                        EscapeCsvField(detail.EntryStation ?? ""),
                        EscapeCsvField(detail.ExitStation ?? ""),
                        EscapeCsvField(detail.BusStops ?? ""),
                        detail.Amount.HasValue ? detail.Amount.Value.ToString() : "",
                        detail.Balance.HasValue ? detail.Balance.Value.ToString() : "",
                        detail.IsCharge ? "1" : "0",
                        detail.IsPointRedemption ? "1" : "0",
                        detail.IsBus ? "1" : "0",
                        detail.GroupId.HasValue ? detail.GroupId.Value.ToString() : ""
                    ));
                }

                await Task.Run(() => File.WriteAllLines(filePath, lines, CsvEncoding));

                return new CsvExportResult
                {
                    Success = true,
                    ExportedCount = details.Count,
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
        /// 履歴インポート用のCSVテンプレートを出力（Issue #510）
        /// </summary>
        /// <remarks>
        /// 年度途中導入時に、紙の出納簿からデータを入力するためのテンプレート。
        /// ヘッダー行のみを出力し、カード情報を含めることで入力を容易にする。
        /// </remarks>
        /// <param name="filePath">出力先ファイルパス</param>
        /// <param name="cardIdm">対象カードのIDm</param>
        /// <param name="cardNumber">対象カードの管理番号</param>
        /// <returns>エクスポート結果</returns>
        public async Task<CsvExportResult> ExportLedgerTemplateAsync(
            string filePath,
            string cardIdm,
            string cardNumber)
        {
            try
            {
                var lines = new List<string>
                {
                    // ヘッダー行（インポート形式と同じ）
                    "ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考",
                    // サンプル行（コメントアウト形式で記載）
                    $"# このテンプレートを使って履歴データを入力してください",
                    $"# カードIDm: {cardIdm}",
                    $"# 管理番号: {cardNumber}",
                    $"#",
                    $"# 入力例（先頭の#を削除して使用）:",
                    $"#,2024-04-01 09:00:00,{cardIdm},{cardNumber},鉄道（博多駅～天神駅）,,220,4780,山田太郎,",
                    $"#,2024-04-02 10:30:00,{cardIdm},{cardNumber},役務費によりチャージ,5000,,9780,,",
                    $"#",
                    $"# 注意:",
                    $"# - ID列は空欄にしてください（新規追加の場合）",
                    $"# - 日時は YYYY-MM-DD HH:MM:SS 形式で入力してください",
                    $"# - 受入金額はチャージ時のみ、払出金額は利用時のみ入力してください",
                    $"# - 残額は前の行の残額 + 受入金額 - 払出金額 と一致するようにしてください"
                };

                // .NET Framework 4.8ではFile.WriteAllLinesAsyncがないためTask.Runで同期版を使用
                await Task.Run(() => File.WriteAllLines(filePath, lines, CsvEncoding));

                return new CsvExportResult
                {
                    Success = true,
                    ExportedCount = 0,  // テンプレートなのでデータ件数は0
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
