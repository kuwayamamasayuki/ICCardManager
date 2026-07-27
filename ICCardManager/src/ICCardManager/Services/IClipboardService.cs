namespace ICCardManager.Services
{
    /// <summary>
    /// クリップボードへの書き込みを抽象化する（Issue #1690）
    /// </summary>
    /// <remarks>
    /// <c>System.Windows.Clipboard</c> は STA スレッドを要求するため、
    /// ViewModel が直接呼ぶと単体テストがスレッド構成に縛られる。
    /// また、他プロセスがクリップボードをロックしていると失敗し得るので、
    /// 「コピーできたか」を戻り値で返し、呼び出し側が利用者へ伝えられるようにする。
    /// </remarks>
    public interface IClipboardService
    {
        /// <summary>
        /// クリップボードへテキストを設定する
        /// </summary>
        /// <param name="text">設定するテキスト</param>
        /// <returns>設定できた場合 true。他プロセスによるロック等で失敗した場合 false</returns>
        bool TrySetText(string text);
    }
}
