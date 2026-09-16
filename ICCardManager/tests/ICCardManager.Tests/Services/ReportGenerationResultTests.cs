using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// 帳票作成結果の状態表現の単体テスト（Issue #2042）
/// </summary>
/// <remarks>
/// <para>
/// 旧実装は結末を <c>Success</c> と <c>Skipped</c> の 2 つの bool で持っており、作成対象外の月が
/// <c>Success = true, Skipped = true</c> という<b>両方が立った状態</b>になっていた。
/// <c>Success</c> だけを見る呼び出し側（一括作成ループ）はこれを「作成した」と数え、
/// 存在しないファイルのパスを作成ファイル一覧へ並べていた。
/// </para>
/// <para>
/// また、一括作成の中断は <c>ErrorMessage.Contains("テンプレート")</c> という文言の部分一致で
/// 判断していた。ここでは「結末は 1 つの値から導かれること」と「共通原因の失敗が型で表されること」を
/// 固定する。ループ側の挙動は <c>ReportViewModelBulkCreationTests</c> が受け持つ。
/// </para>
/// </remarks>
public class ReportGenerationResultTests
{
    #region 結末の表現

    /// <summary>
    /// 欠陥を突く側: 作成対象外は「エラーではない」が「作成した」ではないこと
    /// </summary>
    [Fact]
    public void SkippedResult_エラーではないが作成もしていないこと()
    {
        var result = ReportGenerationResult.SkippedResult("新規購入（2026/07）より前の月です");

        result.Skipped.Should().BeTrue();
        result.Created.Should().BeFalse("何も保存していないため作成件数に数えてはならない");
        result.Success.Should().BeTrue("作成対象外はエラーではない");
        result.OutputPath.Should().BeNull();
        result.IsCommonFailure.Should().BeFalse();
    }

    /// <summary>
    /// 対の表明: 作成した結果は Created が立ち、出力先を持つこと
    /// </summary>
    /// <remarks>
    /// これが無いと、<c>Created</c> を常に false にした実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public void SuccessResult_作成したことと出力先を表すこと()
    {
        var result = ReportGenerationResult.SuccessResult(@"C:\out\物品出納簿_はやかけん_001_2026年度.xlsx");

        result.Created.Should().BeTrue();
        result.Success.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.IsCommonFailure.Should().BeFalse();
        result.OutputPath.Should().EndWith(".xlsx");
    }

    /// <summary>
    /// 結末を設定しない結果は「失敗」に見えること（既定値を安全側へ倒す）
    /// </summary>
    [Fact]
    public void 既定の結末は失敗であること()
    {
        var result = new ReportGenerationResult();

        result.Outcome.Should().Be(ReportGenerationOutcome.Failed);
        result.Success.Should().BeFalse("結末を設定し忘れた結果が成功に見えてはならない");
        result.Created.Should().BeFalse();
    }

    #endregion

    #region 共通原因の失敗

    /// <summary>
    /// 欠陥を突く側: 全カード共通の失敗は、文言に「テンプレート」を含まなくても型で判別できること
    /// </summary>
    [Fact]
    public void CommonFailureResult_文言に依らず共通原因の失敗として判別できること()
    {
        var result = ReportGenerationResult.CommonFailureResult(
            "組織設定のヘッダー列番号が正しくありません",
            "組織設定のヘッダー列番号が帳票の範囲外です（PageNumberColumn=20）。");

        result.IsCommonFailure.Should().BeTrue();
        result.ErrorMessage.Should().NotContain("テンプレート", "文言照合に依存していないことを表明する");
        result.Success.Should().BeFalse();
        result.Created.Should().BeFalse();
        result.Skipped.Should().BeFalse();
    }

    /// <summary>
    /// 対の表明: カード固有の失敗は、文言に「テンプレート」を含んでも共通原因にしないこと
    /// </summary>
    /// <remarks>
    /// これが無いと、すべての失敗を共通原因とみなす実装（1 枚目の失敗で必ず中断する）でも
    /// 上のテストが緑になる。
    /// </remarks>
    [Fact]
    public void FailureResult_文言にテンプレートを含んでも共通原因にしないこと()
    {
        var result = ReportGenerationResult.FailureResult(
            "テンプレートから作成したシートに書き込めませんでした");

        result.IsCommonFailure.Should().BeFalse();
        result.Success.Should().BeFalse();
    }

