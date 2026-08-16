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
/// WPF 非依存の純関数（<see cref="DialogOwnerResolver.SelectOwner{T}"/> と
/// <see cref="DialogOwnerResolver.IsUsableOwnerState"/>）へ切り出し、
/// その規則をここで固定する</b>。<c>Window</c> の実体を要する検証は
/// <c>DialogServiceOwnerTests</c> が「継ぎ目を通っているか」の 1 点に絞って担う。
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

        /// <summary>表示済みでウィンドウハンドルを持つ（オーナーに指定できる）</summary>
        public bool Usable { get; }

        public bool Active { get; }

        /// <summary>false はモーダル子ウィンドウに塞がれている状態を表す</summary>
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
        // メイン画面はモーダルダイアログに塞がれて IsEnabled=false なので、
        // ここでメイン画面をオーナーにすると下位のダイアログが無効化されず欠陥が残る。
        var main = new FakeWindow("main", enabled: false);
        var dialog = new FakeWindow("dialog");

        Select(main, dialog).Should().BeSameAs(dialog);
    }

    [Fact]
    public void 表示されていないウィンドウはアクティブでもオーナーにしないこと()
    {
        // 未表示・破棄済みのウィンドウはハンドルを持たず、
        // MessageBox.Show(owner, ...) に渡すと InvalidOperationException になる。
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
        var main = new FakeWindow("main", enabled: false);
        var dialog = new FakeWindow("dialog");
        var toast = new FakeWindow("toast", usable: false);

        Select(main, dialog, toast).Should().BeSameAs(dialog);
    }

    [Theory]
    // 表示中・ハンドルあり・アクティブ化する → オーナーにできる
    [InlineData(true, true, true, true)]
    // 未表示（生成しただけ／クローズ済み）
    [InlineData(false, true, true, false)]
    // ハンドルを持たない（MessageBox.Show(owner, ...) が InvalidOperationException になる）
    [InlineData(true, false, true, false)]
    // ShowActivated=False（ToastNotificationWindow。モーダルの親としての資格を持たない）
    [InlineData(true, true, false, false)]
    public void 表示済みハンドルありかつアクティブ化するウィンドウだけをオーナー候補にすること(
        bool isVisible, bool hasHandle, bool showActivated, bool expected)
    {
        DialogOwnerResolver.IsUsableOwnerState(isVisible, hasHandle, showActivated)
            .Should().Be(expected);
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
