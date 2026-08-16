using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;

namespace ICCardManager.Common
{
    /// <summary>
    /// モーダルダイアログ（MessageBox）のオーナーウィンドウを解決する（Issue #1794）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MessageBox.Show(message, title, button, image)</c> のようにオーナーを渡さない
    /// オーバーロードでは、WPF が <c>GetActiveWindow()</c> でオーナーを解決する。
    /// <b>呼び出しスレッドがフォアグラウンドでないときこれは NULL になり</b>、
    /// 下位のウィンドウが無効化されない（ownerless）。
    /// </para>
    /// <para>
    /// Issue #1383（エクスポート）/ #1784（インポート）/ #1793（F2/F3/F4/F6）で
    /// 「処理中オーバーレイを閉じてから結果ダイアログを表示する」構造へ是正した結果、
    /// この性質が実害に変わり得る状態になった。オーバーレイは視覚要素であると同時に
    /// <b>全面クリックシールド</b>でもあり、閉じるとシールドも同時に外れるため、
    /// エラーダイアログの背後にあるボタンが押せてしまう（再入）。
    /// </para>
    /// <para>
    /// オーナーを明示すれば WPF が下位ウィンドウを無効化するため、オーバーレイを
    /// クリックシールドとして兼用する必要がなくなる。副次的に、タスクバー上で親と
    /// 分離して見える問題やマルチモニタ環境で親と別の画面に出る問題も解消する。
    /// </para>
    /// <para>
    /// <b>本クラスの利用者は <c>MessageBox</c> 経路に限る。</b><c>Window.ShowDialog()</c> は
    /// アプリケーション内の他ウィンドウをすべて無効化するため本 Issue の欠陥を持たず、
    /// 現状は <c>Owner</c> に <c>Application.Current.MainWindow</c> を設定している
    /// （その是正は Issue #1837）。
    /// </para>
    /// </remarks>
    public static class DialogOwnerResolver
    {
        /// <summary>
        /// 現在のアプリケーションから、モーダル表示のオーナーにできるウィンドウを解決する
        /// </summary>
        /// <returns>
        /// オーナーにできるウィンドウ。解決できない場合は null
        /// （呼び出し側は従来どおり ownerless で表示すること。表示しないより望ましい）
        /// </returns>
        /// <remarks>
        /// UI スレッド以外から呼ばれた場合は null を返す。<c>Application.Current.Windows</c> は
        /// <c>DispatcherObject</c> であり、別スレッドから触ると例外になるため。
        /// </remarks>
        public static Window Resolve()
        {
            var app = Application.Current;
            if (app == null || !app.CheckAccess())
            {
                return null;
            }

            List<Window> windows;
            try
            {
                windows = app.Windows.Cast<Window>().ToList();
            }
            catch (InvalidOperationException)
            {
                // 防御的なガードであり、既知の到達経路は無い（上で CheckAccess() 済みのため
                // 列挙は UI スレッド上で同期実行され、その最中に他スレッドが
                // Application.Windows を変更することはない）。
                // 「競合を手当てしている」と読まれないようここに明記する
                // （.claude/rules/development-conventions.md #1726）。
                // それでも残すのは、本メソッドが**エラーダイアログの表示経路**で呼ばれるため。
                // ここで例外が漏れると、エラーを伝えようとした操作自体が二次例外で落ちる。
                return null;
            }

            return SelectOwner(windows, IsUsableOwner, w => w.IsActive, w => w.IsEnabled);
        }

