using System.Threading.Tasks;
using ICCardManager.Dtos;

namespace ICCardManager.Services
{
    /// <summary>
    /// アプリが依存する外部リソースの状態を一括診断するサービス（Issue #1690）
    /// </summary>
    public interface IConnectionDiagnosticsService
    {
        /// <summary>
        /// 接続診断を実行する
        /// </summary>
        /// <returns>
        /// 全診断項目の結果と、診断を実行した PC の環境情報を含むレポート。
        /// 個々の項目が例外で失敗した場合もレポート全体は返り、該当項目のみ
        /// <see cref="DiagnosticStatus.Error"/> として報告される
        /// （1 項目の失敗で診断そのものが使えなくなる事態を避けるため）。
        /// </returns>
        Task<DiagnosticReport> RunDiagnosticsAsync();
    }
}
