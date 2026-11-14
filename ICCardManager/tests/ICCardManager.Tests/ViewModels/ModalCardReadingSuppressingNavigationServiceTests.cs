using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using ICCardManager.Views.Dialogs;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2484: メイン画面・履歴パネルのモーダルの表示中にカード読み取りを抑制する包み
/// （<see cref="ModalCardReadingSuppressingNavigationService"/>）の単体テスト。
/// </summary>
/// <remarks>
/// <para>
/// 包みのすべての表示メソッドについて「表示の間だけ抑制のスコープが開いている」「戻り値はそのまま返す」
/// 「例外で抜けても閉じる」を表明する。メソッドを 1 つでも素通しにすると、その種類のモーダルだけ抑制が漏れる。
/// </para>
/// <para>
/// メイン画面との実配線（表示中の職員証のタッチで状態が変わらないこと）は <c>MainViewModelTests</c> の
/// 「Issue #2484」region が表明する。
/// </para>
/// </remarks>
public class ModalCardReadingSuppressingNavigationServiceTests
{
    private readonly Mock<INavigationService> _inner = new(MockBehavior.Strict);
    private readonly ModalCardReadingSuppressingNavigationService _sut;
    private int _openScopes;
    private int _beginCount;

    public ModalCardReadingSuppressingNavigationServiceTests()
    {
        _sut = new ModalCardReadingSuppressingNavigationService(_inner.Object, BeginScope);
    }

    private IDisposable BeginScope()
    {
        _beginCount++;
        _openScopes++;
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private ModalCardReadingSuppressingNavigationServiceTests? _owner;

        public Scope(ModalCardReadingSuppressingNavigationServiceTests owner) => _owner = owner;

        public void Dispose()
        {
            if (_owner == null)
            {
                return;
            }

            _owner._openScopes--;
            _owner = null;
        }
    }

    [Fact]
    public void ShowConfirmation_表示の間だけ抑制し結果をそのまま返すこと()
    {
        int? openWhileShown = null;
        _inner.Setup(n => n.ShowConfirmation("本文", "題名"))
            .Callback(() => openWhileShown = _openScopes).Returns(true);

        _sut.ShowConfirmation("本文", "題名").Should().BeTrue();

        openWhileShown.Should().Be(1);
        _openScopes.Should().Be(0);
    }

    [Fact]
    public void ShowWarningConfirmation_表示の間だけ抑制し結果をそのまま返すこと()
    {
        int? openWhileShown = null;
        _inner.Setup(n => n.ShowWarningConfirmation("本文", "題名"))
            .Callback(() => openWhileShown = _openScopes).Returns(false);

        _sut.ShowWarningConfirmation("本文", "題名").Should().BeFalse();

        openWhileShown.Should().Be(1);
        _openScopes.Should().Be(0);
    }

    [Fact]
    public void ShowThreeWayConfirmation_表示の間だけ抑制し結果をそのまま返すこと()
    {
        int? openWhileShown = null;
        _inner.Setup(n => n.ShowThreeWayConfirmation("本文", "題名"))
            .Callback(() => openWhileShown = _openScopes).Returns((bool?)null);

        _sut.ShowThreeWayConfirmation("本文", "題名").Should().BeNull();

        openWhileShown.Should().Be(1);
        _openScopes.Should().Be(0);
    }

    [Fact]
    public void ShowInformation_ShowWarning_ShowError_表示の間だけ抑制すること()
    {
        var openWhileShown = new List<int>();
        _inner.Setup(n => n.ShowInformation("i", "t")).Callback(() => openWhileShown.Add(_openScopes));
        _inner.Setup(n => n.ShowWarning("w", "t")).Callback(() => openWhileShown.Add(_openScopes));
        _inner.Setup(n => n.ShowError("e", "t")).Callback(() => openWhileShown.Add(_openScopes));

        _sut.ShowInformation("i", "t");
        _sut.ShowWarning("w", "t");
        _sut.ShowError("e", "t");

        openWhileShown.Should().Equal(1, 1, 1);
        _openScopes.Should().Be(0);
    }

    [Fact]
    public void ShowCardRegistrationModeDialog_表示の間だけ抑制し結果をそのまま返すこと()
    {
        int? openWhileShown = null;
        _inner.Setup(n => n.ShowCardRegistrationModeDialog(1200))
            .Callback(() => openWhileShown = _openScopes)
            .Returns((CardRegistrationModeResult?)null);

        _sut.ShowCardRegistrationModeDialog(1200).Should().BeNull();

        openWhileShown.Should().Be(1);
        _openScopes.Should().Be(0);
    }

    [Fact]
    public void ShowDialog_表示の間だけ抑制し初期化の関数と結果をそのまま渡すこと()
    {
        int? openWhileShown = null;
        Action<ReportDialog> configure = _ => { };
        _inner.Setup(n => n.ShowDialog(configure))
            .Callback(() => openWhileShown = _openScopes).Returns(true);

        _sut.ShowDialog(configure).Should().BeTrue();

        openWhileShown.Should().Be(1);
        _openScopes.Should().Be(0);
    }

