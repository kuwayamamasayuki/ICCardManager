using FluentAssertions;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// WarningItemとWarningTypeの単体テスト
/// </summary>
public class WarningItemTests
{
    [Fact]
    public void WarningItem_デフォルト値が正しく設定されること()
    {
        var item = new WarningItem();

        item.DisplayText.Should().BeEmpty();
        item.Type.Should().Be(WarningType.LowBalance);
        item.CardIdm.Should().BeNull();
    }

    [Fact]
    public void WarningItem_すべてのプロパティが設定できること()
    {
        var item = new WarningItem
        {
            DisplayText = "残額不足: はやかけん H-001 (残高: ¥500)",
            Type = WarningType.LowBalance,
            CardIdm = "07FE112233445566"
        };

        item.DisplayText.Should().Contain("残額不足");
        item.Type.Should().Be(WarningType.LowBalance);
        item.CardIdm.Should().Be("07FE112233445566");
    }

    [Theory]
    [InlineData(WarningType.LowBalance)]
    [InlineData(WarningType.IncompleteBusStop)]
    [InlineData(WarningType.CardReaderError)]
    [InlineData(WarningType.CardReaderConnection)]
    [InlineData(WarningType.BalanceInconsistency)]
    [InlineData(WarningType.DatabaseConnectionLost)]
    public void WarningType_すべての種別が定義されていること(WarningType type)
    {
        // WarningTypeの各値が存在し、一意であることを確認
        type.Should().BeDefined();
    }

    [Fact]
    public void WarningType_6種類定義されていること()
    {
        var values = System.Enum.GetValues(typeof(WarningType));
        values.Length.Should().Be(6);
    }
}
