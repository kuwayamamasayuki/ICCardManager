using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2484: メイン画面・履歴パネルのモーダルが、表示中のカード読み取りの抑制を通ることを静的に固定する。
/// </summary>
/// <remarks>
/// <para>
/// 抑制は、両画面が受け取った <c>INavigationService</c> を <c>ModalCardReadingSuppressingNavigationService</c> で
/// 包むことで掛けている。挙動は <c>MainViewModelTests</c> / <c>HistoryPanelViewModelTests</c> の「Issue #2484」region が
/// 代表のダイアログで表明するが、<b>包みを通らない表示</b>（<c>Window</c> を直接生成して <c>ShowDialog()</c>、
/// <c>MessageBox</c> の直呼び）を足されると、その表示だけ黙って抑制が漏れる。ここではその経路を列挙して塞ぐ。
/// </para>
/// <para>
/// 破れる経路: ①包みを外す（コンストラクタで受け取った値をそのまま保持する） ②<c>Window.ShowDialog()</c> の直呼び
/// ③<c>MessageBox.Show</c> / <c>OwnedMessageBox.Show</c> の直呼び ④<c>ErrorDialogHelper.Show*</c>（MessageBox を出す）。
/// ②は <c>BeginModalCardReadingSuppression</c> の <c>using</c> の内側なら認める（<c>MergeHistoryDialog</c> が該当）。
/// 職員認証（<c>IStaffAuthService</c>）は認証ダイアログ自身が抑制を取る（<c>CardReadingSource.Authentication</c>）ので対象外。
/// </para>
/// </remarks>
public class ModalCardReadingSuppressionConventionTests
{
    private static readonly Regex DirectWindowShowDialog = new(@"\.ShowDialog\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex DirectMessageBox = new(
        @"MessageBox\s*\.\s*Show\s*\(|ErrorDialogHelper\s*\.\s*Show\w*\s*\(", RegexOptions.Compiled);

    private static readonly Regex NavigationAssignment = new(@"\b_navigationService\s*=\s*([^;]+);", RegexOptions.Compiled);

    /// <summary>
    /// 抑制の <c>using</c> の外にある直接のモーダル表示を列挙する（検査の本体。下のサンプル入力で検査自体を固定する）。
    /// </summary>
    internal static IReadOnlyList<string> FindUnsuppressedDirectModals(string codeOnly)
    {
        var scopes = TestSourceInspection.ExtractUsingScopeBodies(codeOnly, "BeginModalCardReadingSuppression");
        var findings = new List<string>();

        foreach (Match match in DirectWindowShowDialog.Matches(codeOnly))
        {
            if (!scopes.Any(s => s.Start <= match.Index && match.Index < s.End))
            {
                findings.Add(Excerpt(codeOnly, match.Index));
            }
        }

        // MessageBox は包みの中（DialogService）で出すもの。画面から直接出す理由が無いので、抑制の内側でも認めない
        foreach (Match match in DirectMessageBox.Matches(codeOnly))
        {
            findings.Add(Excerpt(codeOnly, match.Index));
        }

        return findings;
    }

    private static string Excerpt(string source, int index)
    {
        var start = Math.Max(0, index - 60);
        return source.Substring(start, Math.Min(source.Length - start, 100)).Replace(Environment.NewLine, " ");
    }

    public static IEnumerable<object[]> ScreenSources()
    {
        yield return new object[] { "MainViewModel" };
        yield return new object[] { "HistoryPanelViewModel" };
    }

    private static IReadOnlyList<ProductionSourceFiles.SourceFile> FilesOf(string screen)
        => screen == "MainViewModel" ? MainViewModelSourceFiles.All : HistoryPanelViewModelSourceFiles.All;

    [Theory]
    [MemberData(nameof(ScreenSources))]
    public void 画面が保持するナビゲーションは抑制の包みであること(string screen)
    {
        var files = FilesOf(screen);
        files.Should().NotBeEmpty("前提: 構成ファイルが見つかる");

        var assignments = files
            .SelectMany(f => NavigationAssignment.Matches(f.CodeOnly).Cast<Match>())
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        assignments.Should().ContainSingle("_navigationService へ代入するのはコンストラクタの 1 か所だけ")
            .Which.Should().StartWith("new ModalCardReadingSuppressingNavigationService(",
                "受け取った INavigationService をそのまま保持すると、すべてのモーダルで抑制が漏れる");
    }

    [Theory]
    [MemberData(nameof(ScreenSources))]
    public void 包みを通らないモーダルは抑制のusingの内側にあること(string screen)
    {
        var findings = FilesOf(screen)
            .SelectMany(f => FindUnsuppressedDirectModals(f.CodeOnly).Select(e => $"{f.RelativePath}: {e}"))
            .ToList();

        findings.Should().BeEmpty(
            "INavigationService を通らないモーダルは、表示の範囲を BeginModalCardReadingSuppression の using で囲むこと");
    }

    /// <summary>
    /// 検査自体の固定（実データが違反ゼロでも空振りしていないことを、既知の入力で確かめる。#1786）。
    /// </summary>
    [Fact]
    public void 検査は抑制の外の直接表示を検出し内側は認めること()
    {
        const string violating = "void A() { var d = new X(); d.ShowDialog(); }";
        const string suppressed = "void A() { var d = new X(); using (Host.BeginModalCardReadingSuppression()) { r = d.ShowDialog(); } }";
        const string declared = "void A() { using var s = BeginModalCardReadingSuppression(); d.ShowDialog(); }";
        const string messageBox = "void A() { using (BeginModalCardReadingSuppression()) { OwnedMessageBox.Show(o, m, t, b, i); } }";
        const string errorHelper = "void A() { ErrorDialogHelper.ShowError(ex, t); }";
        const string navigation = "void A() { _navigationService.ShowDialog<X>(); _navigationService.ShowConfirmation(m, t); }";

        FindUnsuppressedDirectModals(violating).Should().HaveCount(1, "抑制の外の ShowDialog() は違反");
        FindUnsuppressedDirectModals(suppressed).Should().BeEmpty("抑制の using の内側は認める");
        FindUnsuppressedDirectModals(declared).Should().BeEmpty("using 宣言の範囲も内側");
        FindUnsuppressedDirectModals(messageBox).Should().HaveCount(1, "MessageBox の直呼びは抑制の内側でも認めない");
        FindUnsuppressedDirectModals(errorHelper).Should().HaveCount(1, "ErrorDialogHelper も MessageBox を出す");
        FindUnsuppressedDirectModals(navigation).Should().BeEmpty("包みを通る表示は対象外");
    }
}
