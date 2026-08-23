using System.Windows;

namespace ICCardManager.Common
{
    /// <summary>
    /// オーナーを解決したうえで <c>MessageBox</c> を表示する（Issue #1794 / #1837）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>アプリ内で <c>MessageBox</c> を表示する手段は次の 3 つに限る。</b>
    /// </para>
    /// <list type="number">
    /// <item><c>Window</c> のコードビハインド: <c>MessageBox.Show(this, …)</c>（自ウィンドウが正しいオーナー）</item>
    /// <item>ViewModel: <c>IDialogService</c> 経由（<c>BusyScope</c> の静的検査の対象にもなる）</item>
    /// <item>上のどちらも使えない層（<c>App</c> の起動失敗・<c>ErrorDialogHelper</c> の致命エラー等）: 本クラス</item>
    /// </list>
    /// <para>
    /// <b>オーナー無しのオーバーロード（<c>MessageBox.Show(message, title, button, image)</c>）を
    /// 呼んでよいのは本クラスのフォールバック分岐ただ 1 か所。</b>
    /// 経路ごとに「解決できなければ ownerless」の分岐を書き写すと、次に規約を変える人が
    /// 一部を取りこぼす形をそのまま残す（<c>SafeRollback</c> が #1831 で同じ判断をした）。
    /// 回帰は <c>MessageBoxOwnerConventionTests</c> がリポジトリ全体のソーステキストで固定する。
    /// </para>
    /// <para>
    /// オーナーを解決できないときに<b>表示自体をやめない</b>のは、表示しないより
    /// クリックシールドの無い状態でも表示するほうが望ましいため（Issue #1794）。
    /// </para>
    /// </remarks>
    public static class OwnedMessageBox
    {
        /// <summary>
        /// オーナーを <see cref="DialogOwnerResolver"/> で解決してから表示する
        /// </summary>
        /// <param name="message">メッセージ</param>
        /// <param name="title">タイトル</param>
        /// <param name="button">ボタン構成</param>
        /// <param name="image">アイコン</param>
        /// <returns>ユーザーの選択結果</returns>
        public static MessageBoxResult Show(
            string message, string title, MessageBoxButton button, MessageBoxImage image)
            => Show(DialogOwnerResolver.Resolve(), message, title, button, image);

        /// <summary>
        /// 解決済みのオーナーで表示する（オーナーが null なら従来どおり ownerless で表示）
        /// </summary>
        /// <param name="owner">オーナーウィンドウ。null 可</param>
        /// <param name="message">メッセージ</param>
        /// <param name="title">タイトル</param>
        /// <param name="button">ボタン構成</param>
        /// <param name="image">アイコン</param>
        /// <returns>ユーザーの選択結果</returns>
        public static MessageBoxResult Show(
            Window owner, string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            return owner != null
                ? MessageBox.Show(owner, message, title, button, image)
                : MessageBox.Show(message, title, button, image);
        }
    }
}
