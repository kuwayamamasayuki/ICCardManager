using System;
using System.Threading.Tasks;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// 管理者ダッシュボード（Issue #1692）のデータを構築するサービス
    /// </summary>
    /// <remarks>
    /// メイン画面内の「カード残高ダッシュボード」を担う <see cref="DashboardService"/> とは別物。
    /// あちらは窓口操作中に残額を一覧するためのもので、こちらは管理者が運用状況を俯瞰し
    /// カード枚数の適正を判断するためのもの。
    /// </remarks>
    public interface IAdminDashboardService
    {
        /// <summary>
        /// 運用状況（貸出中・長期未返却・残額不足・帳票未出力）を集計する
        /// </summary>
        /// <param name="asOf">集計の基準日時</param>
        /// <param name="longTermUnreturnedDays">長期未返却と判定する日数のしきい値</param>
        Task<AdminDashboardOperationStatus> GetOperationStatusAsync(DateTime asOf, int longTermUnreturnedDays);

        /// <summary>
        /// 利用分析（稼働状況・月別利用額・残高推移）を集計する
        /// </summary>
        /// <param name="fromDate">集計期間の開始日</param>
        /// <param name="toDate">集計期間の終了日</param>
        /// <param name="asOf">未使用日数の算出に使う基準日時</param>
        /// <remarks>
        /// 台帳は 6 年分保持されるため、呼び出し側は分析タブを開いたときに遅延ロードすること。
        /// </remarks>
        Task<AdminDashboardAnalytics> GetAnalyticsAsync(DateTime fromDate, DateTime toDate, DateTime asOf);
    }
}
