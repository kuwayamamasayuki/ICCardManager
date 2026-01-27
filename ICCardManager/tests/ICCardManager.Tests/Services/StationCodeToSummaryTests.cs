using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;
using Xunit.Abstractions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


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

    /// <summary>
    /// テスト入力データを一時保存するリスト
    /// </summary>
    private readonly List<TestInputRecord> _inputRecords = new();

    public StationCodeToSummaryTests(ITestOutputHelper output)
    {
        _output = output;
        _stationService = StationMasterService.Instance;
        _summaryGenerator = new SummaryGenerator();
    }

    #region テスト入力データ記録用

    /// <summary>
    /// テスト入力データを格納するレコード
    /// </summary>
    private record TestInputRecord(
        DateTime UseDate,
        string RecordType,           // "鉄道", "バス", "チャージ"
        int? EntryStationCode,       // 乗車駅コード（鉄道のみ）
        int? ExitStationCode,        // 降車駅コード（鉄道のみ）
        string? EntryStationName,    // 乗車駅名（鉄道のみ）
        string? ExitStationName,     // 降車駅名（鉄道のみ）
        CardType? CardType,          // カード種別（鉄道のみ）
        int Amount,
        int Balance,
        string? BusStops = null      // バス停（バスのみ）
    );

    #endregion

    #region ヘルパーメソッド

    /// <summary>
    /// 入力記録をクリア（各テストの最初に呼び出す）
    /// </summary>
    private void ClearInputRecords()
    {
        _inputRecords.Clear();
    }

    /// <summary>
    /// 駅コードから駅名を取得して LedgerDetail を生成
    /// 同時に入力データを記録する
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

        // 入力データを記録
        _inputRecords.Add(new TestInputRecord(
            useDate, "鉄道",
            entryStationCode, exitStationCode,
            entryStation, exitStation,
            cardType, amount, balance));

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
    /// バス利用の入力データを記録
    /// </summary>
    private void RecordBusInput(DateTime useDate, int amount, int balance, string? busStops)
    {
        _inputRecords.Add(new TestInputRecord(
            useDate, "バス",
            null, null, null, null, null,
            amount, balance, busStops));
    }

    /// <summary>
    /// チャージの入力データを記録
    /// </summary>
    private void RecordChargeInput(DateTime useDate, int amount, int balance)
    {
        _inputRecords.Add(new TestInputRecord(
            useDate, "チャージ",
            null, null, null, null, null,
            amount, balance));
    }

    /// <summary>
    /// カード種別を日本語で取得
    /// </summary>
    private static string GetCardTypeName(CardType? cardType) => cardType switch
    {
        CardType.Hayakaken => "はやかけん",
        CardType.Suica => "Suica",
        CardType.PASMO => "PASMO",
        CardType.ICOCA => "ICOCA",
        CardType.SUGOCA => "SUGOCA",
        CardType.Nimoca => "nimoca",
        CardType.Kitaca => "Kitaca",
        CardType.TOICA => "TOICA",
        CardType.Manaca => "manaca",
        CardType.PiTaPa => "PiTaPa",
        _ => "不明"
    };

    /// <summary>
    /// テスト結果を詳細に出力
    /// </summary>
    private void OutputTestResult(string testName, string result)
    {
        _output.WriteLine("");
        _output.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine($"テスト: {testName}");
        _output.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("");
        _output.WriteLine("【ICカード履歴データ（入力）】");
        _output.WriteLine($"  件数: {_inputRecords.Count}");
        _output.WriteLine("");

        for (int i = 0; i < _inputRecords.Count; i++)
        {
            var rec = _inputRecords[i];
            _output.WriteLine($"  [{i + 1}] {rec.RecordType}");
            _output.WriteLine($"      日時: {rec.UseDate:yyyy-MM-dd}");

            if (rec.RecordType == "鉄道")
            {
                _output.WriteLine($"      カード種別: {GetCardTypeName(rec.CardType)}");
                _output.WriteLine($"      乗車駅: 0x{rec.EntryStationCode:X4} → {rec.EntryStationName}");
                _output.WriteLine($"      降車駅: 0x{rec.ExitStationCode:X4} → {rec.ExitStationName}");
                _output.WriteLine($"      金額: {rec.Amount}円");
                _output.WriteLine($"      残高: {rec.Balance}円");
            }
            else if (rec.RecordType == "バス")
            {
                _output.WriteLine($"      バス停: {rec.BusStops ?? "(未入力)"}");
                _output.WriteLine($"      金額: {rec.Amount}円");
                _output.WriteLine($"      残高: {rec.Balance}円");
            }
            else if (rec.RecordType == "チャージ")
            {
                _output.WriteLine($"      金額: {rec.Amount}円");
                _output.WriteLine($"      残高: {rec.Balance}円");
            }
            _output.WriteLine("");
        }

        _output.WriteLine("【生成された摘要（出力）】");
        _output.WriteLine($"  {result}");
        _output.WriteLine("");
    }

    /// <summary>
    /// GenerateByDate用のテスト結果を詳細に出力
    /// </summary>
    private void OutputTestResultByDate(string testName, List<DailySummary> results)
    {
        _output.WriteLine("");
        _output.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine($"テスト: {testName}");
        _output.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("");
        _output.WriteLine("【ICカード履歴データ（入力）】");
        _output.WriteLine($"  件数: {_inputRecords.Count}");
        _output.WriteLine("");

        for (int i = 0; i < _inputRecords.Count; i++)
        {
            var rec = _inputRecords[i];
            _output.WriteLine($"  [{i + 1}] {rec.RecordType}");
            _output.WriteLine($"      日時: {rec.UseDate:yyyy-MM-dd}");

            if (rec.RecordType == "鉄道")
            {
                _output.WriteLine($"      カード種別: {GetCardTypeName(rec.CardType)}");
                _output.WriteLine($"      乗車駅: 0x{rec.EntryStationCode:X4} → {rec.EntryStationName}");
                _output.WriteLine($"      降車駅: 0x{rec.ExitStationCode:X4} → {rec.ExitStationName}");
                _output.WriteLine($"      金額: {rec.Amount}円");
                _output.WriteLine($"      残高: {rec.Balance}円");
            }
            else if (rec.RecordType == "バス")
            {
                _output.WriteLine($"      バス停: {rec.BusStops ?? "(未入力)"}");
                _output.WriteLine($"      金額: {rec.Amount}円");
                _output.WriteLine($"      残高: {rec.Balance}円");
            }
            else if (rec.RecordType == "チャージ")
            {
                _output.WriteLine($"      金額: {rec.Amount}円");
                _output.WriteLine($"      残高: {rec.Balance}円");
            }
            _output.WriteLine("");
        }

        _output.WriteLine("【生成された摘要（出力）】");
        _output.WriteLine($"  件数: {results.Count}");
        foreach (var r in results)
        {
            var typeLabel = r.IsCharge ? "[チャージ]" : "[利用]";
            _output.WriteLine($"  {r.Date:yyyy-MM-dd} {typeLabel}: {r.Summary}");
        }
        _output.WriteLine("");
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
        ClearInputRecords();
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
        OutputTestResult("空港線 単純片道", result);
    }

    /// <summary>
    /// 空港線 往復（天神⇔博多）
    /// </summary>
    [Fact]
    public void AirportLine_RoundTrip_Tenjin_Hakata()
    {
        // Arrange
        ClearInputRecords();
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

        // Assert: 残高ソートにより行き（天神→博多, 残額高い=先に利用）が基準
        result.Should().Be("鉄道（天神～博多 往復）");
        OutputTestResult("空港線 往復", result);
    }

    /// <summary>
    /// 箱崎線 単純片道（中洲川端→貝塚）
    /// 駅コード: 中洲川端=0xE801, 貝塚=0xE80D
    /// </summary>
    [Fact]
    public void HakozakiLine_SingleTrip_Nakasu_To_Kaizuka()
    {
        // Arrange
        ClearInputRecords();
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
        OutputTestResult("箱崎線 単純片道", result);
    }

    /// <summary>
    /// 七隈線 単純片道（天神南→六本松）
    /// 駅コード: 天神南=0xE91F, 六本松=0xE915
    /// </summary>
    [Fact]
    public void NanakumaLine_SingleTrip_TenjinMinami_To_Ropponmatsu()
    {
        // Arrange
        ClearInputRecords();
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
        OutputTestResult("七隈線 単純片道", result);
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
        ClearInputRecords();
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

        // Assert - 残高ソートにより天神→中洲川端が先になり、乗継として統合される
        result.Should().Be("鉄道（天神～貝塚）");
        OutputTestResult("空港線→箱崎線 乗継", result);
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
        ClearInputRecords();
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
        OutputTestResult("JR鹿児島本線 単純片道", result);
    }

    /// <summary>
    /// JR鹿児島本線 往復（博多⇔二日市）
    /// </summary>
    [Fact]
    public void KagoshimaLine_RoundTrip_Hakata_Futsukaichi()
    {
        // Arrange
        ClearInputRecords();
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

        // Assert: 残高ソートにより行き（博多→二日市, 残額高い=先に利用）が基準
        result.Should().Be("鉄道（博多～二日市 往復）");
        OutputTestResult("JR鹿児島本線 往復", result);
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
        ClearInputRecords();
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
        OutputTestResult("JR山手線 単純片道", result);
    }

    /// <summary>
    /// JR山手線 往復（新宿⇔池袋）
    /// </summary>
    [Fact]
    public void YamanoteLine_RoundTrip_Shinjuku_Ikebukuro()
    {
        // Arrange
        ClearInputRecords();
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

        // Assert: 残高ソートにより行き（新宿→池袋, 残額高い=先に利用）が基準
        result.Should().Be("鉄道（新宿～池袋 往復）");
        OutputTestResult("JR山手線 往復", result);
    }

    /// <summary>
    /// JR東海道本線 単純片道（東京→横浜）
    /// </summary>
    [Fact]
    public void TokaidoLine_SingleTrip_Tokyo_To_Yokohama()
    {
        // Arrange
        ClearInputRecords();
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
        OutputTestResult("JR東海道本線 単純片道", result);
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
        ClearInputRecords();
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
        OutputTestResult("はやかけんで東京出張", result);
    }

    /// <summary>
    /// 駅コード重複時のカード種別優先順位の検証
    /// </summary>
    /// <remarks>
    /// 同じ駅コードが複数エリアに存在する場合、カード種別に応じた
    /// 優先エリアの駅名が返される。
    /// 0xE70F: 九州では「天神」、関西では「高安」
    /// Suica（関東優先→関西→中部→九州）では関西の「高安」が返される。
    /// </remarks>
    [Fact]
    public void StationCodeOverlap_CardTypePriority()
    {
        // Arrange - 同じ駅コードでカード種別が異なる場合
        var entryCode = 0xE70F;
        var exitCode = 0xE715;

        // はやかけん（九州優先）で解決
        var hayakakenEntry = _stationService.GetStationName(entryCode, CardType.Hayakaken);
        var hayakakenExit = _stationService.GetStationName(exitCode, CardType.Hayakaken);

        // Suica（関東優先→関西→中部→九州）で解決
        var suicaEntry = _stationService.GetStationName(entryCode, CardType.Suica);
        var suicaExit = _stationService.GetStationName(exitCode, CardType.Suica);

        _output.WriteLine("");
        _output.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("テスト: 駅コード重複時のカード種別優先順位");
        _output.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("");
        _output.WriteLine("【入力駅コード】");
        _output.WriteLine($"  乗車駅コード: 0x{entryCode:X4}");
        _output.WriteLine($"  降車駅コード: 0x{exitCode:X4}");
        _output.WriteLine("");
        _output.WriteLine("【カード種別による駅名解決結果】");
        _output.WriteLine($"  はやかけん（九州優先）: 0x{entryCode:X4} → {hayakakenEntry}, 0x{exitCode:X4} → {hayakakenExit}");
        _output.WriteLine($"  Suica（関東→関西→中部→九州）: 0x{entryCode:X4} → {suicaEntry}, 0x{exitCode:X4} → {suicaExit}");
        _output.WriteLine("");

        // Assert - カード種別により異なる駅名が返される
        hayakakenEntry.Should().Be("天神");
        hayakakenExit.Should().Be("博多");
        suicaEntry.Should().Be("高安");      // 関西エリアの駅
        suicaExit.Should().Be("河内国分");   // 関西エリアの駅
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
        ClearInputRecords();
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

        OutputTestResultByDate("2日間の通勤記録", results);
    }

    /// <summary>
    /// GenerateByDate: 鉄道とバスの複合利用
    /// </summary>
    [Fact]
    public void GenerateByDate_Railway_And_Bus()
    {
        // Arrange
        ClearInputRecords();
        // バス（新しい方）
        RecordBusInput(new DateTime(2024, 12, 9), 230, 4560, "天神～博多駅");
        var details = new List<LedgerDetail>
        {
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

        OutputTestResultByDate("鉄道とバスの複合利用", results);
    }

    /// <summary>
    /// GenerateByDate: 利用とチャージの混在
    /// </summary>
    [Fact]
    public void GenerateByDate_Usage_And_Charge()
    {
        // Arrange
        ClearInputRecords();
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
        // チャージを記録
        RecordChargeInput(new DateTime(2024, 12, 9), 3000, 5000);

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

        OutputTestResultByDate("利用とチャージの混在", results);
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
        ClearInputRecords();
        // 0xFFFE, 0xFFFF は確実に未登録のコード
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsageFromCodes(
                new DateTime(2024, 12, 9),
                0xFFFE,  // 未登録の駅コード（路線255, 駅254）
                0xFFFF,  // 未登録の駅コード（路線255, 駅255）
                CardType.Hayakaken,
                500,
                4500)
        };

        // Act
        var result = _summaryGenerator.Generate(details);

        // Assert - フォールバック形式「駅XX-YY」が使われる
        result.Should().Be("鉄道（駅FF-FE～駅FF-FF）");
        OutputTestResult("未登録駅コード", result);
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
        ClearInputRecords();
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
        OutputTestResult("同一駅乗降", result);
    }

    /// <summary>
    /// 3区間連続利用（乗継2回）
    /// </summary>
    [Fact]
    public void ThreeSegments_DoubleTransfer()
    {
        // Arrange: 天神→中洲川端→箱崎宮前→貝塚
        ClearInputRecords();
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
        OutputTestResult("3区間連続利用", result);
    }

    #endregion
}
