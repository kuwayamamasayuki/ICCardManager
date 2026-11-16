using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2494: 職員管理ダイアログの職員証の認証の保持の表示・「認証を解除」・無操作の検出が、画面に出る形で置かれていることの検証
/// </summary>
/// <remarks>
/// 保持・破棄の条件は <c>StaffManageViewModelAuthenticationHoldTests</c> が固定する。ViewModel のテストはプロパティの値しか
/// 見ないため、表示が束縛されていない・編集フォームの内側で畳まれる・操作の通知が配線されていない、といった View 側の欠落は
/// 本クラスで見る（#1727 と同じ分担）。通しの動きは UI テスト <c>StaffManageAuthenticationHoldUiTests</c> が確かめる。
/// </remarks>
public class StaffManageDialogHeldAuthenticationLayoutTests
{
    private static readonly string StaffManageDialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "StaffManageDialog.xaml"));

    private static string ReadXaml()
        => XamlElementInspection.StripXmlComments(File.ReadAllText(StaffManageDialogXamlPath));

    private static XamlElementInspection.XamlElementSpan FindPanel(string xaml)
    {
        var panels = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Border")
            .Where(b => XamlElementInspection.GetAttribute(b.StartTag, "x:Name") == "HeldAuthenticationPanel")
            .ToList();
        panels.Should().ContainSingle("職員証の認証の保持の表示の枠（x:Name=\"HeldAuthenticationPanel\"）は 1 つだけ置く");
        return panels[0];
    }

    [Fact]
    public void 保持の表示は常に出し_折り返しと読み上げの名前を持つこと()
    {
        var panel = FindPanel(ReadXaml());

        XamlElementInspection.GetAttribute(panel.StartTag, "Visibility").Should().BeNull(
            "誰の名前で記録されるかを常に見せる（保持していない間も「なし」を表示する）");

        var text = XamlElementInspection.EnumerateStartTags(panel.Body)
            .Single(t => t.StartTag.StartsWith("<TextBlock", System.StringComparison.Ordinal));
        XamlElementInspection.GetAttribute(text.StartTag, "x:Name").Should().Be("HeldAuthenticationText",
            "UI テストが AutomationId で探す");
        XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(text.StartTag, "Text"))
            .Should().Be("HeldAuthenticationText");
        XamlElementInspection.GetAttribute(text.StartTag, "TextWrapping").Should().Be("Wrap",
            "文字サイズ「特大」でも切れないように折り返す");
        XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(text.StartTag, "AutomationProperties.Name"))
            .Should().Be("HeldAuthenticationText", "スクリーンリーダーにも文言を読み上げさせる");
    }

    [Fact]
    public void 認証を解除ボタンは保持している間だけ表示し_解除のコマンドに束縛すること()
    {
        var panel = FindPanel(ReadXaml());
        var button = XamlElementInspection.EnumerateStartTags(panel.Body)
            .Single(t => t.StartTag.StartsWith("<Button", System.StringComparison.Ordinal));

        XamlElementInspection.GetAttribute(button.StartTag, "Content").Should().Be("認証を解除");
        XamlElementInspection.GetBindingPropertyName(XamlElementInspection.GetAttribute(button.StartTag, "Command"))
            .Should().Be("ReleaseHeldAuthenticationCommand");
        var visibility = XamlElementInspection.GetAttribute(button.StartTag, "Visibility");
        XamlElementInspection.GetBindingPropertyName(visibility).Should().Be("IsAuthenticationHeld");
        visibility.Should().Contain("BoolToVisibilityConverter",
            "IsAuthenticationHeld は bool なので、bool を受けるコンバーターで表示を切り替える（#2369）");
    }

    [Fact]
    public void 保持の表示は編集フォームの内側に置かないこと()
    {
        var xaml = ReadXaml();
        var panel = FindPanel(xaml);

        XamlElementInspection.EnumerateEnclosingElements(xaml, panel.Start)
            .Select(e => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(e.StartTag, "Visibility")))
            .Should().NotContain(new[] { "IsEditing", "IsNewStaff" },
                "一覧を見ている間（編集していないとき）にも、誰の名前で記録されるかを見せる");
    }

    /// <summary>
    /// 無操作の検出: キー入力・マウスのボタン・ホイールを、処理済みのイベントも含めて（handledEventsToo）同じ 1 つのハンドラで
    /// ViewModel へ知らせる。マウスの移動は操作とみなさない。ウィンドウのトンネル（Preview）では受けない（#2080 の規約）。
    /// </summary>
    [Fact]
    public void 画面への操作はキー入力とマウスのボタンとホイールで知らせ_マウスの移動は数えないこと()
    {
        var codeBehind = File.ReadAllText(Path.ChangeExtension(StaffManageDialogXamlPath, ".xaml.cs"));
        var code = TestSourceInspection.RemoveCommentsPreservingLines(codeBehind);

        foreach (var (routedEvent, handlerType) in new[]
                 {
                     ("KeyDownEvent", "KeyEventHandler"),
                     ("MouseDownEvent", "MouseButtonEventHandler"),
                     ("MouseWheelEvent", "MouseWheelEventHandler"),
                 })
        {
            System.Text.RegularExpressions.Regex.IsMatch(code,
                    $@"AddHandler\(\s*{routedEvent}\s*,\s*new\s+{handlerType}\(\s*Dialog_UserActivity\s*\)\s*,\s*handledEventsToo:\s*true\s*\)")
                .Should().BeTrue($"{routedEvent} を、処理済みのものも含めて操作として数える");
        }

        code.Should().NotContain("MouseMoveEvent", "席を離れた後にマウスが触れただけで保持が延びないように、移動は数えない");
        System.Text.RegularExpressions.Regex.IsMatch(code,
                @"void\s+Dialog_UserActivity\([^)]*\)\s*\{\s*_viewModel\.NotifyUserActivity\(\);\s*\}")
            .Should().BeTrue("ハンドラは ViewModel へ操作があったことを知らせるだけ（イベントを処理済みにしない）");
    }
}
