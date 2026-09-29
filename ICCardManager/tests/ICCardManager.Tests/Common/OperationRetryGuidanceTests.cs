using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// Issue #2141: 貸出・返却が記録されずに終わったときの「やり直し方」の案内を固定する。
/// </summary>
/// <remarks>
/// <para>
/// 失敗後は <c>MainViewModel</c> の <c>finally</c> が職員証タッチ待ちへ戻す。そこで交通系ICカードだけを
/// タッチし直すと履歴の表示になり、記録が無いので30秒ルールも発動しない。旧文言「もう一度タッチしてください」は
/// まさにその操作を促していた（挙動は <c>MainViewModelIntegrationTests</c> の Issue #2141 の region が固定する）。
/// </para>
/// <para>
/// 期待値はリテラルで書く（本番の定数から読むと、本番の文言が変わっても期待値が一緒に動いて
/// 表明が自己充足する。ui-conventions #1884）。
/// </para>
/// </remarks>
public class OperationRetryGuidanceTests
{
    [Theory]
    [InlineData("貸出", "貸出処理に失敗しました。職員証のタッチからやり直してください。")]
    [InlineData("返却", "返却処理に失敗しました。職員証のタッチからやり直してください。")]
    public void BuildFailureMessage_何が失敗したかと職員証から始める行動指示を述べること(string operation, string expected)
    {
        OperationRetryGuidance.BuildFailureMessage(operation).Should().Be(expected);
    }

    [Theory]
    [InlineData("貸出")]
    [InlineData("返却")]
    public void BuildFailureMessage_トーストで切れない長さに収まること(string operation)
    {
        // LendingServiceErrorMessageTests と同じ基準（文字サイズ「大」以上でトーストの末尾が切れる。#1273 / #1817）
        OperationRetryGuidance.BuildFailureMessage(operation).Length.Should().BeLessThan(40);
    }

    [Fact]
    public void RestartFromStaffCard_時間切れのトーストと同じ語彙であること()
    {
        // 同じ状態（職員証タッチ待ち）へ戻る 2 つの経路で案内を食い違わせない（#1817「近くにある既存文言の語彙に揃える」）。
        // 時間切れのトーストは MainViewModelTests.Timeout_ShouldShowTimeUpToast が固定している
        OperationRetryGuidance.RestartFromStaffCard.Should().Be("職員証のタッチからやり直してください。");
    }
}

/// <summary>
/// Issue #2141: 貸出・返却の失敗の案内が、旧文言（交通系ICカードだけを再タッチさせる形）へ戻らないことを
/// 本番ソース全体で固定する。
/// </summary>
/// <remarks>
/// 旧文言は <c>LendingService</c> の 1 か所と <c>MainViewModel</c> の 3 か所に書き写されていた。
/// 個別の挙動テストは経路の追加に追随できないため、「禁止された形の不在」と「正しい形の存在」を対で見る
/// （error-messages.md #1764。不在だけを見ると、案内ごと消した実装でも緑になる）。
/// 検査はコメントを除き文字列リテラルを残した本文で行う（旧文言を説明するコメント自体が
/// 違反になる極性の反転を避ける。#1692）。
/// </remarks>
public class OperationRetryGuidanceConventionTests
{
    /// <summary>
    /// 失敗後の再試行として「交通系ICカードだけのタッチ」を促す形。
    /// </summary>
    /// <remarks>
    /// 「もう一度タッチしてください」「カードを再度タッチしてください」の 2 形が実在した。
    /// </remarks>
    internal static readonly Regex ForbiddenRetryPattern =
        new Regex(@"(もう一度|カードを再度)タッチしてください", RegexOptions.Compiled);

    [Fact]
    public void 本番ソースに交通系ICカードだけの再タッチを促す案内が無いこと()
    {
        var violations = ProductionSourceFiles.CSharp
            .SelectMany(f => ForbiddenRetryPattern.Matches(f.CommentsRemovedPreservingLines)
                .Cast<Match>()
                .Select(m => $"{f.RelativePath}:{XamlElementInspection.LineOf(f.CommentsRemovedPreservingLines, m.Index)}"))
            .ToList();

        violations.Should().BeEmpty(
            "失敗後は職員証タッチ待ちへ戻るため、交通系ICカードだけを再タッチすると履歴表示になる。" +
            "OperationRetryGuidance で「職員証のタッチから」と案内すること");
    }

    [Theory]
    [InlineData("Services/LendingService.cs")]
    [InlineData("ViewModels/MainViewModel.cs")]
    public void 貸出返却の失敗の案内がOperationRetryGuidanceへ委譲していること(string relativePath)
    {
        var file = ProductionSourceFiles.CSharp.Single(f =>
            f.RelativePath.Replace('\\', '/').EndsWith(relativePath, System.StringComparison.OrdinalIgnoreCase));

        file.CodeOnly.Should().Contain("OperationRetryGuidance.",
            "不在の検査だけでは、案内ごと消した実装でも緑になる（対の表明）");
    }

    [Theory]
    [InlineData("貸出処理に失敗しました。もう一度タッチしてください。", true)]
    [InlineData("履歴の読み取りに失敗しました。カードを再度タッチしてください。", true)]
    [InlineData("貸出処理に失敗しました。職員証のタッチからやり直してください。", false)]
    [InlineData("復旧したら、もう一度カードをタッチしてください。", false)]
    public void 検出パターン自体が旧文言だけを拾うこと(string text, bool expected)
    {
        // 検査ロジックを既知の入力で固定する（実データが空でも空振りしない。#1786）。
        // 最後の行は登録ダイアログの「復旧したら、もう一度カードをタッチしてください」（初期化失敗の案内で、
        // 職員証タッチ待ちへ戻る経路ではない）を誤検出しないことの表明
        ForbiddenRetryPattern.IsMatch(text).Should().Be(expected);
    }
}
