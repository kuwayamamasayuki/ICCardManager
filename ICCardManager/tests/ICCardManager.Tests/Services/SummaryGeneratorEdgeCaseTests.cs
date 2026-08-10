using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// SummaryGeneratorのエッジケーステスト
/// 既存テストで検出できない設定オプション・組み合わせパターンを検証する。
/// </summary>
/// <remarks>
/// Issue #1307: 本クラスは <c>SummaryGenerator.Configure</c> で静的状態を変更する。
/// 並列実行時の他テストへの波及を避けるため <see cref="SummaryGeneratorCollection"/> に属させる。
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class SummaryGeneratorEdgeCaseTests : IDisposable
{
    private readonly SummaryGenerator _generator = new();

    public SummaryGeneratorEdgeCaseTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
    }

    #region EnableRoundTripDetection=false のテスト

    /// <summary>
    /// 往復検出を無効にした場合、A→BとB→Aが「往復」として統合されず、
    /// 別々の区間として出力されることを検証。
    /// </summary>
    [Fact]
    public void GenerateByDate_RoundTripDetectionDisabled_NoRoundTripInOutput()
    {
        // Arrange
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryRules = new SummaryRulesOptions
            {
                EnableRoundTripDetection = false
            }
        });

        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 790
            },
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 210,
                Balance = 580
            }
        };

        // Act
        var result = _generator.GenerateByDate(details);

        // Assert
        result.Should().HaveCount(1);
        result[0].Summary.Should().NotContain("往復", "往復検出が無効なので「往復」は出力されない");
    }

    #endregion

    #region EnableTransferConsolidation=false のテスト

    /// <summary>
    /// 乗継統合を無効にした場合、A→BとB→Cが「A～C」に統合されず、
    /// 別々の区間として出力されることを検証。
    /// </summary>
    [Fact]
    public void GenerateByDate_TransferConsolidationDisabled_NoConsolidation()
    {
        // Arrange
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryRules = new SummaryRulesOptions
            {
                EnableTransferConsolidation = false
            }
        });

        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 790
            },
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "天神",
                ExitStation = "薬院",
                Amount = 200,
                Balance = 590
            }
        };

        // Act
        var result = _generator.GenerateByDate(details);

        // Assert
        result.Should().HaveCount(1);
        // 乗継統合無効の場合、「博多～天神、天神～薬院」のように個別表示
        result[0].Summary.Should().Contain("天神", "中間駅（天神）が省略されない");
    }

    #endregion

    #region 両方のルールを無効化

    /// <summary>
    /// 往復検出と乗継統合の両方を無効にした場合のテスト。
    /// </summary>
    [Fact]
    public void GenerateByDate_BothRulesDisabled_IndividualRoutes()
    {
        // Arrange
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryRules = new SummaryRulesOptions
            {
                EnableRoundTripDetection = false,
                EnableTransferConsolidation = false
            }
        });

        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 790
            },
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 210,
                Balance = 580
            }
        };

        // Act
        var result = _generator.GenerateByDate(details);

        // Assert
        result.Should().HaveCount(1);
        result[0].Summary.Should().NotContain("往復");
    }

    #endregion

    #region 乗継駅グループの検証

    /// <summary>
    /// デフォルトの乗継駅グループで「天神」と「西鉄福岡(天神)」が
    /// 同一駅として扱われることを検証。
    /// </summary>
    [Fact]
    public void GenerateByDate_TransferStationGroup_ConsolidatesDifferentNames()
    {
        // Arrange - デフォルト設定（天神=西鉄福岡(天神)）
        SummaryGenerator.ResetToDefaults();

        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 790
            },
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "西鉄福岡(天神)",
                ExitStation = "薬院",
                Amount = 200,
                Balance = 590
            }
        };

        // Act
        var result = _generator.GenerateByDate(details);

        // Assert - 乗継統合により「博多～薬院」に統合されるべき
        result.Should().HaveCount(1);
        result[0].Summary.Should().Contain("博多");
        result[0].Summary.Should().Contain("薬院");
    }

    #endregion

    #region 空入力・全件null日付

    /// <summary>
    /// 空リストを渡した場合、空リストが返ること。
    /// </summary>
    [Fact]
    public void GenerateByDate_EmptyList_ReturnsEmpty()
    {
        var result = _generator.GenerateByDate(new List<LedgerDetail>());

        result.Should().BeEmpty();
    }

    /// <summary>
    /// 全件のUseDateがnullの場合、空リストが返ること。
    /// </summary>
    [Fact]
    public void GenerateByDate_AllUseDateNull_ReturnsEmpty()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail { UseDate = null, EntryStation = "A", ExitStation = "B", Balance = 100 },
            new LedgerDetail { UseDate = null, EntryStation = "C", ExitStation = "D", Balance = 200 }
        };

        var result = _generator.GenerateByDate(details);

        result.Should().BeEmpty("UseDateがnullの明細はフィルタされるため");
    }

    #endregion

    #region チャージのみ・ポイント還元のみの日

    /// <summary>
    /// チャージのみの日は、チャージ用の摘要が生成されること。
    /// </summary>
    [Fact]
    public void GenerateByDate_ChargeOnly_GeneratesChargeSummary()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                Amount = 3000,
                Balance = 5000,
                IsCharge = true
            }
        };

        var result = _generator.GenerateByDate(details);

        result.Should().HaveCount(1);
        result[0].IsCharge.Should().BeTrue();
    }

    /// <summary>
    /// ポイント還元のみの日は、ポイント還元用の摘要が生成されること。
    /// </summary>
    [Fact]
    public void GenerateByDate_PointRedemptionOnly_GeneratesRedemptionSummary()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                Amount = 240,
                Balance = 1240,
                IsPointRedemption = true
            }
        };

        var result = _generator.GenerateByDate(details);

        result.Should().HaveCount(1);
        result[0].IsPointRedemption.Should().BeTrue();
    }

    #endregion

    #region IsImplicitPointRedemption の検証

    /// <summary>
    /// ポイント還元の暗黙判定: IsCharge=false, IsPointRedemption=false, Amount < 0 の場合。
    /// </summary>
    [Fact]
    public void IsImplicitPointRedemption_NegativeAmount_ReturnsTrue()
    {
        var detail = new LedgerDetail
        {
            Amount = -240,
            IsCharge = false,
            IsPointRedemption = false,
            EntryStation = null,
            ExitStation = null
        };

        var result = SummaryGenerator.IsImplicitPointRedemption(detail);

        result.Should().BeTrue("負の金額で駅情報なし＝暗黙のポイント還元");
    }

    /// <summary>
    /// Amount=0はポイント還元ではない。
    /// </summary>
    [Fact]
    public void IsImplicitPointRedemption_ZeroAmount_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            Amount = 0,
            IsCharge = false,
            IsPointRedemption = false
        };

        var result = SummaryGenerator.IsImplicitPointRedemption(detail);

        result.Should().BeFalse();
    }

    /// <summary>
    /// Amount > 0はポイント還元ではない。
    /// </summary>
    [Fact]
    public void IsImplicitPointRedemption_PositiveAmount_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            Amount = 240,
            IsCharge = false,
            IsPointRedemption = false
        };

        var result = SummaryGenerator.IsImplicitPointRedemption(detail);

        result.Should().BeFalse();
    }

    /// <summary>
    /// IsCharge=trueの場合はポイント還元ではない。
    /// </summary>
    [Fact]
    public void IsImplicitPointRedemption_IsChargeTrue_ReturnsFalse()
    {
        var detail = new LedgerDetail
        {
            Amount = -240,
            IsCharge = true,
            IsPointRedemption = false
        };

        var result = SummaryGenerator.IsImplicitPointRedemption(detail);

        result.Should().BeFalse();
    }

    #endregion

    #region 片側駅名欠落のプレースホルダ補完 (Issue #1735)

    /// <summary>
    /// 降車駅だけ駅名が解決できなかった鉄道明細が、摘要から落ちずに
    /// プレースホルダ「?」で補完されて出力されることを検証。
    /// StationCode.csv 未収録の新駅で降車した場合に発生する形状
    /// （FelicaHistoryBlockDecoder は片側解決時 IsBus=false・欠落側 null を返す）。
    /// </summary>
    [Fact]
    public void Generate_ExitStationUnresolved_UsesPlaceholderInsteadOfDroppingTrip()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = "博多",
                ExitStation = null,
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("鉄道（博多～?）");
    }

    /// <summary>
    /// 乗車駅だけ駅名が解決できなかった鉄道明細も同様に補完されることを検証。
    /// </summary>
    [Fact]
    public void Generate_EntryStationUnresolved_UsesPlaceholderInsteadOfDroppingTrip()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = null,
                ExitStation = "天神",
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("鉄道（?～天神）");
    }

    /// <summary>
    /// 複数区間のうち1件だけ片側欠落の場合、その区間が黙って欠落せず
    /// 「?」付きで併記されることを検証（Issue #1735 の故障シナリオ後半）。
    /// 入力は履歴と同じ新しい順（Generate 内部で時系列順に反転される）。
    /// </summary>
    [Fact]
    public void Generate_OneSidedTripMixedWithFullTrip_BothAppearInSummary()
    {
        var details = new List<LedgerDetail>
        {
            // 新しい方: 降車駅が未解決
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = "香椎",
                ExitStation = null,
                Amount = 260,
                Balance = 580
            },
            // 古い方: 両駅とも解決済み
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("鉄道（博多～天神、香椎～?）");
    }

    /// <summary>
    /// 未解決駅（同一の新駅）への往復が往復として検出されることを検証。
    /// 博多→X（未収録駅）、X→博多 の移動は「博多～? 往復」になる。
    /// </summary>
    [Fact]
    public void Generate_RoundTripToUnresolvedStation_DetectedAsRoundTrip()
    {
        var details = new List<LedgerDetail>
        {
            // 新しい方: 復路（乗車駅が未解決）
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = null,
                ExitStation = "博多",
                Amount = 210,
                Balance = 580
            },
            // 古い方: 往路（降車駅が未解決）
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = "博多",
                ExitStation = null,
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("鉄道（博多～? 往復）");
    }

    /// <summary>
    /// 連続する2区間の接続点が両方とも未解決の場合、プレースホルダ同士が
    /// 一致して乗継統合されることを検証（同一の未収録駅を経由した継続移動とみなす）。
    /// </summary>
    [Fact]
    public void Generate_ConsecutiveTripsJoinedAtUnresolvedStation_ConsolidatedAsTransfer()
    {
        var details = new List<LedgerDetail>
        {
            // 新しい方: 乗車駅が未解決
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = null,
                ExitStation = "天神",
                Amount = 260,
                Balance = 580
            },
            // 古い方: 降車駅が未解決
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = "博多",
                ExitStation = null,
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("鉄道（博多～天神）");
    }

    /// <summary>
    /// GroupId が設定された片側欠落明細も摘要から落ちないことを検証
    /// （履歴編集画面でグループ化された場合の経路）。
    /// </summary>
    [Fact]
    public void Generate_GroupedOneSidedTrip_UsesPlaceholderInsteadOfDroppingTrip()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                GroupId = 1,
                EntryStation = "博多",
                ExitStation = null,
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("鉄道（博多～?）");
    }

    /// <summary>
    /// 運賃 0 円の片側欠落明細（入場記録のみ＝未完了移動）は従来どおり
    /// 摘要に採用されないことを検証。プレースホルダ補完は「運賃が発生した完了移動」に
    /// 限定される（SummaryGeneratorComprehensiveTests TC019 の仕様を維持する境界）。
    /// </summary>
    [Fact]
    public void Generate_OneSidedTripWithZeroAmount_ExcludedFromSummary()
    {
        var details = new List<LedgerDetail>
        {
            // 新しい方: 入場記録のみ（運賃 0 円）
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = "天神",
                ExitStation = null,
                Amount = 0,
                Balance = 790
            },
            // 古い方: 通常の完了移動
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("鉄道（博多～天神）");
    }

    /// <summary>
    /// 両駅とも欠落した鉄道明細は従来どおり経路に採用されないことを検証
    /// （有効な区間と混在する場合、有効な区間のみが出力される）。
    /// </summary>
    [Fact]
    public void Generate_BothStationsEmptyMixedWithFullTrip_OnlyFullTripAppears()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = null,
                ExitStation = null,
                Amount = 260,
                Balance = 580
            },
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be("鉄道（博多～天神）");
    }

    /// <summary>
    /// 両駅とも欠落した鉄道明細のみの場合、Generate は空文字を返すという契約を固定する。
    /// この空文字を摘要が空欄の台帳行にしないのは LendingService 側の
    /// 代替文言ガード（GetUnknownUsageSummary）の責務（LendingServiceTests で検証）。
    /// </summary>
    [Fact]
    public void Generate_BothStationsEmptyOnly_ReturnsEmpty()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 8, 1),
                EntryStation = null,
                ExitStation = null,
                Amount = 210,
                Balance = 790
            }
        };

        var result = _generator.Generate(details);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// 代替摘要の既定値を検証（摘要が空欄の台帳行を防ぐ安全網の文言）。
    /// </summary>
    [Fact]
    public void GetUnknownUsageSummary_Default_ReturnsRailwayUnknownRoute()
    {
        SummaryGenerator.GetUnknownUsageSummary().Should().Be("鉄道（区間不明）");
    }

    /// <summary>
    /// 代替摘要が SummaryTextOptions でカスタマイズ可能なことを検証。
    /// </summary>
    [Fact]
    public void GetUnknownUsageSummary_Configured_ReturnsCustomText()
    {
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryText = new SummaryTextOptions
            {
                UnknownUsageSummary = "電車（経路不明）"
            }
        });

        SummaryGenerator.GetUnknownUsageSummary().Should().Be("電車（経路不明）");
    }

    #endregion

    // 注: GetMidYearCarryoverSummary / IsMidYearCarryoverSummary の静的メソッドテストは
    // SummaryGeneratorTests.cs でカバー済みのためこのファイルからは削除
}
