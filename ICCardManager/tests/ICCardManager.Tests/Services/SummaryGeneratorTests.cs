using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

public class SummaryGeneratorTests
{
    private readonly SummaryGenerator _generator = new();

    [Fact]
    public void Generate_EmptyDetails_ReturnsEmptyString()
    {
        // Arrange
        var details = new List<LedgerDetail>();

        // Act
        var result = _generator.Generate(details);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Generate_ChargeOnly_ReturnsChargeSummary()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { IsCharge = true, Amount = 3000 }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        result.Should().Be("役務費によりチャージ");
    }

    [Fact]
    public void Generate_SingleRailwayTrip_ReturnsCorrectFormat()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                IsCharge = false,
                IsBus = false,
                Amount = 260
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        result.Should().Be("鉄道（博多～天神）");
    }

    [Fact]
    public void Generate_RoundTrip_ReturnsRoundTripFormat()
    {
        // Arrange
        // ICカード履歴は新しい順で格納されているため、
        // 帰り（天神→博多）が先、行き（博多→天神）が後になる
        var details = new List<LedgerDetail>
        {
            // 帰り（新しい）: 天神→博多
            new LedgerDetail
            {
                EntryStation = "天神",
                ExitStation = "博多",
                IsCharge = false,
                IsBus = false,
                Amount = 260
            },
            // 行き（古い）: 博多→天神
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                IsCharge = false,
                IsBus = false,
                Amount = 260
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        // 時系列順で最初の移動方向（博多→天神）が先に表示される
        result.Should().Be("鉄道（博多～天神 往復）");
    }

    [Fact]
    public void Generate_TransferTrip_ReturnsConsolidatedFormat()
    {
        // Arrange
        // ICカード履歴は新しい順で格納されているため、
        // 2区間目（天神→薬院）が先、1区間目（博多→天神）が後になる
        var details = new List<LedgerDetail>
        {
            // 2区間目（新しい）: 天神→薬院
            new LedgerDetail
            {
                EntryStation = "天神",
                ExitStation = "薬院",
                IsCharge = false,
                IsBus = false
            },
            // 1区間目（古い）: 博多→天神
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                IsCharge = false,
                IsBus = false
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        // 乗り継ぎは始発駅～終着駅に統合される
        result.Should().Be("鉄道（博多～薬院）");
    }

    [Fact]
    public void Generate_MultipleSeparateTrips_ReturnsMultipleRoutes()
    {
        // Arrange
        // ICカード履歴は新しい順で格納されているため、
        // 後の移動（薬院→大橋）が先、先の移動（博多→天神）が後になる
        var details = new List<LedgerDetail>
        {
            // 2回目の移動（新しい）: 薬院→大橋
            new LedgerDetail
            {
                EntryStation = "薬院",
                ExitStation = "大橋",
                IsCharge = false,
                IsBus = false
            },
            // 1回目の移動（古い）: 博多→天神
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                IsCharge = false,
                IsBus = false
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        // 時系列順で表示される
        result.Should().Be("鉄道（博多～天神、薬院～大橋）");
    }

    [Fact]
    public void Generate_BusOnly_ReturnsBusWithStar()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                EntryStation = null,
                ExitStation = null,
                IsCharge = false,
                IsBus = true,
                Amount = 230
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        result.Should().Be("バス（★）");
    }

    [Fact]
    public void Generate_BusWithBusStops_ReturnsProperBusStops()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                EntryStation = null,
                ExitStation = null,
                BusStops = "天神～博多駅",
                IsCharge = false,
                IsBus = true,
                Amount = 230
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        result.Should().Be("バス（天神～博多駅）");
    }

    [Fact]
    public void Generate_RailwayAndBus_ReturnsCombinedSummary()
    {
        // Arrange
        // ICカード履歴は新しい順で格納されているため、
        // 後の利用（バス）が先、先の利用（鉄道）が後になる
        var details = new List<LedgerDetail>
        {
            // 2回目の移動（新しい）: バス
            new LedgerDetail
            {
                EntryStation = null,
                ExitStation = null,
                IsCharge = false,
                IsBus = true,
                Amount = 230
            },
            // 1回目の移動（古い）: 鉄道
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                IsCharge = false,
                IsBus = false,
                Amount = 260
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        // 鉄道が先、バスが後に表示される（種別でグループ化）
        result.Should().Be("鉄道（博多～天神）、バス（★）");
    }

    [Fact]
    public void GetLendingSummary_ReturnsCorrectString()
    {
        // Act
        var result = SummaryGenerator.GetLendingSummary();

        // Assert
        result.Should().Be("（貸出中）");
    }

    [Fact]
    public void GetChargeSummary_ReturnsCorrectString()
    {
        // Act
        var result = SummaryGenerator.GetChargeSummary();

        // Assert
        result.Should().Be("役務費によりチャージ");
    }

    [Fact]
    public void GetCarryoverFromPreviousYearSummary_ReturnsCorrectString()
    {
        // Act
        var result = SummaryGenerator.GetCarryoverFromPreviousYearSummary();

        // Assert
        result.Should().Be("前年度より繰越");
    }

    [Fact]
    public void GetCarryoverToNextYearSummary_ReturnsCorrectString()
    {
        // Act
        var result = SummaryGenerator.GetCarryoverToNextYearSummary();

        // Assert
        result.Should().Be("次年度へ繰越");
    }

    [Theory]
    [InlineData(1, "1月計")]
    [InlineData(4, "4月計")]
    [InlineData(12, "12月計")]
    public void GetMonthlySummary_ReturnsCorrectFormat(int month, string expected)
    {
        // Act
        var result = SummaryGenerator.GetMonthlySummary(month);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GetCumulativeSummary_ReturnsCorrectString()
    {
        // Act
        var result = SummaryGenerator.GetCumulativeSummary();

        // Assert
        result.Should().Be("累計");
    }
}
