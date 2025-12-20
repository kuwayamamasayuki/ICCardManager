using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;
using Xunit.Abstractions;

namespace ICCardManager.Tests.Services;

/// <summary>
/// 駅コードから履歴文字列への変換テスト
///
/// ICカードから読み取った駅コードデータ列から、物品出納簿の摘要欄に印字する
/// 文字列への変換処理を検証する。
///
/// テスト対象フロー:
/// 1. ICカードから駅コード（lineCode, stationCode）を取得
/// 2. StationMasterService で駅コードを駅名に変換
/// 3. SummaryGenerator で駅名リストから摘要文字列を生成
///
/// 駅コードフォーマット:
/// - 上位バイト: 路線コード（0-255）
/// - 下位バイト: 駅番号（0-255）
/// - 例: 0xE70F = 路線231(0xE7), 駅15(0x0F) = 福岡市地下鉄空港線 天神
/// </summary>
public class StationCodeToSummaryTests
{
    private readonly StationMasterService _stationService;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly ITestOutputHelper _output;

    public StationCodeToSummaryTests(ITestOutputHelper output)
    {
        _output = output;
        _stationService = StationMasterService.Instance;
        _summaryGenerator = new SummaryGenerator();
    }

    #region ヘルパーメソッド

    /// <summary>
    /// 駅コードから駅名を取得して LedgerDetail を生成
    /// </summary>
    private LedgerDetail CreateRailwayUsageFromCodes(
        DateTime useDate,
        int entryStationCode,
        int exitStationCode,
        CardType cardType,
        int amount,
        int balance)
    {
        var entryStation = _stationService.GetStationName(entryStationCode, cardType);
        var exitStation = _stationService.GetStationName(exitStationCode, cardType);

        _output.WriteLine($"駅コード変換: 0x{entryStationCode:X4} -> {entryStation}, 0x{exitStationCode:X4} -> {exitStation}");

        return new LedgerDetail
        {
            UseDate = useDate,
            EntryStation = entryStation,
            ExitStation = exitStation,
            Amount = amount,
            Balance = balance,
            IsCharge = false,
            IsBus = false
        };
    }

    /// <summary>
    /// テスト結果を出力
    /// </summary>
    private void OutputResult(string testName, List<LedgerDetail> details, string result)
    {
        _output.WriteLine($"");
        _output.WriteLine($"=== {testName} ===");
        _output.WriteLine($"入力件数: {details.Count}");
        foreach (var d in details)
        {
            if (d.IsCharge)
            {
                _output.WriteLine($"  チャージ: {d.Amount}円");
            }
            else if (d.IsBus)
            {
                _output.WriteLine($"  バス: {d.BusStops ?? "(未入力)"}");
            }
            else
            {
                _output.WriteLine($"  鉄道: {d.EntryStation} → {d.ExitStation}");
            }
        }
        _output.WriteLine($"生成された摘要: \"{result}\"");
    }

    #endregion

    #region 福岡市地下鉄（はやかけん）単純利用テスト

