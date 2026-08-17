using System;
using System.Windows;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// モーダルダイアログのオーナー解決規則を固定する（Issue #1794）
/// </summary>
/// <remarks>
/// <para>
/// <c>MessageBox.Show</c> にオーナーを渡さないと、WPF は <c>GetActiveWindow()</c> で
/// オーナーを解決する。呼び出しスレッドがフォアグラウンドでないときこれは NULL になり、
/// 下位のウィンドウが無効化されない＝背後のボタンがクリックできてしまう。
/// </para>
/// <para>
/// 「ownerless になったか」は xUnit から観測できないため、<b>オーナーを選ぶ判断だけを
/// WPF 非依存の純関数（<see cref="DialogOwnerResolver.SelectOwner{T}"/>・
/// <see cref="DialogOwnerResolver.IsUsableOwnerState"/>・
/// <see cref="DialogOwnerResolver.ShouldDeferToNativeActiveWindow"/>）へ切り出し、
/// その規則をここで固定する</b>。<c>Window</c> の実体を要する検証は
/// <c>DialogServiceOwnerTests</c> が「継ぎ目を通っているか」の 1 点に絞って担う。
/// なお「モーダル子に塞がれているか」は Win32 の <c>IsWindowEnabled</c> で判定する
/// （<c>ShowDialog()</c> は WPF の <c>Window.IsEnabled</c> を変えない）。P/Invoke 部分は
/// xUnit から実 <c>Window</c> で再現できないため、ここでは述語の入力を代役で与える。
/// </para>
/// <para>
/// <b>候補を絞る述語（<c>IsUsableOwnerState</c>）も検査対象に含める。</b>
/// 優先順位がどれだけ正しくても、候補の絞り込みが緩ければ誤ったウィンドウが選ばれる。
/// 初版はここが「表示済み＋ハンドルあり」だけだったため、
/// <c>ShowActivated="False"</c> の <c>ToastNotificationWindow</c> が候補に残り、
/// アプリが非フォアグラウンドのとき（＝ #1794 の故障シナリオ）にトーストが
/// オーナーとして選ばれていた。
/// </para>
/// </remarks>
public class DialogOwnerResolverTests
{
    /// <summary>
    /// <c>Window</c> の状態だけを模した検査用の代役。
    /// WPF の <c>Window</c> は STA スレッドでしか生成できず、選択規則の網羅検証には向かない。
    /// </summary>
    private sealed class FakeWindow
    {
        public FakeWindow(string name, bool usable = true, bool active = false, bool enabled = true)
        {
            Name = name;
            Usable = usable;
            Active = active;
            Enabled = enabled;
        }

        public string Name { get; }

        /// <summary>表示済み・ウィンドウハンドルあり・アクティブ化する（オーナーに指定できる）</summary>
        public bool Usable { get; }

        public bool Active { get; }

        /// <summary>
        /// false はモーダル子ウィンドウに塞がれている状態（Win32 の <c>IsWindowEnabled</c> が false）を表す。
        /// WPF の <c>Window.IsEnabled</c> ではない（<c>ShowDialog()</c> はそれを変えない）。
        /// </summary>
        public bool Enabled { get; }

        public override string ToString() => Name;
    }

    private static FakeWindow Select(params FakeWindow[] windows)
        => DialogOwnerResolver.SelectOwner(windows, w => w.Usable, w => w.Active, w => w.Enabled);

    [Fact]
    public void アクティブなウィンドウがあればそれをオーナーにすること()
    {
        var main = new FakeWindow("main");
        var dialog = new FakeWindow("dialog", active: true);

        Select(main, dialog).Should().BeSameAs(dialog);
    }

    [Fact]
    public void 非アクティブのときはモーダル子に塞がれていないウィンドウのうち最後のものを選ぶこと()
    {
        // Issue #1794 の故障シナリオ: 職員が alt-tab したためどのウィンドウもアクティブでない。
        // メイン画面はモーダルダイアログの ShowDialog() により Win32 レベルで無効化されている
        //（IsWindowEnabled=false。WPF の IsEnabled は変わらない）ので、
        // ここでメイン画面をオーナーにすると下位のダイアログが無効化されず欠陥が残る。
        var main = new FakeWindow("main", enabled: false);
        var dialog = new FakeWindow("dialog");

        Select(main, dialog).Should().BeSameAs(dialog);
    }

    [Fact]
    public void 表示されていないウィンドウはアクティブでもオーナーにしないこと()
    {
        // 未表示・破棄済みのウィンドウはハンドルを持たず、MessageBox.Show(owner, ...) に渡しても
        // WPF は黙って GetActiveWindow() へ退化する（＝是正前の ownerless と同じ）。
        var notShown = new FakeWindow("notShown", usable: false, active: true);
        var main = new FakeWindow("main");

        Select(notShown, main).Should().BeSameAs(main);
    }

    [Fact]
    public void 非アクティブのとき最後のウィンドウがトースト通知でもオーナーにしないこと()
    {
        // ToastNotificationWindow は貸出・返却のたびに最後に生成され Application.Windows に載る。
        // 除外しないと「アクティブが無い → 有効なウィンドウのうち最後」でトーストが選ばれ、
        // Win32 の MessageBox はオーナーだけを無効化するため #1794 の欠陥が是正されない。
        // トーストの「使用可能」は代役の固定値ではなく本番の述語から導く。固定値 false にすると
        // ShowActivated の除外を IsUsableOwnerState から外しても本テストは緑のまま通る。
        var main = new FakeWindow("main", enabled: false);
        var dialog = new FakeWindow("dialog");
        var toast = new FakeWindow(
            "toast",
            usable: DialogOwnerResolver.IsUsableOwnerState(hasHandle: true, showActivated: false));

        Select(main, dialog, toast).Should().BeSameAs(dialog);
    }

