using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1908: カードの実残額とピッすいの記録の食い違い判定（<see cref="WarningService.CheckCardBalanceMismatchWarning"/>）の単体テスト。
/// </summary>
public class WarningServiceCardBalanceMismatchTests
{
    private const string CardIdm = "0102030405060708";

    private readonly WarningService _service = new WarningService(
        new Mock<ILedgerRepository>().Object,
        new Mock<IDatabaseInfo>().Object);

    private WarningItem Check(int actualBalance, int recordedBalance, bool isLent = false) =>
        _service.CheckCardBalanceMismatchWarning(
            CardIdm, "はやかけん", "No.3", actualBalance, recordedBalance, isLent);

    [Fact]
    public void 実残額と記録が一致する場合は警告を生成しないこと()
    {
        Check(actualBalance: 2500, recordedBalance: 2500).Should().BeNull();
    }

    [Fact]
    public void 残額がゼロどうしで一致する場合も警告を生成しないこと()
    {
        // 使い切ったカードで「0 円だから異常」と誤検出しないことを固定する
        Check(actualBalance: 0, recordedBalance: 0).Should().BeNull();
    }

    [Fact]
    public void 実残額が記録より少ない場合に警告を生成すること()
    {
        // ピッすいを通さずに利用された典型例
        var warning = Check(actualBalance: 1250, recordedBalance: 2500);

        warning.Should().NotBeNull();
        warning.Type.Should().Be(WarningType.CardBalanceMismatch);
        warning.CardIdm.Should().Be(CardIdm, "警告クリックで該当カードの履歴を開くために保持する");
    }

    [Fact]
    public void 実残額が記録より多い場合も警告を生成すること()
    {
        // ピッすいを通さずに現金チャージされた場合。増加方向も記録漏れである。
        Check(actualBalance: 5000, recordedBalance: 2500).Should().NotBeNull();
    }

    [Theory]
    [InlineData(1250, 2500)]
    [InlineData(2500, 1250)]
    public void 文言に実残額と記録と差額が含まれること(int actualBalance, int recordedBalance)
    {
        var warning = Check(actualBalance, recordedBalance);

        // 「何が」— カードの特定と、判断に必要な 3 つの金額
        warning.DisplayText.Should().Contain("はやかけん");
        warning.DisplayText.Should().Contain("No.3");
        warning.DisplayText.Should().Contain($"{actualBalance:N0}円");
        warning.DisplayText.Should().Contain($"{recordedBalance:N0}円");
        warning.DisplayText.Should().Contain("1,250円", "差額（絶対値）も示す");
    }

    [Fact]
    public void 貸出中のカードには返却処理を案内すること()
    {
        var warning = Check(actualBalance: 1250, recordedBalance: 2500, isLent: true);

        warning.DisplayText.Should().Contain("返却処理");
        warning.DisplayText.Should().NotContain("CSVインポート",
            "貸出中は返却処理で記録が追いつくため、取れる行動が違う（error-messages.md #1757）");
    }

    [Fact]
    public void 未貸出のカードには履歴の補完を案内すること()
    {
        var warning = Check(actualBalance: 1250, recordedBalance: 2500, isLent: false);

        warning.DisplayText.Should().Contain("CSVインポート");
        warning.DisplayText.Should().NotContain("返却処理",
            "未貸出のカードは返却処理ができないため、実行できない指示を出さない");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 警告文言がエラーメッセージ品質基準を満たすこと(bool isLent)
    {
        var text = Check(actualBalance: 1250, recordedBalance: 2500, isLent).DisplayText;

        // 「何が」「なぜ」「どうすれば」の 3 要素（.claude/rules/error-messages.md）
        text.Length.Should().BeGreaterThan(20, "短すぎる文言は情報不足になる");
        text.Should().Contain("食い違", "何が起きたか");
        text.Should().Contain("ピッすいを通さずに", "なぜそうなったか");
        Regex.IsMatch(text, "してください。?$").Should().BeTrue(
            $"行動指示で終わること。実際の文言: {text}");

        foreach (var vague in new[] { "エラーが発生しました", "不正な値です", "入力が正しくありません" })
        {
            text.Should().NotContain(vague);
        }
    }
}
