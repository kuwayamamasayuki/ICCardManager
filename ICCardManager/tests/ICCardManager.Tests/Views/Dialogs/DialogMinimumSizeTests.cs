using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Dialogs;

/// <summary>
/// Issue #1280: 全ダイアログに <c>MinHeight</c> / <c>MinWidth</c> が設定されていることを
/// 静的解析で検証する回帰テスト。低解像度環境（1366×768 ノート PC 等）で OK/キャンセル
/// ボタンが画面外に隠れてキーボード操作ユーザーが到達できなくなる事故を防ぐ。
/// </summary>
/// <remarks>
/// <para>
/// 対象は <c>Views/</c> 配下の XAML のうち、ルート要素が <c>Window</c> で <c>Views/Dialogs/</c> に置かれ、
/// ユーザーがリサイズできるもの（<see cref="IsUserResizable"/>）。対象は手で列挙せず**導出**する。
/// 手書きの一覧だった頃は、一覧の作成後に追加された 7 ダイアログ（AdminDashboard / CarryoverDataLoss /
/// CompanionCountInput / ConnectionDiagnostics / ReportPreflight / SystemLend / TransferStationGroup）が
/// 検査から静かに漏れていた（Issue #2102。<c>.claude/rules/development-conventions.md</c> #1786）。
/// </para>
/// <para>
/// 属性は **Window のルート要素の開始タグだけ**から読む。ファイル全体に正規表現を掛けていた頃は、
/// Window 以外の要素の値でも合格した（<c>OperationLogDialog.xaml</c> の Window から <c>MinWidth</c> を消しても、
/// コンボボックスの <c>MinWidth="100"</c> に一致して緑のままだった。Issue #2102）。
/// 照合の前に XML コメントを除去する（#1692 の極性の反転）。
/// </para>
/// <para>
/// ランタイム動作（実際にウィンドウを小さくリサイズして操作可能か）は UI 自動化が
/// 必要なため、PR のテストプランで手動検証する。
/// </para>
/// </remarks>
public class DialogMinimumSizeTests
{
    /// <summary>1366×768 画面でタスクバーを除いた実用の高さ。</summary>
    private const int MaxMinHeight = 720;

    /// <summary>1366 横画面に収まる最小幅の上限。</summary>
    private const int MaxMinWidth = 1300;

    /// <summary>ダイアログを置くディレクトリ（<c>Views/</c> からの相対。区切りは <c>/</c>）。</summary>
    private const string DialogsFolder = "Dialogs/";

    private static readonly string ViewsDirectory = ViewSourceLocator.ResolveDirectory("Views");

