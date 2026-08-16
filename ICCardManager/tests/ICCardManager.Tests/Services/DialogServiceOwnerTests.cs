using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// <see cref="DialogService"/> がモーダル表示のたびにオーナーを解決して渡すことを固定する（Issue #1794）
/// </summary>
/// <remarks>
/// <para>
/// オーナー未指定の <c>MessageBox</c> は下位ウィンドウを無効化しないため、
/// 処理中オーバーレイ（クリックシールドを兼ねていた）を閉じてから結果ダイアログを出す
/// 現在の構造（Issue #1383 / #1784 / #1793）では、背後のボタンが押せてしまう。
/// </para>
/// <para>
/// <b>ownerless になったかどうかは xUnit から観測できない。</b>
/// ここで表明できるのは「すべてのメッセージ表示メソッドがオーナー解決の継ぎ目を経由し、
/// 解決結果をそのまま <c>MessageBox</c> へ渡している」ことまでで、
/// 実際にクリックが遮られることの確認は手動検証に委ねる。
/// オーナーを<b>選ぶ規則</b>そのものは <c>DialogOwnerResolverTests</c> が担う。
/// </para>
/// </remarks>
public class DialogServiceOwnerTests
{
    private sealed record ShowCall(
        Window Owner,
        string Message,
        string Title,
        MessageBoxButton Button,
        MessageBoxImage Image);

    /// <summary>
    /// 実際の <c>MessageBox</c> を出さずに、継ぎ目へ渡された値を記録する検査用サブクラス。
    /// （<c>ConnectionDiagnosticsService.ProbeFolderWriteAccess</c> と同じ差し替え方式）
    /// </summary>
    private sealed class RecordingDialogService : DialogService
    {
        private readonly Window _owner;

        public RecordingDialogService(Window owner = null)
        {
            _owner = owner;
        }

        public int ResolveOwnerCallCount { get; private set; }

        public List<ShowCall> Calls { get; } = new();

        public MessageBoxResult NextResult { get; set; } = MessageBoxResult.OK;

        protected override Window ResolveOwner()
        {
            ResolveOwnerCallCount++;
            return _owner;
        }

        protected override MessageBoxResult ShowMessageBoxCore(
            Window owner, string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            Calls.Add(new ShowCall(owner, message, title, button, image));
            return NextResult;
        }
    }

