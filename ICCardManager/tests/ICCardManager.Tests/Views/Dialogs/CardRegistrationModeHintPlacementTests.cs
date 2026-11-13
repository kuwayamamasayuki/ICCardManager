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
        // コメントを除いたコードだけを見る（「呼ばない」と書いたコメントや、コメントアウトした呼び出しで緑にしない）
        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(
            ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", DialogFileName + ".cs"))));
        var constructor = TestSourceInspection.ExtractMethodBody(code, "public CardRegistrationModeDialog(");
        var fit = TestSourceInspection.ExtractMethodBody(code, "private void FitToMonitorWorkArea(");

        constructor.Should().Contain("MaxHeight = WindowLayoutCalculator.FitMaxHeight(MaxHeight, SystemParameters.WorkArea.Height)",
            "Issue #2465: 高さの上限を作業領域の高さにする（XAML の 680 は画面の情報が取れないときの受け皿）");

        // 位置を寄せる処理は、書いてあるだけでは働かない。購読と各処理の本体を、空白・改行の違いを除いて完全一致で照合する
        // （部分一致だと、ハンドラーに位置を寄せる呼び出しを足しても緑になる。レビュー 4 回目で検出）
        constructor.Should().Contain("Loaded += (s, e) => FitToMonitorWorkArea();",
            "表示した時点で、載ったモニターの作業領域へ上限と位置を合わせる（CenterOwner は画面内へ寄せない）");
        constructor.Should().Contain("SizeChanged += (s, e) => ApplyWorkAreaFit();",
            "大きさが変わったときは、表示前・操作中・最大化を判断してから合わせる");
        constructor.Should().Contain("AddHook(TrackMoveOrSize)", "移動・リサイズの操作の始まりと終わりを追うフックを付けること");
        foreach (var subscription in new[] { "Loaded +=", "SizeChanged +=", "LocationChanged +=" })
        {
            CountOccurrences(code, subscription).Should().Be(1, $"「{subscription}」の購読は 1 つだけ（別の購読から位置を寄せさせない）");
        }

        CollapseWhitespace(TestSourceInspection.ExtractMethodBody(constructor, "LocationChanged +=")).Should().Be(
            "{ if (IsLoaded) { FitMaxHeightToMonitorWorkArea(); } }",
            "別のモニターへ動かしたときは上限だけを合わせ直す（位置まで寄せるとドラッグと競合する）");
        CollapseWhitespace(TestSourceInspection.ExtractMethodBody(code, "private void ApplyWorkAreaFit(")).Should().Be(
            "{ switch (WindowLayoutCalculator.DecideFitOnSizeChanged(IsLoaded, _isMovingOrSizing, WindowState == WindowState.Normal)) " +
            "{ case WorkAreaFit.MaxHeightAndPosition: FitToMonitorWorkArea(); break; " +
            "case WorkAreaFit.MaxHeightOnly: FitMaxHeightToMonitorWorkArea(); break; } }",
            "判断（DecideFitOnSizeChanged。単体テストで固定）の結果だけで、上限と位置か上限だけかを決める — 操作中に位置まで寄せると" +
            "ドラッグ中のウィンドウが跳ね、最大化中に寄せると元に戻したときに画面の端へ動く");
        CollapseWhitespace(TestSourceInspection.ExtractMethodBody(code, "private IntPtr TrackMoveOrSize(")).Should().Be(
            "{ if (msg == WmEnterSizeMove) { _isMovingOrSizing = true; } " +
            "else if (msg == WmExitSizeMove) { _isMovingOrSizing = false; ApplyWorkAreaFit(); } return IntPtr.Zero; }",
            "操作の始まりで操作中にし、終わりで解いて 1 回だけ合わせる（最大化中は位置を寄せない判断を通す）");
        code.Should().Contain("private const int WmEnterSizeMove = 0x0231;").And.Contain("private const int WmExitSizeMove = 0x0232;",
            "WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE の値");
        CollapseWhitespace(TestSourceInspection.ExtractMethodBody(code, "private void FitMaxHeightToMonitorWorkArea(")).Should().Be(
            "{ if (MonitorWorkArea.Of(this) is Rect workArea) { MaxHeight = WindowLayoutCalculator.FitMaxHeight(MaxHeight, workArea.Height); } }",
            "上限だけを合わせる処理は位置を動かさない");

        // 位置を寄せる処理を呼ぶのは、宣言・表示時（Loaded）・判断が「上限と位置」のときの 3 か所だけ
        CountOccurrences(code, "FitToMonitorWorkArea(").Should().Be(3,
            "位置を寄せる処理（FitToMonitorWorkArea）は、宣言・Loaded・ApplyWorkAreaFit の「上限と位置」の分岐の 3 か所にだけ現れる");

        fit.Should().Contain("MaxHeight = WindowLayoutCalculator.FitMaxHeight(MaxHeight, workArea.Height)",
            "載ったモニターの作業領域の高さを上限にする（表示前はプライマリモニターの値しか取れない）");
        fit.Should().Contain("Top = WindowLayoutCalculator.ClampStart(Top, ActualHeight, workArea.Top, workArea.Height)",
            "上限を広げたウィンドウの下端（OK・キャンセル）が画面の外へ出ないよう、位置を作業領域の中へ寄せる");
        fit.Should().Contain("Left = WindowLayoutCalculator.ClampStart(Left, ActualWidth, workArea.Left, workArea.Width)",
            "横方向も CarryoverDataLossDialog と同じく作業領域の中へ寄せる");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string CollapseWhitespace(string code) => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

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