    /// <summary>
    /// 最小サイズを要求するダイアログ（<c>Views/</c> からの相対パス）。<see cref="EnumerateWindowRoots"/> から導出する。
    /// </summary>
    public static TheoryData<string> AllDialogs
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var dialog in EnumerateDialogsRequiringMinimumSize())
            {
                data.Add(dialog);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllDialogs))]
    public void All_dialogs_should_declare_MinHeight(string relativePath)
    {
        var minHeight = GetAttribute(ReadRootStartTag(relativePath), "MinHeight");
        minHeight.Should().MatchRegex(@"^[0-9]+$",
            $"{relativePath}: 低解像度環境でボタンが隠れないよう、Window のルート要素に MinHeight を数値で明示すべき");
    }

    [Theory]
    [MemberData(nameof(AllDialogs))]
    public void All_dialogs_should_declare_MinWidth(string relativePath)
    {
        var minWidth = GetAttribute(ReadRootStartTag(relativePath), "MinWidth");
        minWidth.Should().MatchRegex(@"^[0-9]+$",
            $"{relativePath}: 横方向にも下限サイズを設定し横スクロール暴走を防ぐため、Window のルート要素に MinWidth を数値で明示すべき");
    }

    /// <summary>
    /// MinHeight は 1366×768 の利用可能高さ（タスクバーを除いた約 728px）を超えない値にする。
    /// 超過するとウィンドウが画面上下にはみ出し、タイトルバーやボタンに到達できなくなる。
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDialogs))]
    public void MinHeight_should_fit_on_1366x768_screen(string relativePath)
    {
        var value = GetAttribute(ReadRootStartTag(relativePath), "MinHeight");
        int.TryParse(value, out var minHeight).Should().BeTrue($"{relativePath} の Window に数値の MinHeight が必要");

        minHeight.Should().BeLessThanOrEqualTo(MaxMinHeight,
            $"{relativePath}: MinHeight={minHeight} は 1366×768 画面（タスクバー考慮で実用 {MaxMinHeight}px）を超えている。" +
            "ダイアログ本体をリサイズ可能にし ScrollViewer で囲むなどの対応を検討。");
    }

    [Theory]
    [MemberData(nameof(AllDialogs))]
    public void MinWidth_should_fit_on_1366x768_screen(string relativePath)
    {
        var value = GetAttribute(ReadRootStartTag(relativePath), "MinWidth");
        int.TryParse(value, out var minWidth).Should().BeTrue($"{relativePath} の Window に数値の MinWidth が必要");

        minWidth.Should().BeLessThanOrEqualTo(MaxMinWidth,
            $"{relativePath}: MinWidth={minWidth} は 1366 横画面を超える可能性がある");
    }

    /// <summary>
    /// 導出が空振りしていないこと、かつ手書きの一覧で漏れていたダイアログが対象に入っていること。
    /// </summary>
    /// <remarks>
    /// 導出が 0 件に縮むと、上の Theory はすべて「ケースなし」で何も検査しなくなる
    /// （testing.md「ガードの検出漏れは緑になる」）。
    /// </remarks>
    [Fact]
    public void 導出した対象に既知のダイアログと一覧から漏れていたダイアログが含まれること()
    {
        var dialogs = EnumerateDialogsRequiringMinimumSize().ToList();

        dialogs.Should().Contain(new[]
            {
                "Dialogs/OperationLogDialog.xaml",
                "Dialogs/StaffAuthDialog.xaml",
                // 手書きの一覧から漏れていた 7 ダイアログ（Issue #2102）
                "Dialogs/AdminDashboardDialog.xaml",
                "Dialogs/CarryoverDataLossDialog.xaml",
                "Dialogs/CompanionCountInputDialog.xaml",
                "Dialogs/ConnectionDiagnosticsDialog.xaml",
                "Dialogs/ReportPreflightDialog.xaml",
                "Dialogs/SystemLendDialog.xaml",
                "Dialogs/TransferStationGroupDialog.xaml",
            },
            "Views/Dialogs 配下の Window は、手で登録しなくても最小サイズの検査対象に入るべき");

        dialogs.Should().HaveCountGreaterThanOrEqualTo(24,
            "Issue #2102 時点で Views/Dialogs には最小サイズを要する Window が 24 個ある。件数が縮んだら導出の誤りを疑うこと");
    }

    /// <summary>
    /// <c>Views/Dialogs/</c> の外にある Window は、ダイアログではない既知のウィンドウだけであること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 対象の導出は「ダイアログは <c>Views/Dialogs/</c> に置く」という配置の規約に依っている。
    /// ダイアログを別の場所へ置くと導出から静かに漏れるため、外にある Window を閉じた集合で表明して
    /// **fail-closed** にする（新しい Window を外に置いた時点で赤になり、判断を強制する）。
    /// </para>
    /// <list type="bullet">
    /// <item><c>MainWindow.xaml</c>: 主ウィンドウ。最小幅は文字サイズに追随する
    /// <c>{DynamicResource WindowMinWidth}</c>（既定 1400）で、1366×768 のダイアログ規約の対象外</item>
    /// <item><c>ToastNotificationWindow.xaml</c>: 通知。<c>ResizeMode="NoResize"</c> で、
    /// <see cref="IsUserResizable"/> の規則でも除外される</item>
    /// </list>
    /// </remarks>
    [Fact]
    public void Dialogsフォルダーの外にあるWindowはダイアログではない既知のウィンドウだけであること()
    {
        var outside = EnumerateWindowRoots()
            .Where(w => !w.RelativePath.StartsWith(DialogsFolder, StringComparison.Ordinal))
            .Select(w => w.RelativePath)
            .ToList();

        outside.Should().BeEquivalentTo(new[] { "MainWindow.xaml", "ToastNotificationWindow.xaml" },
            "ダイアログは Views/Dialogs に置くこと（最小サイズの検査対象はこの配置から導出している）。" +
            "ダイアログではない Window を新設した場合は、最小サイズが不要な理由を確かめてから本テストの集合へ加えること");
    }

    /// <summary>
    /// <c>Views/Dialogs/</c> のうちリサイズできない（最小サイズの検査から除外される）ダイアログは、閉じた集合であること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 除外規則（<see cref="IsUserResizable"/>）は「リサイズできない Window には最小サイズが働かない」という正しい理由で
    /// 対象を絞るが、それは同時に<b>迂回経路</b>でもある — <c>ResizeMode="NoResize"</c> を足すだけで、
    /// 1366×768 に収まらない高さのダイアログが最小サイズの検査（<c>MinHeight ≦ 720</c>）ごと対象外になる
    /// （Issue #2102 のコードレビュー）。リサイズできないダイアログはむしろ、利用者が縮めて逃がす手段も無い。
    /// </para>
    /// <para>
    /// 代わりに「リサイズできない Window は <c>Height ≦ 720</c> を検査する」形は採らない。リサイズできない Window は
    /// <c>SizeToContent</c> で高さが内容から決まることが多く（<c>ToastNotificationWindow.xaml</c> がそう）、
    /// 静的に読める <c>Height</c> が無いと検査が黙って空振りする（fail-open）。閉じた集合で表明すれば、
    /// 新しくリサイズできないダイアログを作った時点で赤になり、高さが画面に収まるかの判断を強制できる（fail-closed）。
    /// 現状は該当するダイアログが無い。
    /// </para>
    /// </remarks>
    [Fact]
    public void Dialogsフォルダーのリサイズできないダイアログは閉じた集合であること()
    {
        var notResizable = EnumerateWindowRoots()
            .Where(w => w.RelativePath.StartsWith(DialogsFolder, StringComparison.Ordinal))
            .Where(w => !IsUserResizable(w.RootStartTag))
            .Select(w => w.RelativePath)
            .ToList();

        notResizable.Should().BeEmpty(
            "リサイズできないダイアログは最小サイズの検査から外れるうえ、利用者が縮めて画面に収める手段も無い。" +
            "ResizeMode を CanResize に戻すか、1366×768（実用の高さ 720px）に収まることを確かめてから本テストの集合へ加えること");
    }

    /// <summary>
    /// 最小サイズの除外規則（ユーザーがリサイズできない Window は最小サイズが働かない）を合成入力で固定する。
    /// </summary>
    /// <remarks>
    /// <c>ResizeMode</c> の既定値は <c>CanResize</c>。<c>CanMinimize</c> は最小化だけを許し、枠のドラッグによる
    /// リサイズはできない。実データが全件リサイズ可能でも、規則自体の誤りをここで検出する（#1786）。
    /// </remarks>
    [Theory]
    [InlineData("<Window ResizeMode=\"NoResize\">", false)]
    [InlineData("<Window ResizeMode=\"CanMinimize\">", false)]
    [InlineData("<Window ResizeMode=\"CanResize\">", true)]
    [InlineData("<Window ResizeMode=\"CanResizeWithGrip\">", true)]
    [InlineData("<Window Title=\"x\">", true)]
    public void リサイズ可否の判定が合成入力で固定されていること(string rootStartTag, bool expected)
    {
        IsUserResizable(rootStartTag).Should().Be(expected, $"入力: {rootStartTag}");
    }

    /// <summary>
    /// 属性を Window のルート要素だけから読むことを合成入力で固定する（Issue #2102）。
    /// </summary>
    [Theory]
    // ルートに無く子要素にだけある → 見つからない（旧実装はファイル全体に一致して合格していた）
    [InlineData("<Window Title=\"x\"><ComboBox MinWidth=\"100\"/></Window>", null)]
    // コメントの中の記述 → 見つからない
    [InlineData("<!-- <Window MinWidth=\"640\"> -->\n<Window Title=\"x\"/>", null)]
    // XML 宣言の後ろのルート → 見つかる
    [InlineData("<?xml version=\"1.0\"?>\n<Window\n    MinWidth=\"640\"><Grid MinWidth=\"100\"/></Window>", "640")]
    // 単引用符の属性（XAML では合法）
    [InlineData("<Window MinWidth='640'/>", "640")]
    public void 最小幅はWindowのルート要素の開始タグだけから読むこと(string xaml, string? expected)
    {
        GetAttribute(GetRootStartTag(xaml), "MinWidth").Should().Be(expected, $"入力: {xaml.Replace("\n", "\\n")}");
    }

    /// <summary>
    /// 低解像度でもボタンに到達できるよう、LedgerRowEditDialog 等の複雑フォームは
    /// ScrollViewer で囲まれていること。Issue #1280 の推奨改善項目。
    /// </summary>
    [Theory]
    [InlineData("LedgerRowEditDialog.xaml")]
    [InlineData("SettingsDialog.xaml")]
    [InlineData("DataExportImportDialog.xaml")]
    public void Dialogs_with_many_inputs_should_wrap_content_in_ScrollViewer(string xamlFileName)
    {
        var xaml = XamlElementInspection.StripXmlComments(ReadView(DialogsFolder + xamlFileName));
        XamlElementInspection.EnumerateElements(xaml, "ScrollViewer").Should().NotBeEmpty(
            $"{xamlFileName}: 入力フォームが多いダイアログは ScrollViewer で囲み、" +
            "リサイズ・低解像度でも縦スクロールで全項目にアクセス可能にすべき");
    }

    /// <summary>
    /// Issue #1280 の修正対象: LedgerRowEditDialog が ResizeMode=CanResize になっていること。
    /// </summary>
    [Fact]
    public void LedgerRowEditDialog_should_be_resizable()
    {
        GetAttribute(ReadRootStartTag(DialogsFolder + "LedgerRowEditDialog.xaml"), "ResizeMode").Should().Be("CanResize",
            "低解像度環境でユーザーがリサイズしてボタンにアクセスできるよう、NoResize から CanResize へ変更");
    }

    [Fact]
    public void SettingsDialog_should_be_resizable()
    {
        GetAttribute(ReadRootStartTag(DialogsFolder + "SettingsDialog.xaml"), "ResizeMode").Should().Be("CanResize");
    }

    /// <summary>
    /// ユーザーが枠をドラッグしてリサイズできるか。できない Window では最小サイズが働かないため、検査対象から除く。
    /// </summary>
    private static bool IsUserResizable(string rootStartTag)
    {
        var resizeMode = GetAttribute(rootStartTag, "ResizeMode");
        return resizeMode is null or "CanResize" or "CanResizeWithGrip";
    }

    private static IEnumerable<string> EnumerateDialogsRequiringMinimumSize()
        => EnumerateWindowRoots()
            .Where(w => w.RelativePath.StartsWith(DialogsFolder, StringComparison.Ordinal))
            .Where(w => IsUserResizable(w.RootStartTag))
            .Select(w => w.RelativePath);

    /// <summary>
    /// <c>Views/</c> 配下の XAML のうち、ルート要素が <c>Window</c> のものを列挙する。
    /// </summary>
    private static IEnumerable<(string RelativePath, string RootStartTag)> EnumerateWindowRoots()
    {
        foreach (var path in Directory.EnumerateFiles(ViewsDirectory, "*.xaml", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var rootStartTag = GetRootStartTag(File.ReadAllText(path));
            if (rootStartTag != null && Regex.IsMatch(rootStartTag, @"^<Window[\s/>]"))
            {
                var relativePath = path.Substring(ViewsDirectory.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                yield return (relativePath, rootStartTag);
            }
        }
    }

    private static string? GetRootStartTag(string xaml) => XamlElementInspection.GetRootStartTag(xaml);

    private static string ReadRootStartTag(string relativePath)
    {
        var rootStartTag = GetRootStartTag(ReadView(relativePath));
        rootStartTag.Should().NotBeNull($"{relativePath} にルート要素があるべき");
        return rootStartTag!;
    }

    private static string? GetAttribute(string? startTag, string attributeName)
        => startTag == null ? null : XamlElementInspection.GetAttribute(startTag, attributeName);

    private static string ReadView(string relativePath)
    {
        var path = Path.Combine(ViewsDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"{relativePath} が Views 配下に存在すべき");
        return File.ReadAllText(path);
    }
}
