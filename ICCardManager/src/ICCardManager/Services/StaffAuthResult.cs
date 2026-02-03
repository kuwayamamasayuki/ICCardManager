namespace ICCardManager.Services
{
    /// <summary>
    /// 職員認証の結果
    /// </summary>
    public class StaffAuthResult
    {
        /// <summary>
        /// 認証された職員のIDm
        /// </summary>
        public string Idm { get; set; } = string.Empty;

        /// <summary>
        /// 認証された職員の氏名
        /// </summary>
        public string StaffName { get; set; } = string.Empty;
    }
}