        /// <summary>
        /// オーナーにするウィンドウを選ぶ（WPF に依存しない判断部分）
        /// </summary>
        /// <remarks>
        /// <para>優先順位:</para>
        /// <list type="number">
        /// <item>アクティブなウィンドウ（職員がいま操作している画面）</item>
        /// <item>
        /// モーダル子ウィンドウに塞がれていないウィンドウのうち最後のもの。
        /// <b>アプリが非フォアグラウンドのときはどれもアクティブでない</b>ため、この分岐が
        /// Issue #1794 の故障シナリオを受け持つ。<c>ShowDialog()</c> 中の親は
        /// <c>IsEnabled=false</c> になるので、これを外すと最前面のダイアログが選ばれる
        /// </item>
        /// <item>最後の使用可能なウィンドウ（すべて無効化されている場合の最後の手段）</item>
        /// </list>
        /// <para>
        /// <c>Application.MainWindow</c> を特別扱いしない。メイン画面も
        /// <c>Application.Windows</c> に含まれるうえ、モーダルダイアログが開いている間に
        /// メイン画面をオーナーにすると、そのダイアログが無効化されず欠陥がそのまま残る。
        /// </para>
        /// <para>
        /// WPF 非依存の署名にしているのは、<c>Window</c> が STA スレッドでしか生成できず
        /// 選択規則を単体テストで網羅できないため（<c>DialogOwnerResolverTests</c>）。
        /// </para>
        /// </remarks>
        /// <param name="windows">候補のウィンドウ（生成順）</param>
        /// <param name="isUsable">
        /// オーナーに指定できるか（<see cref="IsUsableOwnerState"/> を参照）。
        /// <b>この述語が緩いと、優先順位がどれだけ正しくても誤ったウィンドウが選ばれる</b>
        /// </param>
        /// <param name="isActive">アクティブか</param>
        /// <param name="isEnabled">有効か（false はモーダル子ウィンドウに塞がれている）</param>
        internal static T SelectOwner<T>(
            IEnumerable<T> windows,
            Func<T, bool> isUsable,
            Func<T, bool> isActive,
            Func<T, bool> isEnabled) where T : class
        {
            if (windows == null)
            {
                return null;
            }

            var usable = windows.Where(w => w != null && isUsable(w)).ToList();
            if (usable.Count == 0)
            {
                return null;
            }

            var active = usable.FirstOrDefault(isActive);
            if (active != null)
            {
                return active;
            }

            var enabled = usable.LastOrDefault(isEnabled);
            if (enabled != null)
            {
                return enabled;
            }

            return usable[usable.Count - 1];
        }

        /// <summary>
        /// オーナーに指定できるウィンドウ状態か（WPF に依存しない判断部分）
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>MessageBox.Show(owner, ...)</c> はオーナーのハンドルを要求するため、
        /// 未表示・クローズ済みのウィンドウを渡すと <c>InvalidOperationException</c> になる。
        /// <c>IsLoaded</c> ではなくハンドルの有無で判定するのは、クローズ後に
        /// <c>IsLoaded</c> が true のまま残る場合があるため。
        /// </para>
        /// <para>
        /// <b><c>ShowActivated=False</c> のウィンドウは除外する。</b>
        /// <c>ToastNotificationWindow</c>（トースト通知）は <c>Topmost</c> かつ
        /// <c>ShowActivated="False"</c> の非モーダルウィンドウで、貸出・返却のたびに
        /// <b>最後に生成されて</b> <c>Application.Windows</c> に載る。除外しないと、
        /// アプリが非フォアグラウンドのとき（＝ Issue #1794 の故障シナリオそのもの）
        /// 「アクティブなウィンドウが無い → 有効なウィンドウのうち最後」の分岐で
        /// <b>トーストがオーナーに選ばれる</b>。その害は 3 つある:
        /// </para>
        /// <list type="number">
        /// <item>Win32 の <c>MessageBox</c> は<b>オーナーだけ</b>を無効化するため、
        /// メイン画面や業務ダイアログは有効なまま残り、#1794 の欠陥が是正されない</item>
        /// <item>トーストは 3 秒（<c>DefaultDisplayDurationMs</c>）で自動的に閉じる。
        /// オーナーウィンドウが破棄されると Win32 は<b>その所有ウィンドウも破棄する</b>ため、
        /// 表示中の <c>MessageBox</c> が消え、<c>ShowConfirmation</c> は
        /// <c>MessageBoxResult</c> 0 を受け取って<b>無言で「いいえ」を返す</b></item>
        /// <item>エラー通知のトーストは <c>autoClose: false</c> で閉じるまで残るため、
        /// 上記が一過性ではなく持続する</item>
        /// </list>
        /// <para>
        /// 「アクティブ化しないウィンドウ」は設計上ユーザー操作の焦点ではなく、
        /// モーダルの親としての資格を持たない。判定を型名（<c>ToastNotificationWindow</c>）で
        /// 行わないのは、同種のウィンドウが増えたときに追随漏れが起きるため。
        /// </para>
        /// </remarks>
        /// <param name="isVisible">表示中か（<c>Window.IsVisible</c>）</param>
        /// <param name="hasHandle">ウィンドウハンドルを持つか</param>
        /// <param name="showActivated">表示時にアクティブ化するか（<c>Window.ShowActivated</c>）</param>
        internal static bool IsUsableOwnerState(bool isVisible, bool hasHandle, bool showActivated)
            => isVisible && hasHandle && showActivated;

        /// <summary>
        /// オーナーに指定できるウィンドウか（WPF の状態を読み取って <see cref="IsUsableOwnerState"/> へ渡す）
        /// </summary>
        private static bool IsUsableOwner(Window window)
            => IsUsableOwnerState(
                window.IsVisible,
                new WindowInteropHelper(window).Handle != IntPtr.Zero,
                window.ShowActivated);
    }
}