    [Theory]
    // ハンドルあり（表示済みでクローズ前）・アクティブ化する → オーナーにできる
    [InlineData(true, true, true)]
    // ハンドルを持たない（未表示／クローズ済み）。渡しても例外にはならず WPF が黙って
    // GetActiveWindow() へ退化する＝オーナーを渡したつもりで ownerless になる
    [InlineData(false, true, false)]
    // ShowActivated=False（ToastNotificationWindow。モーダルの親としての資格を持たない）
    [InlineData(true, false, false)]
    public void ハンドルありかつアクティブ化するウィンドウだけをオーナー候補にすること(
        bool hasHandle, bool showActivated, bool expected)
    {
        DialogOwnerResolver.IsUsableOwnerState(hasHandle, showActivated)
            .Should().Be(expected);
    }

    [Fact]
    public void オーナーの最小化で隠れたモーダル子もオーナー候補に残ること()
    {
        // 職員が処理を待つ間にアプリを最小化すると、Win32 は所有ウィンドウを隠し（SW_PARENTCLOSING）
        // WPF は所有ダイアログの IsVisible を false にする。ここで IsVisible を候補条件に含めると
        // 最前面のモーダルダイアログだけが落ち、Win32 レベルで無効化済みのメイン画面がオーナーになって、
        // 復元後にダイアログのボタンが押せたまま残る（#1794 の最小化変種）。
        // 隠れているだけでハンドルを持つウィンドウは Win32 のオーナーとして有効。
        DialogOwnerResolver.IsUsableOwnerState(hasHandle: true, showActivated: true).Should().BeTrue();

        var main = new FakeWindow("main", enabled: false);
        var hiddenDialog = new FakeWindow(
            "dialog",
            usable: DialogOwnerResolver.IsUsableOwnerState(hasHandle: true, showActivated: true));

        Select(main, hiddenDialog).Should().BeSameAs(hiddenDialog);
    }

    [Fact]
    public void すべて無効化されていても使用可能な最後のウィンドウを返すこと()
    {
        var first = new FakeWindow("first", enabled: false);
        var last = new FakeWindow("last", enabled: false);

        Select(first, last).Should().BeSameAs(last);
    }

    [Fact]
    public void 使用可能なウィンドウが無ければnullを返すこと()
    {
        var notShown = new FakeWindow("notShown", usable: false);

        Select(notShown).Should().BeNull();
    }

    [Fact]
    public void ウィンドウが1つも無ければnullを返すこと()
    {
        Select().Should().BeNull();
    }

    [Fact]
    public void 候補の列挙がnullでもnullを返すこと()
    {
        DialogOwnerResolver.SelectOwner<FakeWindow>(null, w => true, w => true, w => true)
            .Should().BeNull();
    }

    [Fact]
    public void アクティブなウィンドウが無ければWPFへ委ねずに自前で解決すること()
    {
        // アプリが非フォアグラウンド（＝ #1794 の故障シナリオ）では GetActiveWindow() が NULL を返す。
        // ここで委ねると ownerless（欠陥そのもの）に戻ってしまう。
        DialogOwnerResolver.ShouldDeferToNativeActiveWindow(IntPtr.Zero, new[] { new IntPtr(1) })
            .Should().BeFalse();
    }

    [Fact]
    public void アクティブなウィンドウがWPFのウィンドウなら自前で解決すること()
    {
        var active = new IntPtr(2);

        DialogOwnerResolver.ShouldDeferToNativeActiveWindow(active, new[] { new IntPtr(1), active })
            .Should().BeFalse();
    }

    [Fact]
    public void アクティブなウィンドウがWPF以外なら解決をWPFへ委ねること()
    {
        // 表示中の MessageBox（Win32 ダイアログ）がアクティブな状態で、その内側から
        // 別の MessageBox を出す経路（確認ダイアログ表示中の職員証タッチ等）。
        // 是正前は ownerless → GetActiveWindow() ＝外側の MessageBox がオーナーになり外側を無効化していた。
        // ここで WPF ウィンドウを選ぶと外側が押せたまま残るため、この場合だけは従来どおり WPF に委ねる。
        var nativeDialog = new IntPtr(99);

        DialogOwnerResolver.ShouldDeferToNativeActiveWindow(nativeDialog, new[] { new IntPtr(1), new IntPtr(2) })
            .Should().BeTrue();
    }

    [Fact]
    public void Applicationが存在しないときはnullを返し例外にしないこと()
    {
        // 前提（Application.Current == null）はプロセス全体の環境依存であり、
        // このテストが作り出したものではない。将来どこかのテストが Application を
        // 生成すると、本テストは黙って別の分岐を検査し始めて**それでも緑になる**。
        // 前提が崩れたことをテスト自身が申告できるよう、先に表明しておく。
        Application.Current.Should().BeNull(
            "本テストは Application 未生成を前提とする。生成するテストが増えたら本テストを設計し直すこと");

        // オーナーを解決できない状況でも例外にせず、従来どおり ownerless で表示できること。
        DialogOwnerResolver.Resolve().Should().BeNull();
    }
}
