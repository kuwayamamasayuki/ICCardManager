using System.Collections.Generic;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// 帳票（物品出納簿）の出力済み / 未出力を判定するサービス（Issue #1691）
    /// </summary>
    public interface IReportExportStatusService
    {
        /// <summary>
        /// 指定カード群について、対象年月の帳票が出力済みかどうかを判定する
        /// </summary>
        /// <param name="targets">対象カード</param>
        /// <param name="outputFolder">出力先フォルダ</param>
        /// <param name="year">対象年</param>
        /// <param name="month">対象月（1-12）</param>
        /// <returns>カードごとの出力状況（<paramref name="targets"/> と同じ順序）</returns>
        /// <remarks>
        /// ファイル走査を伴う同期処理。UI スレッドを塞がないよう、呼び出し側で
        /// <c>Task.Run</c> にオフロードすること（帳票の Excel 生成と同じ方針）。
        /// </remarks>
        IReadOnlyList<ReportExportStatus> GetStatuses(
            IEnumerable<ReportExportTarget> targets, string outputFolder, int year, int month);
    }
}
