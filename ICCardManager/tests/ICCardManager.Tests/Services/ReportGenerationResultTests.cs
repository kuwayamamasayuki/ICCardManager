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
}
