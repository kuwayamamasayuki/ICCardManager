using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using ICCardManager.Common;

namespace ICCardManager.Views.Helpers
{
    /// <summary>
    /// スクリーンリーダーへ LiveRegionChanged を明示的に通知する（Issue #1548 / #2142）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>AutomationProperties.LiveSetting</c> を付けただけでは LiveRegionChanged は発火しない
    /// （#1509 / #1548 で実測済み）。<c>UIElementAutomationPeer.RaiseAutomationEvent</c> を呼ぶ必要がある。
    /// 読み上げられるのは<b>通知した要素の Name</b> なので、通知する要素の Name は表示内容そのもの
    /// （バインドまたはコードビハインドでの <c>AutomationProperties.SetName</c>）でなければならない。
    /// 固定のラベルを Name に置いたまま通知すると、変化のたびにそのラベルだけが読まれる（ui-conventions #2073）。
    /// </para>
    /// <para>
    /// 表示の更新 → 描画 → 通知の順を保証するため、<see cref="DispatcherPriority.ApplicationIdle"/> で
    /// 1 サイクル待ってから発火する。同期発火だとバインドの更新前に読み上げソフトが Name を問い合わせ、
    /// 古い内容が読まれる（#1507 で実機検証）。操作ログ画面（#1548）で確立した形を共有する（#1763）。
    /// </para>
    /// </remarks>
    public static class LiveRegionAnnouncer
    {
        /// <summary>
        /// <paramref name="element"/> の LiveRegionChanged を、表示の更新が済んだ後に発火する。
        /// </summary>
        /// <param name="element">通知する要素（Name が表示内容そのものであること）</param>
        /// <param name="operationName">失敗時にログへ残す操作名</param>
        public static void Announce(UIElement element, string operationName)
        {
            // Issue #1873: ディスパッチした処理の例外を観測してログへ残す
            element.Dispatcher.InvokeAsyncObserved(
                () =>
                {
                    var peer = UIElementAutomationPeer.FromElement(element)
                               ?? UIElementAutomationPeer.CreatePeerForElement(element);
                    peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                },
                operationName,
                DispatcherPriority.ApplicationIdle);
        }
    }
}
