using System;
using System.Collections.Generic;
using System.IO;
using ICCardManager.Common;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// 出力先フォルダの実ファイルを走査して帳票の出力済み / 未出力を判定する（Issue #1691）
    /// </summary>
    /// <remarks>
    /// ファイル名（年度ファイル）とシート名（「N月」）の決め方は <see cref="ReportService"/> の
    /// 生成ロジックをそのまま呼び出す。ここで書式を再実装すると、帳票側の命名規則を変えたときに
    /// 「出力したのに未出力と表示される」形で静かに乖離するため。
    /// </remarks>
    public class ReportExportStatusService : IReportExportStatusService
    {
        /// <inheritdoc/>
        public IReadOnlyList<ReportExportStatus> GetStatuses(
            IEnumerable<ReportExportTarget> targets, string outputFolder, int year, int month)
        {
            var statuses = new List<ReportExportStatus>();
            if (targets == null)
            {
                return statuses;
            }

            // 出力先が未指定・存在しない場合は「未出力」ではなく「判定不能」。
            // フォルダを選び直せば解決する問題を「未出力」と表示すると、
            // 出力済みのカードを二重に出力させてしまう。
            var isFolderReadable = IsFolderReadable(outputFolder);

            var fiscalYear = ReportService.GetFiscalYear(year, month);
            var sheetName = ReportService.GetMonthSheetName(month);

            foreach (var target in targets)
            {
                statuses.Add(GetStatus(target, outputFolder, isFolderReadable, fiscalYear, sheetName));
            }

            return statuses;
        }

        /// <summary>
        /// カード1枚ぶんの出力状況を判定する
        /// </summary>
        private static ReportExportStatus GetStatus(
            ReportExportTarget target,
            string outputFolder,
            bool isFolderReadable,
            int fiscalYear,
            string sheetName)
        {
            var status = new ReportExportStatus
            {
                CardIdm = target?.CardIdm ?? string.Empty,
                State = ReportExportState.Unknown,
            };

            if (target == null || !isFolderReadable)
            {
                return status;
            }

            string filePath;
            try
            {
                var fileName = ReportService.GetFiscalYearFileName(
                    target.CardType, target.CardNumber, fiscalYear);
                filePath = Path.Combine(outputFolder, fileName);
            }
            catch (ArgumentException)
            {
                // 出力先フォルダに不正文字が含まれる等でパスを組み立てられない場合
                return status;
            }

            status.FilePath = filePath;

            if (!File.Exists(filePath))
            {
                // 年度ファイル自体が無い = その年度をまだ一度も出力していない
                status.State = ReportExportState.NotExported;
                return status;
            }

            if (!XlsxSheetNameReader.TryContainsSheet(filePath, sheetName, out var containsSheet))
            {
                // ファイルはあるが読めない（破損・権限不足など）
                return status;
            }

            if (!containsSheet)
            {
                // 年度ファイルはあるが対象月のシートが無い = その月は未出力
                status.State = ReportExportState.NotExported;
                return status;
            }

            status.State = ReportExportState.Exported;
            status.LastWriteTime = TryGetLastWriteTime(filePath);
            return status;
        }

        /// <summary>
        /// 出力先フォルダが実際に読めるかを判定する
        /// </summary>
        private static bool IsFolderReadable(string outputFolder)
        {
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                return false;
            }

            try
            {
                // UNC パスや切断されたネットワークドライブでは例外になり得る
                return Directory.Exists(outputFolder);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// ファイルの最終更新日時を取得する（取得できない場合はnull）
        /// </summary>
        private static DateTime? TryGetLastWriteTime(string filePath)
        {
            try
            {
                return File.GetLastWriteTime(filePath);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
