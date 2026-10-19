using System;
using System.Linq;
using FluentAssertions;
using ICCardManager.Infrastructure.CardReader;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.CardReader;

/// <summary>
/// Issue #2293: UI テストが仮想カードリーダーへ渡すカードデータ（JSON）の解析。
/// </summary>
/// <remarks>
/// 読み込みとリーダーへの設定（<c>ApplyFromEnvironment</c>）は DEBUG ビルドにしか無いが、解析は Release でも検証する。
/// 形式の誤りを黙って読み飛ばすと、UI テストが「履歴の無いカード」として進み、原因の分からない失敗になる。
/// </remarks>
public class DebugCardDataTests
{
    [Fact]
    public void Parse_残額と利用履歴を読み_履歴の並びと各項目を保つこと()
    {
        const string json = @"{
  ""cards"": [
    {
      ""idm"": ""07FE112233445566"",
      ""balance"": 19580,
      ""history"": [
        { ""useDate"": ""2026-10-19"", ""entryStation"": ""博多"", ""exitStation"": ""天神"", ""amount"": 210, ""balance"": 19580 },
        { ""useDate"": ""2026-10-18"", ""amount"": 1000, ""balance"": 19790, ""isCharge"": true },
        { ""useDate"": ""2026-10-17"", ""amount"": 200, ""balance"": 18790, ""isBus"": true }
      ]
    }
  ]
}";

        var cards = DebugCardData.Parse(json);

        var card = cards.Should().ContainSingle().Subject;
        card.Idm.Should().Be("07FE112233445566");
        card.Balance.Should().Be(19580);
        card.History!.Select(h => h.UseDate).Should().Equal(
            new DateTime(2026, 10, 19), new DateTime(2026, 10, 18), new DateTime(2026, 10, 17));

        var first = card.History![0];
        (first.EntryStation, first.ExitStation, first.Amount, first.Balance, first.IsCharge, first.IsBus)
            .Should().Be(("博多", "天神", 210, 19580, false, false));
        card.History[1].IsCharge.Should().BeTrue();
        card.History[1].EntryStation.Should().BeEmpty("駅名を省略したらカードの読み取りと同じく空文字");
        card.History[2].IsBus.Should().BeTrue();
    }

    [Fact]
    public void Parse_残額や履歴を省略したカードはその項目をnullにすること()
    {
        var cards = DebugCardData.Parse(@"{ ""cards"": [ { ""idm"": ""A"" }, { ""idm"": ""B"", ""balance"": 0 } ] }");

        cards.Select(c => (c.Idm, c.Balance, c.History == null)).Should().Equal(
            ("A", (int?)null, true),
            ("B", (int?)0, true));
    }

    [Fact]
    public void Parse_IDmが無いカードは形式の誤りとすること()
    {
        var act = () => DebugCardData.Parse(@"{ ""cards"": [ { ""balance"": 100 } ] }");

        act.Should().Throw<FormatException>().WithMessage("*idm*");
    }

    [Fact]
    public void Parse_日付がyyyy_MM_dd形式でなければ形式の誤りとすること()
    {
        var act = () => DebugCardData.Parse(
            @"{ ""cards"": [ { ""idm"": ""A"", ""history"": [ { ""useDate"": ""2026/10/19"", ""amount"": 210, ""balance"": 100 } ] } ] }");

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Parse_cardsが無ければ空を返すこと()
    {
        DebugCardData.Parse("{}").Should().BeEmpty();
    }
}
