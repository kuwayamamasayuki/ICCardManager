using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2467: 職員管理ダイアログの「登録済みの職員が 1 人だけ」の注意が、画面に出る形で置かれていることの検証
/// </summary>
/// <remarks>
/// 表示する条件は <c>StaffManageViewModelSingleStaffNoticeTests</c> が固定する。ViewModel のテストはプロパティの値しか見ないため、
/// 表示領域が束縛されていない・編集フォームの内側にあって畳まれる、といった View 側の欠落は本クラスで見る（#1727 と同じ分担）。
/// </remarks>
public class StaffManageDialogSingleStaffNoticeLayoutTests
{
    private static readonly string StaffManageDialogXamlPath =
        ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "StaffManageDialog.xaml"));

    private static string ReadXaml()
        => XamlElementInspection.StripXmlComments(File.ReadAllText(StaffManageDialogXamlPath));

    private static XamlElementInspection.XamlElementSpan FindNotice(string xaml)
    {
        var notices = XamlElementInspection.EnumerateElementsIncludingNested(xaml, "Border")
            .Where(b => XamlElementInspection.GetAttribute(b.StartTag, "x:Name") == "SingleStaffNotice")
            .ToList();
        notices.Should().ContainSingle("注意の枠（x:Name=\"SingleStaffNotice\"）は 1 つだけ置く");
        return notices[0];
    }

    [Fact]
    public void 注意の枠は人数の判定に束縛した表示切替を持つこと()
    {
        var notice = FindNotice(ReadXaml());
        var visibility = XamlElementInspection.GetAttribute(notice.StartTag, "Visibility");

        XamlElementInspection.GetBindingPropertyName(visibility).Should().Be("IsSingleStaffNoticeVisible",
            "1 人だけのときにだけ出す（0 人・2 人以上では出さない）");
        visibility.Should().Contain("BoolToVisibilityConverter",
            "IsSingleStaffNoticeVisible は bool なので、bool を受けるコンバーターで表示を切り替える（#2369）");
    }

    [Fact]
    public void 注意の文言はアイコンと折り返しと読み上げの名前を持つこと()
    {
        var notice = FindNotice(ReadXaml());
        var textBlock = XamlElementInspection.EnumerateStartTags(notice.Body)
            .Single(t => t.StartTag.StartsWith("<TextBlock", System.StringComparison.Ordinal));

        XamlElementInspection.GetAttribute(textBlock.StartTag, "TextWrapping").Should().Be("Wrap",
            "文字サイズを大きくしても切れないように折り返す");
        XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(textBlock.StartTag, "AutomationProperties.Name"))
            .Should().Be("SingleStaffNoticeText", "スクリーンリーダーにも文言を読み上げさせる");

        var runs = XamlElementInspection.EnumerateStartTags(notice.Body)
            .Where(t => t.StartTag.StartsWith("<Run", System.StringComparison.Ordinal))
            .Select(t => XamlElementInspection.GetAttribute(t.StartTag, "Text"))
            .ToList();
        runs.Should().Contain(text => text != null && text.Contains("⚠"), "色だけに頼らずアイコンで伝える");
        runs.Select(XamlElementInspection.GetBindingPropertyName)
            .Should().Contain("SingleStaffNoticeText", "表示する文言は ViewModel の文言を束縛する");
    }

    [Fact]
    public void 注意の枠は編集フォームの内側に置かないこと()
    {
        var xaml = ReadXaml();
        var notice = FindNotice(xaml);

        XamlElementInspection.EnumerateEnclosingElements(xaml, notice.Start)
            .Select(e => XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(e.StartTag, "Visibility")))
            .Should().NotContain(new[] { "IsEditing", "IsNewStaff" },
                "注意は一覧を見ている間（編集していないとき）にも出す必要がある");
    }
}
