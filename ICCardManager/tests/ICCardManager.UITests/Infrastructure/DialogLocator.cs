using System;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;

namespace ICCardManager.UITests.Infrastructure
{
    /// <summary>
    /// ダイアログの上に開くダイアログ（二段モーダル）を UIA から探す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPF のモーダルは、開いたウィンドウの <c>ModalWindows</c> に現れるとは限らない。
    /// <c>Owner</c> の設定やアクティブ化のタイミング次第で、メイン画面の <c>ModalWindows</c> にだけ
    /// 見えたり、どちらにも現れずトップレベルのウィンドウとしてしか観測できないことがある
    /// （Issue #1522 で操作ログダイアログについて実測済み。<c>OperationLogQuickFilterDisplayTests</c>
    /// が同じ 3 経路のフォールバックを持つ）。
    /// </para>
    /// <para>
    /// 開いた側の <c>ModalWindows</c> だけを見る実装は、その経路に現れなかった日に
    /// 「ダイアログが開かない」という原因の分からないタイムアウトになる。撮影（Issue #2011）でも
    /// 同じ形を踏んだため、経路の列挙をここ 1 か所に集約する。
    /// </para>
    /// </remarks>
    internal static class DialogLocator
    {
        /// <summary>
        /// <paramref name="opener"/> から開いたダイアログが現れるまで待つ。
        /// </summary>
        /// <param name="fixture">起動中のアプリ。</param>
        /// <param name="opener">ダイアログを開いたウィンドウ。</param>
        /// <param name="dialogName">ダイアログの AutomationProperties.Name。</param>
        /// <param name="timeout">待ち時間。既定は <see cref="TestConstants.OperationLogDialogOpenTimeoutSeconds"/> 秒。</param>
        /// <exception cref="TimeoutException">時間内に見つからなかった場合。</exception>
        public static Window WaitForNestedDialog(
            AppFixture fixture, Window opener, string dialogName, TimeSpan? timeout = null)
        {
            if (fixture == null)
            {
                throw new ArgumentNullException(nameof(fixture));
            }

            if (opener == null)
            {
                throw new ArgumentNullException(nameof(opener));
            }

            var effective = timeout ?? TimeSpan.FromSeconds(TestConstants.OperationLogDialogOpenTimeoutSeconds);
            var found = Retry.WhileNull(() => Find(fixture, opener, dialogName), effective).Result;
            if (found == null)
            {
                throw new TimeoutException(
                    $"ダイアログ \"{dialogName}\" が {effective.TotalSeconds} 秒以内に開きませんでした" +
                    $"（\"{opener.Name}\" から開く操作の後、開いた側・メイン画面・トップレベルのいずれにも現れていません）。" +
                    "前面に別のモーダルが出ていないか確認してください。");
            }
            return found;
        }

        /// <summary>
        /// ダイアログが開いているか（Issue #2192）。<see cref="WaitForNestedDialog"/> と同じ 3 か所を探す。
        /// </summary>
        /// <remarks>
        /// 「閉じたこと」を開いた側の ModalWindows だけで判定すると、メイン画面の配下やトップレベルに見えている
        /// ダイアログを「閉じた」と取り違える。探す場所は開くのを待つときと同じでなければならない。
        /// </remarks>
        public static bool IsOpen(AppFixture fixture, Window opener, string dialogName) =>
            Find(fixture, opener, dialogName) != null;

        /// <summary>
        /// モーダルのダイアログが閉じるのを待つ（Issue #2192）。閉じたら true。
        /// </summary>
        /// <remarks>
        /// UIA から見つからないことだけで判定すると、一時的な取得失敗（<see cref="Find"/> は例外を「見つからない」へ畳む）を
        /// 「閉じた」と取り違える。モーダルのダイアログが開いている間はメイン画面が Win32 レベルで無効なので、
        /// メイン画面が有効に戻ったことを合わせて見る（時間に依存しない別の観測で裏を取る）。
        /// </remarks>
        public static bool WaitUntilClosed(AppFixture fixture, Window opener, string dialogName)
        {
            var mainHandle = NativeWindows.HandleOf(fixture.MainWindow);
            return Retry.WhileFalse(
                () => !IsOpen(fixture, opener, dialogName) && NativeWindows.IsEnabled(mainHandle),
                TimeSpan.FromSeconds(TestConstants.DialogOpenTimeoutSeconds)).Success;
        }

        /// <summary>
        /// アプリのトップレベルのウィンドウとそのモーダルを列挙する（待機に失敗したときの原因の切り分け用。Issue #2190 / #2192）。
        /// </summary>
        public static string DescribeOpenWindows(AppFixture fixture)
        {
            try
            {
                return string.Join(" / ", fixture.App.GetAllTopLevelWindows(fixture.Automation).Select(w =>
                    $"「{w.Name}」({w.ClassName}) モーダル[{string.Join(", ", w.ModalWindows.Select(m => $"「{m.Name}」({m.ClassName})"))}]"));
            }
            catch (Exception ex)
            {
                return $"（列挙に失敗: {ex.GetType().Name}）";
            }
        }

        private static Window? Find(AppFixture fixture, Window opener, string dialogName)
        {
            try
            {
                // (1) 開いた側の配下
                var fromOpener = opener.ModalWindows.FirstOrDefault(w => w.Name == dialogName);
                if (fromOpener != null)
                {
                    return fromOpener;
                }

                // (2) メイン画面の配下（Owner がメイン画面になっている場合）
                var fromMain = fixture.MainWindow.ModalWindows.FirstOrDefault(w => w.Name == dialogName);
                if (fromMain != null)
                {
                    return fromMain;
                }

                // (3) トップレベルのウィンドウ（どちらの ModalWindows にも現れない場合）
                return fixture.App.GetAllTopLevelWindows(fixture.Automation)
                    .FirstOrDefault(w => w.Name == dialogName);
            }
            catch
            {
                // ウィンドウが作り直される途中は UIA が一時的に例外を投げる。次の試行で拾う
                return null;
            }
        }
    }
}
