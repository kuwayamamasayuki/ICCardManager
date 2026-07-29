using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// DiagnosticReportFormatter の単体テスト（Issue #1690）
/// </summary>
/// <remarks>
/// 出力テキストは IT 担当への障害報告そのものになる。
/// 「どの PC の・いつの・どの環境の診断か」がヘッダーだけで判別できること、
/// 対処が必要な項目の理由と対処方法が本文に含まれることを固定する。
/// </remarks>
public class DiagnosticReportFormatterTests
{
    private static DiagnosticReport BuildReport(params DiagnosticItem[] items) => new()
    {
        DiagnosedAt = new DateTime(2026, 7, 28, 14, 30, 15),
        AppVersion = "2.11.0",
        MachineName = "SOMU-PC01",
        OsDescription = "Microsoft Windows NT 10.0.22631.0",
        DatabasePath = @"\\fileserver\share\iccard.db",
        IsSharedMode = true,
        Items = new List<DiagnosticItem>(items)
    };

    private static DiagnosticItem OkItem() => new()
    {
        Kind = DiagnosticItemKind.DatabaseReachability,
        Title = "データベース到達性",
        Status = DiagnosticStatus.Ok,
        SummaryText = "接続できます",
        DetailText = "データベースファイルへ読み取りアクセスできています。"
    };

    private static DiagnosticItem ErrorItem() => new()
    {
        Kind = DiagnosticItemKind.CardReader,
        Title = "ICカードリーダー",
        Status = DiagnosticStatus.Error,
        SummaryText = "接続されていません",
        DetailText = "ICカードリーダーが認識されていません。USB ケーブルが抜けている可能性があります。" +
                     "PaSoRi を USB ポートに挿し直してください。"
    };

    [Fact]
    public void Format_IncludesSystemNameAndDiagnosedAt()
    {
        var text = DiagnosticReportFormatter.Format(BuildReport(OkItem()));

        text.Should().Contain(AppConstants.SystemName);
        text.Should().Contain("2026-07-28 14:30:15");
    }

    [Fact]
    public void Format_IncludesEnvironmentInformation()
    {
        // IT 担当が最初から正確な状況を把握できることが Issue #1690 の目的
        var text = DiagnosticReportFormatter.Format(BuildReport(OkItem()));

        text.Should().Contain("2.11.0");
        text.Should().Contain("SOMU-PC01");
        text.Should().Contain("Microsoft Windows NT 10.0.22631.0");
        text.Should().Contain(@"\\fileserver\share\iccard.db");
        text.Should().Contain("共有モード");
    }

    [Fact]
    public void Format_WithLocalMode_LabelsLocalMode()
    {
        var report = BuildReport(OkItem());
        report.IsSharedMode = false;

        DiagnosticReportFormatter.Format(report).Should().Contain("ローカルモード");
    }

    [Fact]
    public void Format_IncludesOverallStatusAndProblemCount()
    {
        var text = DiagnosticReportFormatter.Format(BuildReport(OkItem(), ErrorItem()));

        text.Should().Contain("総合判定");
        text.Should().Contain(DiagnosticStatusPresenter.GetOverallSummary(DiagnosticStatus.Error));
        text.Should().Contain("1件");
    }

    [Fact]
    public void Format_WithNoProblems_OmitsProblemCount()
    {
        var text = DiagnosticReportFormatter.Format(BuildReport(OkItem()));

        text.Should().Contain(DiagnosticStatusPresenter.GetOverallSummary(DiagnosticStatus.Ok));
        text.Should().NotContain("対処が必要な項目");
    }

    [Fact]
    public void Format_ListsEveryItemWithStatusLabelAndSummary()
    {
        var text = DiagnosticReportFormatter.Format(BuildReport(OkItem(), ErrorItem()));

        text.Should().Contain("[正常] データベース到達性: 接続できます");
        text.Should().Contain("[異常] ICカードリーダー: 接続されていません");
    }

    [Fact]
    public void Format_IncludesDetailTextForProblemItems()
    {
        var text = DiagnosticReportFormatter.Format(BuildReport(ErrorItem()));

        text.Should().Contain("PaSoRi を USB ポートに挿し直してください。");
    }

    [Fact]
    public void Format_OmitsDetailTextForNormalItems()
    {
        // 正常項目の詳細まで載せると、対処すべき箇所が埋もれて報告の価値が下がる
        var text = DiagnosticReportFormatter.Format(BuildReport(OkItem()));

        text.Should().NotContain("データベースファイルへ読み取りアクセスできています。");
    }

    [Fact]
    public void Format_IndentsMultiLineDetailText()
    {
        var item = ErrorItem();
        item.DetailText = "1行目です。" + Environment.NewLine + "2行目です。";

        var lines = DiagnosticReportFormatter.Format(BuildReport(item))
            .Split(new[] { Environment.NewLine }, StringSplitOptions.None);

        lines.Should().Contain(l => l.StartsWith("    ") && l.Contains("1行目です。"));
        lines.Should().Contain(l => l.StartsWith("    ") && l.Contains("2行目です。"));
    }

    [Fact]
    public void Format_WithNullReport_ReturnsActionableMessage()
    {
        var text = DiagnosticReportFormatter.Format(null);

        text.Should().NotBeNullOrWhiteSpace();
        text.Should().EndWith("してください。");
    }

    [Fact]
    public void Format_WithMissingEnvironmentValues_ShowsUnknownInsteadOfBlank()
    {
        // 空欄だと報告先が「情報の欠落」と「未設定」を区別できない
        var report = new DiagnosticReport
        {
            DiagnosedAt = new DateTime(2026, 7, 28),
            Items = new List<DiagnosticItem> { OkItem() }
        };

        var text = DiagnosticReportFormatter.Format(report);

        text.Should().Contain("PC名: 不明");
        text.Should().Contain("アプリバージョン: 不明");
    }

    [Fact]
    public void Format_WithNoItems_StatesThereAreNoItems()
    {
        var report = new DiagnosticReport { DiagnosedAt = new DateTime(2026, 7, 28) };

        DiagnosticReportFormatter.Format(report).Should().Contain("診断項目がありません。");
    }

    [Fact]
    public void Format_WithNullItemInList_SkipsItWithoutThrowing()
    {
        var report = BuildReport(OkItem());
        report.Items = new List<DiagnosticItem> { OkItem(), null };

        var act = () => DiagnosticReportFormatter.Format(report);

        act.Should().NotThrow();
    }
}
