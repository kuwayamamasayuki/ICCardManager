using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// SummaryGenerator の固有機能テスト
/// 基本的な Generate/GenerateByDate のテストは SummaryGeneratorComprehensiveTests を参照
/// </summary>
public class SummaryGeneratorTests
{
    private readonly SummaryGenerator _generator = new();

    #region Issue #510: 年度途中導入対応

    [Theory]
    [InlineData(1, "1月から繰越")]
    [InlineData(4, "4月から繰越")]
    [InlineData(5, "5月から繰越")]
    [InlineData(12, "12月から繰越")]
    public void GetMidYearCarryoverSummary_ReturnsCorrectFormat(int month, string expected)
    {
        // Act
        var result = SummaryGenerator.GetMidYearCarryoverSummary(month);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("1月から繰越", true)]
    [InlineData("5月から繰越", true)]
    [InlineData("12月から繰越", true)]
    [InlineData("10月から繰越", true)]
    [InlineData("新規購入", false)]
    [InlineData("前年度より繰越", false)]
    [InlineData("5月より繰越", false)]  // 「より」ではなく「から」
    [InlineData("13月から繰越", false)]  // 無効な月
    [InlineData("0月から繰越", false)]   // 無効な月
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMidYearCarryoverSummary_CorrectlyIdentifiesPattern(string? summary, bool expected)
    {
        // Act
        var result = SummaryGenerator.IsMidYearCarryoverSummary(summary);

        // Assert
        result.Should().Be(expected);
    }

    #endregion

    #region Issue #599: 繰越レコード日付計算

    [Fact]
    public void GetMidYearCarryoverDate_BasicCase_ReturnsFirstDayOfNextMonth()
    {
        // Arrange: 2月9日に「1月から繰越」→ 2月1日
        var registrationDate = new DateTime(2026, 2, 9);

        // Act
        var result = SummaryGenerator.GetMidYearCarryoverDate(1, registrationDate);

        // Assert
        result.Should().Be(new DateTime(2026, 2, 1));
    }

    [Fact]
    public void GetMidYearCarryoverDate_DecemberCarryover_ReturnsJanuaryFirstSameYear()
    {
        // Arrange: 1月15日に「12月から繰越」→ 1月1日（同年）
        var registrationDate = new DateTime(2026, 1, 15);

        // Act
        var result = SummaryGenerator.GetMidYearCarryoverDate(12, registrationDate);

        // Assert
        result.Should().Be(new DateTime(2026, 1, 1));
    }

    [Fact]
    public void GetMidYearCarryoverDate_FarPastMonth_ReturnsPreviousYear()
    {
        // Arrange: 2月15日に「11月から繰越」→ 前年12月1日
        var registrationDate = new DateTime(2026, 2, 15);

        // Act
        var result = SummaryGenerator.GetMidYearCarryoverDate(11, registrationDate);

        // Assert
        result.Should().Be(new DateTime(2025, 12, 1));
    }

    [Fact]
    public void GetMidYearCarryoverDate_FiscalYearStart_ReturnsAprilFirst()
    {
        // Arrange: 4月1日に「3月から繰越」→ 4月1日
        var registrationDate = new DateTime(2026, 4, 1);

        // Act
        var result = SummaryGenerator.GetMidYearCarryoverDate(3, registrationDate);

        // Assert
        result.Should().Be(new DateTime(2026, 4, 1));
    }

    #endregion

    #region Issue #633: GroupIdによる分割が摘要に反映される

    [Fact]
    public void Generate_RoundTrip_WithSingleItemGroupIds_ReturnsSeparateTrips()
    {
        // Arrange
        // 往復（博多→天神、天神→博多）を個別GroupIdで分割した場合
        // Generate()はReverse()するため、新しい順（ICカード順）で渡す
        var details = new List<LedgerDetail>
        {
            // 帰り（新しい）: 天神→博多
            new LedgerDetail
            {
                EntryStation = "天神",
                ExitStation = "博多",
                IsCharge = false,
                IsBus = false,
                Amount = 260,
                UseDate = new DateTime(2026, 2, 10, 15, 0, 0),
                Balance = 740,
                SequenceNumber = 2,
                GroupId = 2
            },
            // 行き（古い）: 博多→天神
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                IsCharge = false,
                IsBus = false,
                Amount = 260,
                UseDate = new DateTime(2026, 2, 10, 10, 0, 0),
                Balance = 1000,
                SequenceNumber = 1,
                GroupId = 1
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        // GroupIdが異なるため、往復検出されず個別表示される
        result.Should().Be("鉄道（博多～天神、天神～博多）");
    }

    [Fact]
    public void Generate_TransferTrip_WithSingleItemGroupIds_ReturnsSeparateTrips()
    {
        // Arrange
        // 乗継（博多→天神、天神→薬院）を個別GroupIdで分割した場合
        var details = new List<LedgerDetail>
        {
            // 2区間目（新しい）: 天神→薬院
            new LedgerDetail
            {
                EntryStation = "天神",
                ExitStation = "薬院",
                IsCharge = false,
                IsBus = false,
                Amount = 210,
                UseDate = new DateTime(2026, 2, 10, 10, 30, 0),
                Balance = 530,
                SequenceNumber = 2,
                GroupId = 2
            },
            // 1区間目（古い）: 博多→天神
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                IsCharge = false,
                IsBus = false,
                Amount = 260,
                UseDate = new DateTime(2026, 2, 10, 10, 0, 0),
                Balance = 740,
                SequenceNumber = 1,
                GroupId = 1
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        // GroupIdが異なるため、乗継統合されず個別表示される
        result.Should().Be("鉄道（博多～天神、天神～薬院）");
    }

    [Fact]
    public void Generate_RoundTrip_WithoutGroupId_ReturnsRoundTrip()
    {
        // Arrange
        // GroupIdなし（自動検出モード）の場合は従来通り往復検出される
        // FeliCa互換: 小さいSequenceNumber = 新しい（後に利用した）
        var details = new List<LedgerDetail>
        {
            // 帰り（新しい）: 天神→博多（小さいseq = 新しい）
            new LedgerDetail
            {
                EntryStation = "天神",
                ExitStation = "博多",
                IsCharge = false,
                IsBus = false,
                Amount = 260,
                UseDate = new DateTime(2026, 2, 10, 15, 0, 0),
                Balance = 740,
                SequenceNumber = 1,
                GroupId = null
            },
            // 行き（古い）: 博多→天神（大きいseq = 古い）
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                IsCharge = false,
                IsBus = false,
                Amount = 260,
                UseDate = new DateTime(2026, 2, 10, 10, 0, 0),
                Balance = 1000,
                SequenceNumber = 2,
                GroupId = null
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        // GroupIdがnullなので自動検出で往復判定
        result.Should().Be("鉄道（博多～天神 往復）");
    }

    #endregion
}
