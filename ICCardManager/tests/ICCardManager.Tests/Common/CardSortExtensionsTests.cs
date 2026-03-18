using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// CardSortExtensionsの単体テスト
/// </summary>
public class CardSortExtensionsTests
{
    private record CardItem(string CardType, string CardNumber);

    private readonly List<CardItem> _unsorted = new()
    {
        new("nimoca", "N002"),
        new("はやかけん", "H001"),
        new("nimoca", "N001"),
        new("SUGOCA", "S001"),
        new("はやかけん", "H002"),
    };

    [Fact]
    public void OrderByCardDefault_SortsByTypeThenNumber()
    {
        var result = _unsorted
            .OrderByCardDefault(c => c.CardType, c => c.CardNumber)
            .ToList();

        // 同一カード種別内で番号順にソートされることを検証
        var nimocaItems = result.Where(c => c.CardType == "nimoca").ToList();
        nimocaItems[0].CardNumber.Should().Be("N001");
        nimocaItems[1].CardNumber.Should().Be("N002");

        var hayakakenItems = result.Where(c => c.CardType == "はやかけん").ToList();
        hayakakenItems[0].CardNumber.Should().Be("H001");
        hayakakenItems[1].CardNumber.Should().Be("H002");

        // 全5件が返されること
        result.Should().HaveCount(5);

        // カード種別でグループ化され、同種別が連続していること
        var types = result.Select(c => c.CardType).ToList();
        types.Distinct().Should().HaveCount(3);
        // 同一種別のアイテムが連続している（ソート済み）
        var firstNimocaIndex = types.IndexOf("nimoca");
        var lastNimocaIndex = types.LastIndexOf("nimoca");
        (lastNimocaIndex - firstNimocaIndex).Should().Be(1);
    }

    [Fact]
    public void OrderByCardDefault_EmptyCollection_ReturnsEmpty()
    {
        var result = new List<CardItem>()
            .OrderByCardDefault(c => c.CardType, c => c.CardNumber)
            .ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void ThenByCardDefault_UsedAsSecondarySort()
    {
        var items = new List<(int Balance, string CardType, string CardNumber)>
        {
            (1000, "nimoca", "N002"),
            (500, "はやかけん", "H001"),
            (1000, "はやかけん", "H001"),
            (500, "nimoca", "N001"),
            (1000, "nimoca", "N001"),
        };

        var result = items
            .OrderBy(x => x.Balance)
            .ThenByCardDefault(x => x.CardType, x => x.CardNumber)
            .ToList();

        // プライマリソート（Balance）が効いている
        result.Take(2).Should().AllSatisfy(x => x.Balance.Should().Be(500));
        result.Skip(2).Should().AllSatisfy(x => x.Balance.Should().Be(1000));

        // 同一Balance内で同一CardTypeのアイテムが連続し、CardNumber昇順
        var balance1000 = result.Where(x => x.Balance == 1000).ToList();
        var nimoca1000 = balance1000.Where(x => x.CardType == "nimoca").ToList();
        nimoca1000[0].CardNumber.Should().Be("N001");
        nimoca1000[1].CardNumber.Should().Be("N002");
    }
}
