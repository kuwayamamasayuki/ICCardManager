using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ICCardManager.Common;
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

        /// <summary>
        /// 摘要生成に使う部署種別（チャージ摘要の「旅費／役務費」の切替）
        /// </summary>
        /// <remarks>
        /// 既定値を持たせない。<see cref="SummaryGenerator"/> の引数なしコンストラクタは
        /// <see cref="DepartmentType.MayorOffice"/> を既定に持つため、渡し忘れると企業会計部局の
        /// 組織で「役務費によりチャージ」が 6 年保存の <c>ledger.summary</c> と物品出納簿に入る。
        /// </remarks>
        private readonly DepartmentType _departmentType;

        public NewLedgerFromSegmentsBuilder(ILedgerRepository ledgerRepository, DepartmentType departmentType)
        {
            _ledgerRepository = ledgerRepository;
            _departmentType = departmentType;
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

                var summaryGenerator = new SummaryGenerator(_departmentType);
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
                    // Issue #1913: SplitAtChargeBoundaries は時系列昇順（古い→新しい）で返す。
                    // 挿入順がそのまま rowid の並びになるため、昇順のまま渡すと
                    // LedgerDetail.SequenceNumber の規約（FeliCa 互換で小さい rowid ＝ 新しい）が
                    // 反転する。新しい順にしてから渡す（LendingService の同型の挿入と同じ）。
                    // 摘要・金額（上の Generate / CalculateGroupFinancials）は昇順のまま使う。
                    var success = await _ledgerRepository.InsertDetailsAsync(
                        newLedgerId, segmentDetails.AsEnumerable().Reverse()).ConfigureAwait(false);

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
                // Issue #1817: UI 文言を差し替える前に、技術的詳細の出口を用意する
                // （このクラスは ILogger を持たないため既存のファイルログ機構を使う）。
                // NOTE: 下の Message は生の ex.Message と IDm を含んでおり Issue #1614 に反するが、
                //       既存テスト（NewLedgerFromSegmentsBuilderTests）がその内容を明示的に固定して
                //       いるため、文言の是正は別途仕様判断のうえで行う。
                ErrorDialogHelper.LogException(ex, "利用履歴の自動作成");
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
