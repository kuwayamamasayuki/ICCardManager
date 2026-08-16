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
/// WPF 非依存の純関数（<see cref="DialogOwnerResolver.SelectOwner{T}"/>）へ切り出し、
/// その規則をここで固定する</b>。<c>Window</c> の実体を要する検証は
/// <c>DialogServiceOwnerTests</c> が「継ぎ目を通っているか」の 1 点に絞って担う。
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
        // 単体テスト実行時は Application.Current が null。
        // オーナーを解決できない状況でも例外にせず、従来どおり ownerless で表示できること。
        DialogOwnerResolver.Resolve().Should().BeNull();
    }
}