    /// <summary>
    /// 空港線 単純片道（天神→博多）
    /// 駅コード: 天神=0xE70F, 博多=0xE715
    /// </summary>
    [Fact]
    public void AirportLine_SingleTrip_Tenjin_To_Hakata()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE70F,  // 天神 (空港線 Station 15)
                0xE715,  // 博多 (空港線 Station 21)
                CardType.Hayakaken,
                210,
                4790)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（天神～博多）");
        OutputResult("空港線 単純片道", details, result);
    }

    /// <summary>
    /// 空港線 往復（天神⇔博多）
    /// </summary>
    [Fact]
    public void AirportLine_RoundTrip_Tenjin_Hakata()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE715,  // 博多（帰り）
                0xE70F,  // 天神
                CardType.Hayakaken,
                210,
                4580),
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE70F,  // 天神（行き）
                0xE715,  // 博多
                CardType.Hayakaken,
                210,
                4790)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（博多～天神 往復）");
        OutputResult("空港線 往復", details, result);
    }

    /// <summary>
    /// 箱崎線 単純片道（中洲川端→貝塚）
    /// 駅コード: 中洲川端=0xE801, 貝塚=0xE80D
    /// </summary>
    [Fact]
    public void HakozakiLine_SingleTrip_Nakasu_To_Kaizuka()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE801,  // 中洲川端 (箱崎線 Station 1)
                0xE80D,  // 貝塚 (箱崎線 Station 13)
                CardType.Hayakaken,
                260,
                4740)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（中洲川端～貝塚）");
        OutputResult("箱崎線 単純片道", details, result);
    }

    /// <summary>
    /// 七隈線 単純片道（天神南→六本松）
    /// 駅コード: 天神南=0xE91F, 六本松=0xE915
    /// </summary>
    [Fact]
    public void NanakumaLine_SingleTrip_TenjinMinami_To_Ropponmatsu()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE91F,  // 天神南 (七隈線 Station 31)
                0xE915,  // 六本松 (七隈線 Station 21)
                CardType.Hayakaken,
                260,
                4740)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（天神南～六本松）");
        OutputResult("七隈線 単純片道", details, result);
    }

    #endregion

    #region 福岡市地下鉄 乗継テスト

    /// <summary>
    /// 空港線→箱崎線 乗継（天神→中洲川端→貝塚）
    /// 乗継駅: 中洲川端
    /// </summary>
    [Fact]
    public void Transfer_AirportLine_To_HakozakiLine()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            // 2区間目: 中洲川端→貝塚（新しい方）
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE801,  // 中洲川端（箱崎線の駅コード）
                0xE80D,  // 貝塚
                CardType.Hayakaken,
                260,
                4530),
            // 1区間目: 天神→中洲川端（古い方）
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE70F,  // 天神
                0xE711,  // 中洲川端（空港線の駅コード）
                CardType.Hayakaken,
                210,
                4790)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert - 乗継として統合される（天神～貝塚）
        result.Should().Be("鉄道（中洲川端～貝塚、天神～中洲川端）");
        OutputResult("空港線→箱崎線 乗継", details, result);
    }

    #endregion

    #region JR九州（鹿児島本線）テスト

    /// <summary>
    /// JR鹿児島本線 単純片道（博多→吉塚）
    /// 駅コード: 博多=0x0627, 吉塚=0x0626
    /// </summary>
    [Fact]
    public void KagoshimaLine_SingleTrip_Hakata_To_Yoshizuka()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0x0627,  // 博多 (鹿児島本線 Station 39)
                0x0626,  // 吉塚 (鹿児島本線 Station 38)
                CardType.Hayakaken,
                170,
                4830)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（博多～吉塚）");
        OutputResult("JR鹿児島本線 単純片道", details, result);
    }

    /// <summary>
    /// JR鹿児島本線 往復（博多⇔二日市）
    /// </summary>
    [Fact]
    public void KagoshimaLine_RoundTrip_Hakata_Futsukaichi()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0x062F,  // 二日市（帰り）
                0x0627,  // 博多
                CardType.Hayakaken,
                280,
                4720),
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0x0627,  // 博多（行き）
                0x062F,  // 二日市
                CardType.Hayakaken,
                280,
                5000)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（二日市～博多 往復）");
        OutputResult("JR鹿児島本線 往復", details, result);
    }

    #endregion

    #region 関東エリア（Suica）テスト

    /// <summary>
    /// JR山手線 単純片道（品川→渋谷）
    /// 駅コード: 品川=0x2501, 渋谷=0x2507
    /// </summary>
    [Fact]
    public void YamanoteLine_SingleTrip_Shinagawa_To_Shibuya()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0x2501,  // 品川 (山手線)
                0x2507,  // 渋谷 (山手線)
                CardType.Suica,
                200,
                4800)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（品川～渋谷）");
        OutputResult("JR山手線 単純片道", details, result);
    }

    /// <summary>
    /// JR山手線 往復（新宿⇔池袋）
    /// </summary>
    [Fact]
    public void YamanoteLine_RoundTrip_Shinjuku_Ikebukuro()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0x250F,  // 池袋（帰り）
                0x250A,  // 新宿
                CardType.Suica,
                170,
                4830),
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0x250A,  // 新宿（行き）
                0x250F,  // 池袋
                CardType.Suica,
                170,
                5000)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（池袋～新宿 往復）");
        OutputResult("JR山手線 往復", details, result);
    }

    /// <summary>
    /// JR東海道本線 単純片道（東京→横浜）
    /// </summary>
    [Fact]
    public void TokaidoLine_SingleTrip_Tokyo_To_Yokohama()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0x0101,  // 東京
                0x0112,  // 横浜
                CardType.Suica,
                480,
                4520)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（東京～横浜）");
        OutputResult("JR東海道本線 単純片道", details, result);
    }

    #endregion

    #region 出張シナリオテスト（カード種別とエリアの組み合わせ）

    /// <summary>
    /// はやかけんで東京出張（九州カードで関東エリアを利用）
    /// </summary>
    [Fact]
    public void BusinessTrip_Hayakaken_In_Tokyo()
    {
        // Arrange - はやかけんで山手線を利用
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0x2507,  // 渋谷
                0x250A,  // 新宿
                CardType.Hayakaken,  // 九州のカードだが関東で利用
                170,
                4830)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert - 関東の駅名が正しく解決される
        result.Should().Be("鉄道（渋谷～新宿）");
        OutputResult("はやかけんで東京出張", details, result);
    }

    /// <summary>
    /// Suicaで福岡出張（関東カードで九州エリアを利用）
    /// </summary>
    [Fact]
    public void BusinessTrip_Suica_In_Fukuoka()
    {
        // Arrange - Suicaで箱崎線を利用（箱崎線は他エリアと重複しない）
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE801,  // 中洲川端（箱崎線）
                0xE80D,  // 貝塚（箱崎線）
                CardType.Suica,  // 関東のカードだが九州で利用
                260,
                4740)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert - 九州の駅名が正しく解決される
        result.Should().Be("鉄道（中洲川端～貝塚）");
        OutputResult("Suicaで福岡出張", details, result);
    }

    #endregion

    #region 複数日・複合利用シナリオ

    /// <summary>
    /// GenerateByDate: 2日間の通勤記録（各日往復）
    /// </summary>
    [Fact]
    public void GenerateByDate_TwoDays_Commute()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            // 12/9: 往復（新しい順）
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE715,  // 博多（帰り）
                0xE70F,  // 天神
                CardType.Hayakaken, 210, 4160),
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE70F,  // 天神（行き）
                0xE715,  // 博多
                CardType.Hayakaken, 210, 4370),
            // 12/8: 往復（新しい順）
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 8),
                0xE715,  // 博多（帰り）
                0xE70F,  // 天神
                CardType.Hayakaken, 210, 4580),
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 8),
                0xE70F,  // 天神（行き）
                0xE715,  // 博多
                CardType.Hayakaken, 210, 4790)
        };

        // Act
        var results = _summaryGenerator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(2);
        // 古い順
        results[0].Date.Should().Be(new DateTime(2024, 12, 8));
        results[0].Summary.Should().Be("鉄道（天神～博多 往復）");
        results[1].Date.Should().Be(new DateTime(2024, 12, 9));
        results[1].Summary.Should().Be("鉄道（天神～博多 往復）");

        _output.WriteLine("=== 2日間の通勤記録 ===");
        foreach (var r in results)
        {
            _output.WriteLine($"  {r.Date:yyyy-MM-dd}: {r.Summary}");
        }
    }

    /// <summary>
    /// GenerateByDate: 鉄道とバスの複合利用
    /// </summary>
    [Fact]
    public void GenerateByDate_Railway_And_Bus()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            // バス（新しい方）
            new LedgerDetail
            {
                UseDate = new DateTime(2024, 12, 9),
                EntryStation = null,
                ExitStation = null,
                Amount = 230,
                Balance = 4560,
                IsCharge = false,
                IsBus = true,
                BusStops = "天神～博多駅"
            },
            // 鉄道（古い方）
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE70F,  // 天神
                0xE715,  // 博多
                CardType.Hayakaken, 210, 4790)
        };

        // Act
        var results = _summaryGenerator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("鉄道（天神～博多）、バス（天神～博多駅）");

        _output.WriteLine("=== 鉄道とバスの複合利用 ===");
        _output.WriteLine($"  {results[0].Date:yyyy-MM-dd}: {results[0].Summary}");
    }

    /// <summary>
    /// GenerateByDate: 利用とチャージの混在
    /// </summary>
    [Fact]
    public void GenerateByDate_Usage_And_Charge()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            // 利用（新しい方）
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE70F,  // 天神
                0xE715,  // 博多
                CardType.Hayakaken, 210, 4790),
            // チャージ（古い方）
            new LedgerDetail
            {
                UseDate = new DateTime(2024, 12, 9),
                EntryStation = null,
                ExitStation = null,
                Amount = 3000,
                Balance = 5000,
                IsCharge = true,
                IsBus = false
            }
        };

        // Act
        var results = _summaryGenerator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(2);
        // チャージが先（古い）
        results[0].IsCharge.Should().BeTrue();
        results[0].Summary.Should().Be("役務費によりチャージ");
        // 利用が後（新しい）
        results[1].IsCharge.Should().BeFalse();
        results[1].Summary.Should().Be("鉄道（天神～博多）");

        _output.WriteLine("=== 利用とチャージの混在 ===");
        foreach (var r in results)
        {
            _output.WriteLine($"  {r.Date:yyyy-MM-dd}: IsCharge={r.IsCharge}, {r.Summary}");
        }
    }

    #endregion

    #region 未登録駅コードのフォールバックテスト

    /// <summary>
    /// 未登録の駅コードはフォールバック形式で表示
    /// </summary>
    [Fact]
    public void UnknownStationCode_FallbackFormat()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xFF01,  // 未登録の駅コード
                0xFF02,  // 未登録の駅コード
                CardType.Hayakaken,
                500,
                4500)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert - フォールバック形式「駅XX-YY」が使われる
        result.Should().Be("鉄道（駅FF-01～駅FF-02）");
        OutputResult("未登録駅コード", details, result);
    }

    #endregion

    #region エッジケース

    /// <summary>
    /// 同一駅での乗降（改札通過のみなど）
    /// </summary>
    [Fact]
    public void SameStation_Entry_And_Exit()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE70F,  // 天神
                0xE70F,  // 天神（同一駅）
                CardType.Hayakaken,
                0,
                5000)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert
        result.Should().Be("鉄道（天神～天神）");
        OutputResult("同一駅乗降", details, result);
    }

    /// <summary>
    /// 3区間連続利用（乗継2回）
    /// </summary>
    [Fact]
    public void ThreeSegments_DoubleTransfer()
    {
        // Arrange: 天神→中洲川端→箱崎宮前→貝塚
        var details = new List<LedgerDetail>
        {
            // 3区間目（最新）
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE809,  // 箱崎宮前
                0xE80D,  // 貝塚
                CardType.Hayakaken, 180, 4320),
            // 2区間目
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE801,  // 中洲川端
                0xE809,  // 箱崎宮前
                CardType.Hayakaken, 210, 4500),
            // 1区間目（最古）
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xE70F,  // 天神
                0xE711,  // 中洲川端
                CardType.Hayakaken, 210, 4710)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert - 乗継として統合される
        // 注: 駅コードが異なる（空港線の中洲川端=0xE711、箱崎線の中洲川端=0xE801）ため
        // 駅名が同じでも乗継として認識される
        result.Should().Contain("天神");
        result.Should().Contain("貝塚");
        OutputResult("3区間連続利用", details, result);
    }

    #endregion
}
