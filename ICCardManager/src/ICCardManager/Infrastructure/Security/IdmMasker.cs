namespace ICCardManager.Infrastructure.Security
{
    /// <summary>
    /// Issue #1704: ログ出力用に IDm（カードの識別子）をマスクするヘルパー。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本システムでは職員証の IDm が唯一の認証要素（パスワード機構は存在しない）である。
    /// その IDm を平文でアプリログに出力すると、ログファイル（<c>C:\ProgramData\ICCardManager\Logs</c>、
    /// インストーラが <c>users-full</c> ACL を付与）を読める任意のローカルユーザーが
    /// 職員の認証クレデンシャルを収集でき、IDm エミュレーションによるなりすましに繋がる（CWE-532）。
    /// </para>
    /// <para>
    /// トラブルシュートでの識別性（どのカードかの相関）を残しつつ全体の露出を避けるため、
    /// 先頭 4 文字のみを残し、残りを <c>*</c> で伏せる。ログには生の IDm を決して出力せず、
    /// 本メソッドを通した値のみを出力すること。
    /// </para>
    /// </remarks>
    public static class IdmMasker
    {
        /// <summary>
        /// 先頭に残す可視文字数。
        /// </summary>
        public const int VisiblePrefixLength = 4;

        /// <summary>
        /// IDm をログ表示用にマスクする。先頭 <see cref="VisiblePrefixLength"/> 文字を残し、
        /// 残りを <c>*</c> に置換する。
        /// </summary>
        /// <param name="idm">生の IDm（16進16文字を想定）。null / 空はそのまま返す</param>
        /// <returns>マスク済み文字列（例: <c>0123************</c>）</returns>
        public static string Mask(string idm)
        {
            if (string.IsNullOrEmpty(idm))
                return idm;

            // 想定より短い IDm は全体を伏せる（短いクレデンシャルを部分露出させない）
            if (idm.Length <= VisiblePrefixLength)
                return new string('*', idm.Length);

            return idm.Substring(0, VisiblePrefixLength)
                   + new string('*', idm.Length - VisiblePrefixLength);
        }
    }
}
