using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// DiagnosticReport の集約ロジックの単体テスト（Issue #1690）
/// </summary>
/// <remarks>
/// 総合判定はダイアログ見出しとクリップボード出力の両方が参照するため、
/// DTO 側の 1 か所で計算する。特に NotApplicable（対象外）を
/// 「正常」に丸めないことが重要で、ローカルモードでは共有フォルダ接続状態が
/// 常に対象外になるため、丸めると診断していない項目を診断済みと誤認させる。
/// </remarks>
public class DiagnosticReportTests
{
    private static DiagnosticItem Item(DiagnosticStatus status) => new()
    {
        Kind = DiagnosticItemKind.DatabaseReachability,
        Title = "テスト項目",
        Status = status,
        SummaryText = "要約",
        DetailText = "詳細"
    };

    private static DiagnosticReport ReportWith(params DiagnosticStatus[] statuses)
    {
        var items = new List<DiagnosticItem>();
        foreach (var s in statuses)
            items.Add(Item(s));
        return new DiagnosticReport { Items = items };
    }

    [Fact]
    public void OverallStatus_WithAllOk_ReturnsOk()
    {
        ReportWith(DiagnosticStatus.Ok, DiagnosticStatus.Ok)
            .OverallStatus.Should().Be(DiagnosticStatus.Ok);
    }

    [Fact]
    public void OverallStatus_WithWarningAmongOk_ReturnsWarning()
    {
        ReportWith(DiagnosticStatus.Ok, DiagnosticStatus.Warning, DiagnosticStatus.Ok)
            .OverallStatus.Should().Be(DiagnosticStatus.Warning);
    }

    [Fact]
    public void OverallStatus_WithErrorAndWarning_ReturnsError()
    {
        // 異常が 1 件でもあれば警告より重い判定を採る
        ReportWith(DiagnosticStatus.Warning, DiagnosticStatus.Error, DiagnosticStatus.Ok)
            .OverallStatus.Should().Be(DiagnosticStatus.Error);
    }

    [Fact]
    public void OverallStatus_IgnoresNotApplicableWhenOtherItemsExist()
    {
        ReportWith(DiagnosticStatus.NotApplicable, DiagnosticStatus.Ok)
            .OverallStatus.Should().Be(DiagnosticStatus.Ok);
    }

    [Fact]
    public void OverallStatus_WithAllNotApplicable_ReturnsNotApplicable()
    {
        // 対象外を「正常」に丸めない。診断していない事実を保つ
        ReportWith(DiagnosticStatus.NotApplicable, DiagnosticStatus.NotApplicable)
            .OverallStatus.Should().Be(DiagnosticStatus.NotApplicable);
    }

    [Fact]
    public void OverallStatus_WithNoItems_ReturnsNotApplicable()
    {
        new DiagnosticReport().OverallStatus.Should().Be(DiagnosticStatus.NotApplicable);
    }

    [Fact]
    public void ProblemCount_CountsWarningAndErrorOnly()
    {
        var report = ReportWith(
            DiagnosticStatus.Ok,
            DiagnosticStatus.Warning,
            DiagnosticStatus.Error,
            DiagnosticStatus.NotApplicable);

        report.ProblemCount.Should().Be(2);
    }

    [Theory]
    [InlineData(DiagnosticStatus.Ok, false)]
    [InlineData(DiagnosticStatus.NotApplicable, false)]
    [InlineData(DiagnosticStatus.Warning, true)]
    [InlineData(DiagnosticStatus.Error, true)]
    public void IsProblem_IsTrueOnlyForWarningAndError(DiagnosticStatus status, bool expected)
    {
        Item(status).IsProblem.Should().Be(expected);
    }

    [Fact]
    public void StatusPresentationProperties_DifferPerStatus()
    {
        // アイコン・ラベル・色キーが判定ごとに異なることで、
        // 色のみに依存しない状態伝達（UI/UX原則）が成立する
        var ok = Item(DiagnosticStatus.Ok);
        var error = Item(DiagnosticStatus.Error);

        ok.StatusIcon.Should().NotBe(error.StatusIcon);
        ok.StatusLabel.Should().Be("正常");
        error.StatusLabel.Should().Be("異常");
        ok.StatusForegroundResourceKey.Should().NotBe(error.StatusForegroundResourceKey);
    }

    [Fact]
    public void StatusForegroundResourceKey_ReturnsResourceKeyNotColorLiteral()
    {
        // 色値リテラル（#RRGGBB）を返さないこと。XAML 側でブラシ解決する方針（Issue #1392、#1461）
        foreach (DiagnosticStatus status in System.Enum.GetValues(typeof(DiagnosticStatus)))
        {
            var key = Item(status).StatusForegroundResourceKey;
            key.Should().NotStartWith("#");
            key.Should().EndWith("Brush");
        }
    }
}
