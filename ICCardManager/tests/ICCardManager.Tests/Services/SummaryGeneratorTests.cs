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

    #region バス往復検出 - Issue #985

    /// <summary>
    /// バスの往復利用（A～B、B～A）が「A～B 往復」として表示されること
    /// </summary>
    [Fact]
    public void Generate_バス往復_往復と表示される()
    {
        // Arrange: FeliCa順（新しい順）で入力
        // 時系列: 薬院大通→西鉄平尾駅（往路）→ 西鉄平尾駅→薬院大通（復路）
        var details = new List<LedgerDetail>
        {
            new()
            {
                IsBus = true,
                BusStops = "西鉄平尾駅～薬院大通",  // 復路（新しい）
                Amount = 210,
                Balance = 290
            },
            new()
            {
                IsBus = true,
                BusStops = "薬院大通～西鉄平尾駅",  // 往路（古い）
                Amount = 210,
                Balance = 500
            }
        };

        // Act
        var result = _generator.Generate(details);

        // Assert
        result.Should().Be("バス（薬院大通～西鉄平尾駅 往復）");
    }

    /// <summary>
    /// バスの片道利用では往復と表示されないこと
    /// </summary>
    [Fact]
    public void Generate_バス片道_往復とならない()
    {
        var details = new List<LedgerDetail>
        {
            new()
            {
                IsBus = true,
                BusStops = "薬院大通～西鉄平尾駅",
                Amount = 210,
                Balance = 500
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("バス（薬院大通～西鉄平尾駅）");
    }

    /// <summary>
    /// バス停名が「A～B」形式でない場合は往復検出されず連結されること
    /// </summary>
    [Fact]
    public void Generate_バス停名に経路区切りなし_連結される()
    {
        // FeliCa順（新しい順）: 博多(新)、天神(古)
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "博多", Amount = 210, Balance = 290 },
            new() { IsBus = true, BusStops = "天神", Amount = 210, Balance = 500 }
        };

        var result = _generator.Generate(details);

        // Reverse後: 天神(古)→博多(新) の順
        result.Should().Be("バス（天神、博多）");
    }

    /// <summary>
    /// バス往復＋残りの片道がある場合の表示
    /// </summary>
    [Fact]
    public void Generate_バス往復と片道混在_正しく表示される()
    {
        // FeliCa順（新しい順）: 博多→吉塚(片道)、天神→薬院(復路)、薬院→天神(往路)
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "博多～吉塚", Amount = 170, Balance = 320 },
            new() { IsBus = true, BusStops = "天神～薬院", Amount = 210, Balance = 490 },
            new() { IsBus = true, BusStops = "薬院～天神", Amount = 210, Balance = 700 }
        };

        var result = _generator.Generate(details);

        result.Should().Contain("薬院～天神 往復");
        result.Should().Contain("博多～吉塚");
    }

    /// <summary>
    /// 鉄道とバス往復が混在する場合
    /// </summary>
    [Fact]
    public void Generate_鉄道とバス往復混在_正しく表示される()
    {
        // FeliCa順（新しい順）: 渡辺通→天神(復路)、天神→渡辺通(往路)、博多→天神(鉄道)
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "渡辺通～天神", Amount = 210, Balance = 530 },
            new() { IsBus = true, BusStops = "天神～渡辺通", Amount = 210, Balance = 740 },
            new() { IsBus = false, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 1000 }
        };

        var result = _generator.Generate(details);

        result.Should().Contain("鉄道（博多～天神）");
        result.Should().Contain("バス（天神～渡辺通 往復）");
    }

    /// <summary>
    /// バス停名が★（未入力）の場合は往復検出されないこと
    /// </summary>
    [Fact]
    public void Generate_バス停名が未入力_往復検出されない()
    {
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "★", Amount = 210, Balance = 500 },
            new() { IsBus = true, BusStops = "★", Amount = 210, Balance = 290 }
        };

        var result = _generator.Generate(details);

        // ★はDistinctで1つになるので「バス（★）」
        result.Should().Be("バス（★）");
    }

    #endregion

    #region バス乗り継ぎ統合 - Issue #985

    /// <summary>
    /// バスの乗り継ぎ（A～B、B～C）が「A～C」として統合されること
    /// </summary>
    [Fact]
    public void Generate_バス乗り継ぎ_統合される()
    {
        // Arrange: FeliCa順（新しい順）で入力
        // 時系列: 薬院大通→天神（1区間目）→ 天神→博多駅（2区間目）
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "天神～博多駅", Amount = 210, Balance = 290 },  // 新しい
            new() { IsBus = true, BusStops = "薬院大通～天神", Amount = 210, Balance = 500 }  // 古い
        };

        var result = _generator.Generate(details);

        result.Should().Be("バス（薬院大通～博多駅）");
    }

    /// <summary>
    /// バスの3区間乗り継ぎ（A～B、B～C、C～D）が「A～D」として統合されること
    /// </summary>
    [Fact]
    public void Generate_バス3区間乗り継ぎ_統合される()
    {
        // FeliCa順（新しい順）
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "西新～藤崎", Amount = 170, Balance = 120 },      // 3区間目（新）
            new() { IsBus = true, BusStops = "天神～西新", Amount = 210, Balance = 290 },       // 2区間目
            new() { IsBus = true, BusStops = "薬院大通～天神", Amount = 210, Balance = 500 }    // 1区間目（古）
        };

        var result = _generator.Generate(details);

        result.Should().Be("バス（薬院大通～藤崎）");
    }

    /// <summary>
    /// バス乗り継ぎ＋往復（A～B、B～C、C～B、B～A）が正しく検出されること
    /// </summary>
    [Fact]
    public void Generate_バス乗り継ぎ往復_統合される()
    {
        // 時系列: 薬院→天神（往路1）、天神→博多（往路2）、博多→天神（復路1）、天神→薬院（復路2）
        // FeliCa順は逆
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "天神～薬院", Amount = 210, Balance = 160 },      // 復路2（最新）
            new() { IsBus = true, BusStops = "博多～天神", Amount = 260, Balance = 370 },      // 復路1
            new() { IsBus = true, BusStops = "天神～博多", Amount = 260, Balance = 630 },      // 往路2
            new() { IsBus = true, BusStops = "薬院～天神", Amount = 210, Balance = 890 }       // 往路1（最古）
        };

        var result = _generator.Generate(details);

        result.Should().Be("バス（薬院～博多 往復）");
    }

    /// <summary>
    /// 乗り継ぎにならないバス（降車停と次の乗車停が異なる）は個別表示されること
    /// </summary>
    [Fact]
    public void Generate_バス乗り継ぎにならない_個別表示()
    {
        // 時系列: 薬院大通→天神、博多→吉塚（天神≠博多なので乗り継ぎにならない）
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "博多～吉塚", Amount = 170, Balance = 330 },     // 新しい
            new() { IsBus = true, BusStops = "薬院大通～天神", Amount = 210, Balance = 500 }   // 古い
        };

        var result = _generator.Generate(details);

        result.Should().Be("バス（薬院大通～天神、博多～吉塚）");
    }

    /// <summary>
    /// 鉄道とバス乗り継ぎが混在する場合
    /// </summary>
    [Fact]
    public void Generate_鉄道とバス乗り継ぎ混在_正しく表示される()
    {
        // FeliCa順（新しい順）: 天神→博多(バス2区間目)、薬院→天神(バス1区間目)、博多→天神(鉄道)
        var details = new List<LedgerDetail>
        {
            new() { IsBus = true, BusStops = "天神～博多駅", Amount = 210, Balance = 530 },
            new() { IsBus = true, BusStops = "薬院～天神", Amount = 210, Balance = 740 },
            new() { IsBus = false, EntryStation = "博多", ExitStation = "天神", Amount = 260, Balance = 1000 }
        };

        var result = _generator.Generate(details);

        result.Should().Contain("鉄道（博多～天神）");
        result.Should().Contain("バス（薬院～博多駅）");
    }

    #endregion
}
