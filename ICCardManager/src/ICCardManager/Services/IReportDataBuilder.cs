using System.Threading.Tasks;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 月次帳票のデータ準備インターフェース
    /// </summary>
    /// <remarks>
    /// Issue #841: ReportService（Excel出力）とPrintService（印刷プレビュー）の
    /// 共通データ準備ロジックを統合するために導入。
    /// </remarks>
    public interface IReportDataBuilder
    {
        /// <summary>
        /// 月次帳票データを構築する
        /// </summary>
        /// <param name="cardIdm">カードIDm</param>
        /// <param name="year">年</param>
        /// <param name="month">月</param>
        /// <returns>帳票データ。カードが見つからない場合はnull</returns>
        Task<MonthlyReportData> BuildAsync(string cardIdm, int year, int month);
    }
}
