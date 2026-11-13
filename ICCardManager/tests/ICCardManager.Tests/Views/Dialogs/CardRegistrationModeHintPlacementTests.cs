using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #2465: カード登録方法の選択ダイアログのヒント（管理者マニュアル「1.6」への案内）が、
/// 繰越の入力欄に押し出されてスクロールの外に隠れない配置であることを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 是正前はヒントが内容の最下端（入力欄の下）にあり、繰越の入力欄（繰越額・累計受入・累計払出）を含む内容が
/// 高さの上限（MaxHeight 680）を超えるため、文字サイズ「中」でもヒントの一部がスクロールの外に隠れていた。
/// ヒントはフォーカスを受けないので、Tab で入力欄を移ってもスクロール領域が表示範囲へ寄せることはなく、読まれない。
/// </para>
/// <para>
/// 実際の画面での包含（ヒントの矩形がスクロール領域の表示範囲に収まること）は UI テスト
/// <c>CardRegistrationHintVisibilityUiTests</c> が文字サイズ「中」「大」「特大」で確かめる。ここでは XAML の構造
/// （ヒントの行が入力欄の行より上）と、高さの上限を作業領域へ合わせるコードビハインドの呼び出しを固定する。
/// **回帰は「ヒントが入力欄より上にある」と「ヒントがマニュアルへの案内を持っている」を対で置く** — 前者だけだと、
/// ヒントを丸ごと消した実装（行の照合が空振りする）でも緑になり得る。
/// </para>
/// </remarks>
public class CardRegistrationModeHintPlacementTests
{
    private const string DialogFileName = "CardRegistrationModeDialog.xaml";

    [Fact]
    public void ヒントが繰越の入力欄より上の行にあること()
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadDialog(DialogFileName));

        var hintRow = GridRowOfOutermostBorderContaining(xaml, "x:Name=\"RegistrationHintText\"");
        var newPurchaseRow = GridRowOfOutermostBorderContaining(xaml, "x:Name=\"NewPurchaseRadio\"");
        var carryoverRow = GridRowOfOutermostBorderContaining(xaml, "x:Name=\"CarryoverOptionsPanel\"");

        hintRow.Should().BeLessThan(newPurchaseRow,
            "Issue #2465: ヒントは選択肢・入力欄より上に置く（下にあると繰越の入力欄に押し出されてスクロールの外に隠れ、" +
            "フォーカスを受けないので Tab で移っても表示範囲へ寄せられない）");
        hintRow.Should().BeLessThan(carryoverRow,
            "Issue #2465: ヒントは繰越の入力欄（繰越額・累計受入・累計払出）より上に置く");
    }

    /// <summary>
    /// 対の表明: ヒントがマニュアルへの案内を持ち、スクロール領域の中にあること（消しただけ・固定領域へ追い出しただけの実装を止める）。
    /// </summary>
    [Fact]
    public void ヒントがマニュアルへの案内を持ちスクロール領域の中にあること()
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadDialog(DialogFileName));

        var hint = XamlElementInspection.EnumerateElements(xaml, "TextBlock")
            .SingleOrDefault(t => t.StartTag.IndexOf("x:Name=\"RegistrationHintText\"", StringComparison.Ordinal) >= 0);
        hint.Should().NotBeNull("ヒントの TextBlock（x:Name=RegistrationHintText。UI テストが AutomationId で探す）があること");
        hint!.Body.Should().Contain("管理者マニュアル", "ヒントは管理者マニュアルの参照先を案内する");
        hint.Body.Should().Contain("1.6 交通系ICカードを登録する", "参照先の節の見出し（管理者マニュアル §1.6）を示す");

        XamlElementInspection.EnumerateElements(xaml, "ScrollViewer")
            .Should().Contain(sv => sv.Body.IndexOf("x:Name=\"RegistrationHintText\"", StringComparison.Ordinal) >= 0,
                "ヒントは内容のスクロール領域の中に置く（ボタン行の上の固定領域に置くと、文字サイズ「特大」の狭い画面で入力欄の表示範囲を常に削る）");
    }

    /// <summary>
    /// 高さの上限（XAML の MaxHeight 680）を、コードビハインドが作業領域の高さへ置き換えていること。
    /// </summary>
    /// <remarks>
    /// 広い画面では繰越の入力欄までスクロールせずに収め、狭い画面では画面の外へはみ出さない。
    /// 上限を広げるとオーナーの中央に置いたウィンドウの下端が画面の外へ出得るので、位置を作業領域へ寄せる呼び出しも対で見る。
    /// </remarks>
    [Fact]
    public void 高さの上限を作業領域に合わせ位置を作業領域へ寄せていること()
    {
        var codeBehind = File.ReadAllText(
            ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", DialogFileName + ".cs")));

        codeBehind.Should().Contain("MaxHeight = WindowLayoutCalculator.FitMaxHeight(MaxHeight, SystemParameters.WorkArea.Height)",
            "Issue #2465: 高さの上限を作業領域の高さにする（XAML の 680 は画面の情報が取れないときの受け皿）");
        codeBehind.Should().Contain("Top = WindowLayoutCalculator.ClampStart(Top, ActualHeight, workArea.Top, workArea.Height)",
            "上限を広げたウィンドウの下端（OK・キャンセル）が画面の外へ出ないよう、位置を作業領域の中へ寄せる");
    }

    private static int GridRowOfOutermostBorderContaining(string xaml, string marker)
    {
        var border = XamlElementInspection.EnumerateElements(xaml, "Border")
            .Where(b => b.Body.IndexOf(marker, StringComparison.Ordinal) >= 0
                        && XamlElementInspection.GetAttribute(b.StartTag, "Grid.Row") != null)
            .OrderByDescending(b => b.Body.Length)
            .FirstOrDefault();
        border.Should().NotBeNull($"前提: 「{marker}」を含み Grid.Row を持つ Border があること");

        return int.Parse(XamlElementInspection.GetAttribute(border!.StartTag, "Grid.Row")!, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadDialog(string fileName)
        => File.ReadAllText(ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", fileName)));
}
