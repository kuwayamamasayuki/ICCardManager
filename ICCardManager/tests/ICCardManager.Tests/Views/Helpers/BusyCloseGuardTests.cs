using System;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using FluentAssertions;
using ICCardManager.ViewModels;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2141: 処理中のダイアログを利用者の操作（✕・Alt+F4・Esc）で閉じさせないガードを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 処理中オーバーレイが塞ぐのはマウスだけで（#1761）、Esc と ✕ は効いていた。閉じる処理に処理中の判定を
/// 持っていたのは <c>LedgerDetailDialog</c>（#1743）だけだった。
/// </para>
/// <para>
/// <b>アプリ自身の閉じる（<c>Window.Close()</c>）は止めない。</b>行編集は処理中のまま <c>IsSaved</c> を立てて
/// 閉じるため、<c>Closing</c> で処理中を拒むと保存しても閉じなくなる。ガードは利用者起点の入口
/// （<c>WM_SYSCOMMAND(SC_CLOSE)</c> と Esc）でだけ止める。この区別は判断の純関数と、
/// 「<c>Closing</c> を使っていないこと」の静的検査で固定する。
/// </para>
/// <para>
/// <b>検査できない範囲</b>: 実際に ✕・Alt+F4 がこのメッセージで届くか、Esc が <c>IsCancel</c> より先に
/// 握り潰されるかは Win32 / WPF の実行時挙動で、xUnit からは確かめられない（<c>Window</c> は STA 依存）。
/// 実機での確認は PR の手動テスト手順に記す。
/// </para>
/// </remarks>
public class BusyCloseGuardTests
{
    private sealed class FakeBusy : IBusyState
    {
        public bool IsBusy { get; set; }
    }

    [Fact]
    public void 処理中のダイアログは利用者の閉じるを止めること()
    {
        BusyCloseGuard.ShouldBlockUserClose(new FakeBusy { IsBusy = true }, isMainWindow: false).Should().BeTrue();
    }

    [Fact]
    public void 処理中でなければ閉じるを止めないこと()
    {
        // 対の表明: 常に止める実装（＝ダイアログを閉じられなくなる）を落とす
        BusyCloseGuard.ShouldBlockUserClose(new FakeBusy { IsBusy = false }, isMainWindow: false).Should().BeFalse();
    }

    [Fact]
    public void 処理中の状態を持たない画面は止めないこと()
    {
        BusyCloseGuard.ShouldBlockUserClose(null, isMainWindow: false).Should().BeFalse("DataContext が無い画面（職員証認証等）");
        BusyCloseGuard.ShouldBlockUserClose(new object(), isMainWindow: false).Should().BeFalse();
    }

    [Fact]
    public void メイン画面は対象外であること()
    {
        // メイン画面を閉じることはアプリの終了で、その扱いは MainWindow.OnClosing が持つ
        BusyCloseGuard.ShouldBlockUserClose(new FakeBusy { IsBusy = true }, isMainWindow: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(0x0112, 0xF060, true)]   // WM_SYSCOMMAND / SC_CLOSE（✕・Alt+F4・システムメニュー）
    [InlineData(0x0112, 0xF062, true)]   // 下位 4 ビットはシステムが使う（Win32 の仕様）
    [InlineData(0x0112, 0xF020, false)]  // SC_MINIMIZE
    [InlineData(0x0112, 0xF030, false)]  // SC_MAXIMIZE
    [InlineData(0x0010, 0x0000, false)]  // WM_CLOSE（Window.Close() が送る。アプリ自身の閉じるは止めない）
    public void 利用者の閉じる操作だけを判別すること(int msg, int wParam, bool expected)
    {
        BusyCloseGuard.IsUserCloseCommand(msg, new IntPtr(wParam)).Should().Be(expected);
    }

    [Theory]
    [InlineData(Key.Escape, true, true)]
    [InlineData(Key.Escape, false, false)]
    [InlineData(Key.Enter, true, false)]
    public void 処理中のEscだけを握り潰すこと(Key key, bool isBusy, bool expected)
    {
        BusyCloseGuard.ShouldSwallowKey(key, new FakeBusy { IsBusy = isBusy }, isMainWindow: false).Should().Be(expected);
    }
}

/// <summary>
/// Issue #2141: 処理中ガードの結線と対象範囲を固定する。
/// </summary>
public class BusyCloseGuardConventionTests
{
    [Fact]
    public void 起動時に全ウィンドウへガードを掛けていること()
    {
        var app = ProductionSourceFiles.CSharp.Single(f => f.Name == "App.xaml.cs").CodeOnly;

        app.Should().Contain("BusyCloseGuard.Register()",
            "画面ごとに結線すると、画面が増えたときに掛け忘れる（#1786）");
    }

    [Fact]
    public void ガードはClosingでは判定しないこと()
    {
        // Closing で処理中を拒むと、処理中に IsSaved を立てて Close() する行編集の「保存して閉じる」まで止まる
        var guard = ProductionSourceFiles.CSharp.Single(f => f.Name == "BusyCloseGuard.cs").CodeOnly;

        guard.Should().NotContain("Closing", "アプリ自身の Close() を止めない");
        guard.Should().Contain("AddHook", "対の表明: ✕・Alt+F4 は WM_SYSCOMMAND のフックで止める");
        guard.Should().Contain("PreviewKeyDownEvent", "対の表明: Esc はウィンドウの PreviewKeyDown で止める");
    }

    /// <summary>
    /// 処理中フラグ（<c>IsBusy</c>）を公開する ViewModel は、すべて <see cref="IBusyState"/> を実装していること。
    /// </summary>
    /// <remarks>
    /// ガードは型ではなく <see cref="IBusyState"/> で判定する。<c>ViewModelBase</c> を継承せずに独自の
    /// <c>IsBusy</c> を持つ画面（<c>LedgerDetailViewModel</c>）があるため、対象を「<c>IsBusy</c> を持つ」性質から
    /// 導出する（#1786）。実装を忘れた画面は、処理中でも ✕・Esc で閉じられる状態に黙って戻る。
    /// </remarks>
    [Fact]
    public void IsBusyを持つViewModelはすべてIBusyStateを実装していること()
    {
        var withIsBusy = typeof(ViewModelBase).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "ICCardManager.ViewModels")
            .Where(t => t.GetProperty("IsBusy", BindingFlags.Public | BindingFlags.Instance)?.PropertyType == typeof(bool))
            .ToList();

        withIsBusy.Should().Contain(typeof(LedgerDetailViewModel),
            "空振り検出: ViewModelBase を継承しない画面まで走査できていること");
        withIsBusy.Should().Contain(typeof(LedgerRowEditViewModel));

        withIsBusy.Where(t => !typeof(IBusyState).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Should().BeEmpty("IBusyState を実装しないと、処理中でもダイアログを閉じられる");
    }
}