    #endregion

    #region 一括作成結果の集計

    /// <summary>
    /// 欠陥を突く側: 作成対象外は作成件数にもファイル一覧にも入らないこと
    /// </summary>
    [Fact]
    public void BatchResult_作成対象外は作成件数にもファイル一覧にも入らないこと()
    {
        var batch = new BatchReportGenerationResult(new List<(string, string, ReportGenerationResult)>
        {
            ("01", "はやかけん 001", ReportGenerationResult.SuccessResult(@"C:\out\001.xlsx")),
            ("02", "はやかけん 002", ReportGenerationResult.SkippedResult("新規購入より前の月です"))
        });

        batch.SuccessCount.Should().Be(1);
        batch.SkippedCount.Should().Be(1);
        batch.FailureCount.Should().Be(0);
        batch.SuccessfulFiles.Should().ContainSingle().Which.Should().EndWith("001.xlsx");
        batch.AllSuccess.Should().BeTrue("作成対象外はエラーではない");
        batch.GetSummary().Should().Be("1件の帳票を作成しました。（対象期間外 1件）");
    }

    /// <summary>
    /// 対の表明: 作成対象外が無ければ内訳を付けないこと
    /// </summary>
    [Fact]
    public void BatchResult_作成対象外が無ければ内訳を付けないこと()
    {
        var batch = new BatchReportGenerationResult(new List<(string, string, ReportGenerationResult)>
        {
            ("01", "はやかけん 001", ReportGenerationResult.SuccessResult(@"C:\out\001.xlsx")),
            ("02", "はやかけん 002", ReportGenerationResult.SuccessResult(@"C:\out\002.xlsx"))
        });

        batch.SuccessfulFiles.Should().HaveCount(2);
        batch.IsAborted.Should().BeFalse();
        batch.GetSummary().Should().Be("2件の帳票を作成しました。");
    }

    /// <summary>
    /// 欠陥を突く側: 共通原因の失敗があれば中断として報告すること
    /// </summary>
    [Fact]
    public void BatchResult_共通原因の失敗は中断として報告すること()
    {
        var batch = new BatchReportGenerationResult(new List<(string, string, ReportGenerationResult)>
        {
            ("01", "はやかけん 001", ReportGenerationResult.CommonFailureResult(
                "テンプレートファイルが見つかりません",
                "テンプレートを Resources\\Templates に配置してください。"))
        });

        batch.IsAborted.Should().BeTrue();

        // 中断の見出しは簡潔に、詳細は別に持つ（ステータス欄とダイアログで出し分ける。#1688）
        batch.AbortReason.Should().Be("テンプレートファイルが見つかりません");
        batch.AbortDetail.Should().Contain("配置してください");
        batch.GetSummary().Should().Be("帳票作成を中断しました: テンプレートファイルが見つかりません");
        batch.AllSuccess.Should().BeFalse();
    }

    /// <summary>
    /// 詳細を持たない共通失敗では、詳細の代わりに見出しを返すこと
    /// </summary>
    [Fact]
    public void BatchResult_詳細の無い共通失敗は見出しを詳細として返すこと()
    {
        var batch = new BatchReportGenerationResult(new List<(string, string, ReportGenerationResult)>
        {
            ("01", "はやかけん 001", ReportGenerationResult.CommonFailureResult("組織設定のヘッダー列番号が正しくありません"))
        });

        batch.AbortDetail.Should().Be("組織設定のヘッダー列番号が正しくありません");
    }

    /// <summary>
    /// 対の表明: カード固有の失敗だけなら中断にしないこと
    /// </summary>
    /// <remarks>
    /// これが無いと、失敗があれば常に中断とみなす実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public void BatchResult_カード固有の失敗だけなら中断にしないこと()
    {
        var batch = new BatchReportGenerationResult(new List<(string, string, ReportGenerationResult)>
        {
            ("01", "はやかけん 001", ReportGenerationResult.SuccessResult(@"C:\out\001.xlsx")),
            ("02", "はやかけん 002", ReportGenerationResult.FailureResult("カード情報が見つかりません"))
        });

        batch.IsAborted.Should().BeFalse();
        batch.AbortReason.Should().BeNull();
        batch.GetSummary().Should().Be("1件成功、1件失敗しました。");
    }

    #endregion
}
