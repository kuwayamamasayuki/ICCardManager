using System;
using FluentAssertions;
using ICCardManager.Dtos;
using Xunit;

namespace ICCardManager.Tests.Dtos;

/// <summary>
/// CardDtoの表示用プロパティの単体テスト
/// </summary>
public class CardDtoTests
{
    [Fact]
    public void DisplayName_カード種別と番号が結合されること()
    {
        var dto = new CardDto
        {
            CardType = "nimoca",
            CardNumber = "N-002"
        };

        dto.DisplayName.Should().Be("nimoca N-002");
    }

    #region LentStatusDisplay

    [Fact]
    public void LentStatusDisplay_払戻済の場合に払戻済と表示すること()
    {
        var dto = new CardDto { IsRefunded = true };

        dto.LentStatusDisplay.Should().Be("払戻済");
    }

    [Fact]
    public void LentStatusDisplay_貸出中の場合に貸出中と表示すること()
    {
        var dto = new CardDto { IsLent = true, IsRefunded = false };

        dto.LentStatusDisplay.Should().Be("貸出中");
    }

    [Fact]
    public void LentStatusDisplay_在庫の場合に在庫と表示すること()
    {
        var dto = new CardDto { IsLent = false, IsRefunded = false };

        dto.LentStatusDisplay.Should().Be("在庫");
    }

    [Fact]
    public void LentStatusDisplay_払戻済は貸出中より優先されること()
    {
        var dto = new CardDto { IsLent = true, IsRefunded = true };

        dto.LentStatusDisplay.Should().Be("払戻済");
    }

    #endregion

    #region LentAtDisplay

    [Fact]
    public void LentAtDisplay_日時がある場合にフォーマットされること()
    {
        var dto = new CardDto { LentAt = new DateTime(2025, 12, 25, 14, 30, 0) };

        dto.LentAtDisplay.Should().Be("2025/12/25 14:30");
    }

    [Fact]
    public void LentAtDisplay_日時がnullの場合にnullを返すこと()
    {
        var dto = new CardDto { LentAt = null };

        dto.LentAtDisplay.Should().BeNull();
    }

    #endregion
}