    /// <summary>
    /// 非同期の初期化を伴う表示は、初期化の完了を待ってから閉じるまで抑制すること（返した Task の完了まで解放しない）。
    /// </summary>
    [Fact]
    public async Task ShowDialogAsync_表示が終わるまで抑制し結果をそのまま返すこと()
    {
        var completion = new TaskCompletionSource<bool?>();
        Func<ReportDialog, Task> configure = _ => Task.CompletedTask;
        _inner.Setup(n => n.ShowDialogAsync(configure)).Returns(completion.Task);

        var pending = _sut.ShowDialogAsync(configure);
        _openScopes.Should().Be(1, "表示（初期化を含む）が終わるまで抑制する");

        completion.SetResult(false);
        (await pending).Should().BeFalse();
        _openScopes.Should().Be(0);
    }

    [Fact]
    public async Task ShowDialogAsync_ViewModelを初期化する形も表示が終わるまで抑制すること()
    {
        var completion = new TaskCompletionSource<bool?>();
        Func<LedgerRowEditViewModel, Task> configure = _ => Task.CompletedTask;
        _inner.Setup(n => n.ShowDialogAsync<LedgerRowEditDialog, LedgerRowEditViewModel>(configure))
            .Returns(completion.Task);

        var pending = _sut.ShowDialogAsync<LedgerRowEditDialog, LedgerRowEditViewModel>(configure);
        _openScopes.Should().Be(1, "表示（初期化を含む）が終わるまで抑制する");

        completion.SetResult(true);
        (await pending).Should().BeTrue();
        _openScopes.Should().Be(0);
    }

    [Fact]
    public void 同期の表示が例外で抜けても抑制を解放すること()
    {
        _inner.Setup(n => n.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new InvalidOperationException("injected"));

        Action act = () => _sut.ShowConfirmation("本文", "題名");

        act.Should().Throw<InvalidOperationException>();
        _beginCount.Should().Be(1, "前提: 抑制を取った");
        _openScopes.Should().Be(0, "例外で抜けても解放する");
    }

    [Fact]
    public async Task 非同期の表示が例外で抜けても抑制を解放すること()
    {
        _inner.Setup(n => n.ShowDialogAsync(It.IsAny<Func<ReportDialog, Task>>()))
            .ThrowsAsync(new InvalidOperationException("injected"));

        var act = () => _sut.ShowDialogAsync<ReportDialog>(null);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _beginCount.Should().Be(1, "前提: 抑制を取った");
        _openScopes.Should().Be(0, "例外で抜けても解放する");
    }

    /// <summary>
    /// 職員認証の包み（<see cref="ModalCardReadingSuppressingStaffAuthService"/>。PR #2491 のレビュー 3 回目）も、認証を求めている間
    /// （返した Task の完了まで）だけ抑制し、結果をそのまま返すこと。
    /// </summary>
    [Fact]
    public async Task 職員認証の包みは認証が終わるまで抑制し結果をそのまま返すこと()
    {
        var completion = new TaskCompletionSource<StaffAuthResult?>();
        var inner = new Mock<IStaffAuthService>(MockBehavior.Strict);
        inner.Setup(a => a.RequestAuthenticationAsync("履歴の統合")).Returns(completion.Task);
        var sut = new ModalCardReadingSuppressingStaffAuthService(inner.Object, BeginScope);
        var expected = new StaffAuthResult { Idm = "AABBCCDDEEFF0011", StaffName = "田中太郎" };

        var pending = sut.RequestAuthenticationAsync("履歴の統合");
        _openScopes.Should().Be(1, "認証を求めている間は抑制する");

        completion.SetResult(expected);
        (await pending).Should().BeSameAs(expected, "認証結果はそのまま返す（Enter は呼び出し元が行う）");
        _openScopes.Should().Be(0);
    }

    [Fact]
    public async Task 職員認証の包みは例外で抜けても抑制を解放すること()
    {
        var inner = new Mock<IStaffAuthService>(MockBehavior.Strict);
        inner.Setup(a => a.RequestAuthenticationAsync(It.IsAny<string>())).ThrowsAsync(new InvalidOperationException("injected"));
        var sut = new ModalCardReadingSuppressingStaffAuthService(inner.Object, BeginScope);

        var act = () => sut.RequestAuthenticationAsync("履歴の追加");

        await act.Should().ThrowAsync<InvalidOperationException>();
        _beginCount.Should().Be(1, "前提: 抑制を取った");
        _openScopes.Should().Be(0, "例外で抜けても解放する");
    }

    /// <summary>
    /// 表示のメソッドを足したときに、包みを素通しにしたまま見落とさないための検査。
    /// </summary>
    /// <remarks>
    /// インターフェースへメソッドを足すと包みはコンパイルエラーになるが、そこで「抑制せずに委譲するだけ」の実装を
    /// 書くと緑のまま漏れる。上の各テストが数えたメソッドの数と、インターフェースのメソッドの数を突き合わせる
    /// （足したらここが赤になり、そのメソッドの表明を上へ足すよう促す）。
    /// </remarks>
    [Fact]
    public void 包むメソッドの数がインターフェースの表示メソッドの数と一致すること()
    {
        var methods = new[] { typeof(INavigationService), typeof(IDialogService) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(m => m.ToString())
            .Distinct()
            .ToList();

        // 上で表明したメソッド: ShowConfirmation / ShowWarningConfirmation / ShowThreeWayConfirmation /
        // ShowInformation / ShowWarning / ShowError / ShowCardRegistrationModeDialog / ShowDialog /
        // ShowDialogAsync<TDialog> / ShowDialogAsync<TDialog, TViewModel>
        methods.Should().HaveCount(10, "表示メソッドを足したら、包みで抑制していることをこのクラスへ表明として足すこと");
    }
}