    /// <summary>
    /// WPF の <c>Window</c> は STA スレッドでしか生成できないため、専用スレッドで検証する
    /// </summary>
    private static void RunOnSta(Action action)
    {
        Exception captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("STA スレッドが時間内に完了すること");

        if (captured != null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    [Fact]
    public void すべてのメッセージ表示メソッドが解決したオーナーをMessageBoxへ渡すこと()
    {
        RunOnSta(() =>
        {
            var owner = new Window();
            var sut = new RecordingDialogService(owner);

            sut.ShowInformation("情報", "情報タイトル");
            sut.ShowWarning("警告", "警告タイトル");
            sut.ShowError("エラー", "エラータイトル");
            sut.ShowConfirmation("確認", "確認タイトル");
            sut.ShowWarningConfirmation("警告確認", "警告確認タイトル");

            sut.Calls.Should().HaveCount(5, "5 つのメッセージ表示メソッドすべてが継ぎ目を経由すること");
            sut.ResolveOwnerCallCount.Should().Be(5, "表示のたびにオーナーを解決し直すこと（アクティブなウィンドウは変わり得る）");
            sut.Calls.Should().OnlyContain(c => ReferenceEquals(c.Owner, owner),
                "解決したオーナーがそのまま MessageBox へ渡ること");
        });
    }

    public static IEnumerable<object[]> MessageBoxStyles => new[]
    {
        new object[] { "ShowInformation", MessageBoxButton.OK, MessageBoxImage.Information },
        new object[] { "ShowWarning", MessageBoxButton.OK, MessageBoxImage.Warning },
        new object[] { "ShowError", MessageBoxButton.OK, MessageBoxImage.Error },
        new object[] { "ShowConfirmation", MessageBoxButton.YesNo, MessageBoxImage.Question },
        new object[] { "ShowWarningConfirmation", MessageBoxButton.YesNo, MessageBoxImage.Warning },
    };

    [Theory]
    [MemberData(nameof(MessageBoxStyles))]
    public void 継ぎ目へ集約してもボタンとアイコンが従来どおりであること(
        string methodName, MessageBoxButton expectedButton, MessageBoxImage expectedImage)
    {
        var sut = new RecordingDialogService();

        Invoke(sut, methodName, "メッセージ", "タイトル");

        sut.Calls.Should().ContainSingle();
        sut.Calls[0].Message.Should().Be("メッセージ");
        sut.Calls[0].Title.Should().Be("タイトル");
        sut.Calls[0].Button.Should().Be(expectedButton);
        sut.Calls[0].Image.Should().Be(expectedImage);
    }

    [Theory]
    [InlineData("ShowConfirmation")]
    [InlineData("ShowWarningConfirmation")]
    public void 確認ダイアログはYesのときだけtrueを返すこと(string methodName)
    {
        var sut = new RecordingDialogService { NextResult = MessageBoxResult.Yes };
        Invoke(sut, methodName, "確認", "タイトル").Should().Be(true);

        sut.NextResult = MessageBoxResult.No;
        Invoke(sut, methodName, "確認", "タイトル").Should().Be(false);

        sut.NextResult = MessageBoxResult.Cancel;
        Invoke(sut, methodName, "確認", "タイトル").Should().Be(false);
    }

    [Fact]
    public void オーナーを解決できないときも例外にせず表示すること()
    {
        // Application.Current が無い / どのウィンドウも表示前、といった状況では
        // オーナーを付けられない。その場合は従来どおり ownerless で表示する
        // （表示できないより、クリックシールドが無い状態で表示するほうがまし）。
        var sut = new RecordingDialogService(owner: null);

        sut.ShowError("エラー", "タイトル");

        sut.Calls.Should().ContainSingle();
        sut.Calls[0].Owner.Should().BeNull();
    }

    /// <summary>
    /// <c>DialogService</c> 内の <c>MessageBox.Show</c> 直呼びが、オーナーを受け取る継ぎ目の
    /// 内側だけに存在することをソーステキスト上で固定する。
    /// </summary>
    /// <remarks>
    /// 上の挙動テストは<b>既存の 5 メソッドしか通らない</b>ため、6 つ目のメソッドが
    /// <c>MessageBox.Show</c> を直呼びして追加されても検出できない。
    /// 静的検査と挙動テストは対で置く（<c>.claude/rules/development-conventions.md</c> #1793）。
    /// </remarks>
    [Fact]
    public void MessageBoxの直呼びが継ぎ目の内側だけにあること()
    {
        var path = Path.Combine(TestPaths.GetProductionSourceRoot(), "Services", "DialogService.cs");
        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(path));

        var seamBody = TestSourceInspection.ExtractMethodBody(
            code, "protected virtual MessageBoxResult ShowMessageBoxCore");

        // 抽出範囲の妥当性を先に固定する。式形式（=> ...）へ変えると波括弧が無く、
        // ExtractMethodBody は「次に現れた別のブロック」を静かに返すため、
        // これを表明しないと検査が空振りしたまま緑になる。
        seamBody.Should().Contain("owner != null", "継ぎ目メソッドの本体が抽出できていること");

        var pattern = new Regex(@"MessageBox\.Show\s*\(");
        var totalCalls = pattern.Matches(code).Count;
        var seamCalls = pattern.Matches(seamBody).Count;

        seamCalls.Should().Be(2, "継ぎ目はオーナー有無の 2 分岐を持つこと");
        totalCalls.Should().Be(seamCalls,
            "DialogService 内の MessageBox.Show は継ぎ目の内側だけに存在すること（Issue #1794）");
    }

    private static object Invoke(DialogService sut, string methodName, string message, string title)
    {
        var method = typeof(IDialogService).GetMethod(methodName, new[] { typeof(string), typeof(string) });
        method.Should().NotBeNull($"IDialogService に {methodName}(string, string) が存在すること");
        return method.Invoke(sut, new object[] { message, title });
    }
}
