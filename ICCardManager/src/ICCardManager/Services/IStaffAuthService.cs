#nullable enable
using System.Threading.Tasks;

namespace ICCardManager.Services
{
    /// <summary>
    /// 職員認証サービスのインターフェース
    /// </summary>
    public interface IStaffAuthService
    {
        /// <summary>
        /// 職員証による認証を要求
        /// </summary>
        /// <param name="operationDescription">操作内容の説明（ダイアログに表示）</param>
        /// <returns>
        /// 認証結果（成功時はIDmと職員名、失敗/キャンセル時はnull）。
        /// 成功時の結果は認証した操作のスコープであり、呼び出し側は <c>using</c> で受けて
        /// 操作の完了時に破棄すること（Issue #2260。<see cref="StaffAuthResult"/> の remarks を参照）。
        /// </returns>
        Task<StaffAuthResult?> RequestAuthenticationAsync(string operationDescription);
    }
}
