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
        /// <returns>認証結果（成功時はIDmと職員名、失敗/キャンセル時はnull）</returns>
        Task<StaffAuthResult?> RequestAuthenticationAsync(string operationDescription);
    }
}
