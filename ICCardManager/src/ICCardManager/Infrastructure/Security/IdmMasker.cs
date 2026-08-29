namespace ICCardManager.Infrastructure.Security
{
    /// <summary>
    /// Issue #1704 / #1940: ログ出力用に IDm（カードの識別子）をマスクするヘルパー。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 本システムでは職員証の IDm が唯一の認証要素（パスワード機構は存在しない）である。
    /// その IDm を平文でアプリログに出力すると、ログファイル（<c>C:\ProgramData\ICCardManager\Logs</c>、
    /// インストーラが <c>users-full</c> ACL を付与）を読める任意のローカルユーザーが
    /// 職員の認証クレデンシャルを収集でき、IDm エミュレーションによるなりすましに繋がる（CWE-532）。
    /// </para>
    /// <para>
    /// <b>残すのは先頭 4 文字と末尾 4 文字で、中間を <c>*</c> で伏せる</b>（例: <c>07FE********ABCD</c>）。
    /// FeliCa の IDm は 8 バイトで、上位 2 バイトが<b>製造者コード</b>、下位 6 バイトが
    /// <b>カード識別番号</b>である。個体差は下位側にしか無いため、先頭だけを残すと
    /// 同一事業者から同時期に購入したカードがログ上で同じ文字列になる。
    /// #1704 の初版は先頭 4 文字のみを残しており、開発機の実データで
    /// 交通系ICカード 7 枚の先頭 4 文字が 5 種類（<c>07FE</c> × 2 枚、<c>05FE</c> × 2 枚が衝突）、
    /// 職員証 7 枚では 3 種類しかなく、意図していた「トラブルシュート時の識別性」が
    /// 成立していなかった（#1940）。
    /// </para>
    /// <para>
    /// 伏せる量は先頭のみを残す方式と比べて 48 bit から 32 bit（約 43 億通り）へ減るが、
    /// なりすましの攻撃手段は物理的なカードエミュレーション（1 回のタッチに数秒）であり、
    /// 現実的な脅威にならない。<b>先頭を残しても末尾を残しても伏せる量は同じ 48 bit だった</b>
    /// ため、初版は同じ安全性で識別できないほうを選んでいたことになる。
    /// </para>
    /// <para>
    /// ログには生の IDm を決して出力せず、本メソッドを通した値のみを出力すること
    /// （静的検査 <c>IdmLoggingMaskConventionTests</c> が全 <c>.cs</c> を走査して固定する。#1852）。
    /// </para>
    /// </remarks>
    public static class IdmMasker
    {
        /// <summary>
        /// 先頭に残す可視文字数（製造者コード側。カード種別の当たりを付ける用途）。
        /// </summary>
        public const int VisiblePrefixLength = 4;

        /// <summary>
        /// 末尾に残す可視文字数（カード識別番号側。個体を識別する用途）。
        /// </summary>
        public const int VisibleSuffixLength = 4;

        /// <summary>
        /// 伏せる部分に確保する最小文字数。可視幅（先頭＋末尾）にこの値を加えた長さ
        /// （<see cref="VisiblePrefixLength"/> + <see cref="VisibleSuffixLength"/> + 本値 ＝ 16 文字）
        /// を下回る入力は、部分露出させず全体を伏せる。
        /// </summary>
        public const int MinimumMaskedLength = 8;

        /// <summary>
        /// IDm をログ表示用にマスクする。先頭 <see cref="VisiblePrefixLength"/> 文字と
        /// 末尾 <see cref="VisibleSuffixLength"/> 文字を残し、中間を <c>*</c> に置換する。
        /// </summary>
        /// <param name="idm">生の IDm（16進16文字を想定）。null / 空はそのまま返す</param>
        /// <returns>マスク済み文字列（例: <c>0123********CDEF</c>）</returns>
        public static string Mask(string idm)
        {
            if (string.IsNullOrEmpty(idm))
                return idm;

            // 想定より短い IDm は全体を伏せる（短いクレデンシャルを部分露出させない）。
            // 可視 8 文字に対しマスクが MinimumMaskedLength に満たない長さ（15文字以下）が対象。
            if (idm.Length < VisiblePrefixLength + VisibleSuffixLength + MinimumMaskedLength)
                return new string('*', idm.Length);

            return idm.Substring(0, VisiblePrefixLength)
                   + new string('*', idm.Length - VisiblePrefixLength - VisibleSuffixLength)
                   + idm.Substring(idm.Length - VisibleSuffixLength);
        }
    }
}
