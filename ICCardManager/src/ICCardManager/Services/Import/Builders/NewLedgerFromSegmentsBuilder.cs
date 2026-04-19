using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;

namespace ICCardManager.Services.Import.Builders
{
    /// <summary>
    /// 利用履歴 ID 空欄の詳細行から、segment 分割を伴って新規 Ledger を自動生成する。
    /// Detail CSV インポートの一機能として使用（Issue #906, #918, #1053）。
    /// Issue #1284 で CsvImportService.Detail.cs から抽出。
    /// </summary>
    internal class NewLedgerFromSegmentsBuilder
    {
        private readonly ILedgerRepository _ledgerRepository;

        public NewLedgerFromSegmentsBuilder(ILedgerRepository ledgerRepository)
        {
            _ledgerRepository = ledgerRepository;
        }

        /// <summary>
        /// 1 カード・1 日分の詳細リストから、チャージ境界で segment 分割し、
        /// 各 segment ごとに Ledger を作成して detail を挿入する。
        /// </summary>
        /// <param name="cardIdm">カード IDm</param>
        /// <param name="groupDate">グループキーの日付（DateTime.MinValue なら detail.UseDate から推定）</param>
        /// <param name="detailRows">(line_number, LedgerDetail) のリスト</param>
        /// <param name="errors">エラー追加先</param>
        /// <returns>挿入成功した detail 件数（segment 単位で失敗した場合は 0）</returns>
        public async Task<int> BuildAndInsertAsync(
            string cardIdm,
            DateTime groupDate,
            List<(int LineNumber, LedgerDetail Detail)> detailRows,
            List<CsvImportError> errors)
        {
            if (detailRows.Count == 0)
            {
                return 0;
            }

            var firstLineNumber = detailRows.First().LineNumber;
            var detailList = detailRows.Select(r => r.Detail).ToList();

            try
            {
                // チャージ/ポイント還元の位置で利用グループを分割
                var segments = LendingHistoryAnalyzer.SplitAtChargeBoundaries(detailList);

                // セグメントがない場合（空リスト対策）は元のリストで 1 segment として扱う
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

                    var summary = summaryGenerator.Generate(segmentDetails);
                    if (string.IsNullOrEmpty(summary))
                    {
                        summary = "CSVインポート";
                    }

                    var (income, expense, balance) = LedgerSplitService.CalculateGroupFinancials(segmentDetails);

                    var date = groupDate;
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

                    var newLedger = new Ledger
                    {
                        CardIdm = cardIdm,
                        Date = date,
                        Summary = summary,
                        Income = income,
                        Expense = expense,
                        Balance = balance
                    };

                    var newLedgerId = await _ledgerRepository.InsertAsync(newLedger).ConfigureAwait(false);
                    var success = await _ledgerRepository.InsertDetailsAsync(newLedgerId, segmentDetails).ConfigureAwait(false);

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

                return segmentFailed ? 0 : detailRows.Count;
            }
            catch (Exception ex)
            {
                errors.Add(new CsvImportError
                {
                    LineNumber = firstLineNumber,
                    Message = $"カード {cardIdm} の利用履歴自動作成中にエラーが発生しました: {ex.Message}",
                    Data = cardIdm
                });
                return 0;
            }
        }
    }
}
