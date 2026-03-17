using FluentAssertions;
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
/// SummaryGenerator の包括的テスト
///
/// ■ テスト対象メソッド
/// - GenerateByDate: 日付ごとに分割し、利用とチャージを別行にして返す（新仕様）
/// - Generate: 従来のメソッド（互換性維持）
///
/// ■ 新仕様の要件
/// 1. 履歴は日付ごとに分割する
/// 2. 鉄道・バスの利用とチャージは別の行に記載する
/// 3. ICカード履歴は新しい順だが、物品出納簿は古い順にする
///
/// ■ 使用駅（福岡市内の3社）
///
/// 【JR九州 鹿児島本線】地区0x00, 線区0x06
/// - 香椎(35), 千早(36), 箱崎(37), 吉塚(38), 博多(39), 竹下(40), 南福岡(42), 春日(43), 大野城(44)
///
/// 【福岡市交通局 空港線(1号線)】地区0x03, 線区0xE7(231)
/// - 姪浜(1), 室見(3), 藤崎(5), 西新(7), 唐人町(9), 大濠公園(11), 赤坂(13), 天神(15),
///   中洲川端(17), 祇園(19), 博多(21), 東比恵(23), 福岡空港(25)
///
/// 【福岡市交通局 箱崎線(2号線)】地区0x03, 線区0xE8(232)
/// - 中洲川端(1), 呉服町(3), 千代県庁口(5), 馬出九大病院前(7), 箱崎宮前(9), 箱崎九大前(11), 貝塚(13)
///
/// 【福岡市交通局 七隈線(3号線)】地区0x03, 線区0xE9(233)
/// - 橋本(1), 次郎丸(3), 賀茂(5), 野芥(7), 梅林(9), 福大前(11), 七隈(13), 金山(15),
///   茶山(17), 別府(19), 六本松(21), 桜坂(23), 薬院大通(25), 薬院(27), 渡辺通(29), 天神南(31)
///
/// 【西日本鉄道 天神大牟田線】地区0x03, 線区0xD7(215)
/// - 西鉄福岡(天神)(101), 薬院(103), 西鉄平尾(105), 高宮(107), 大橋(109), 井尻(111),
///   雑餉隈(113), 春日原(115), 白木原(117), 下大利(119), 都府楼前(121), 西鉄二日市(123)
/// </summary>
public class SummaryGeneratorComprehensiveTests
{
    private readonly SummaryGenerator _generator = new();
    private readonly ITestOutputHelper _output;

    public SummaryGeneratorComprehensiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    #region ヘルパーメソッド

    private static LedgerDetail CreateRailwayUsage(
        DateTime useDate,
        string entryStation,
        string exitStation,
        int amount,
        int balance)
    {
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

    private static LedgerDetail CreateBusUsage(
        DateTime useDate,
        int amount,
        int balance,
        string? busStops = null)
    {
        return new LedgerDetail
        {
            UseDate = useDate,
            EntryStation = null,
            ExitStation = null,
            Amount = amount,
            Balance = balance,
            IsCharge = false,
            IsBus = true,
            BusStops = busStops
        };
    }

    private static LedgerDetail CreateCharge(
        DateTime useDate,
        int amount,
        int balance)
    {
        return new LedgerDetail
        {
            UseDate = useDate,
            EntryStation = null,
            ExitStation = null,
            Amount = amount,
            Balance = balance,
            IsCharge = true,
            IsBus = false
        };
    }

    /// <summary>
    /// 入力データを整形して出力
    /// </summary>
    private void OutputInput(List<LedgerDetail> details)
    {
        _output.WriteLine("【入力（ICカード履歴データ、新しい順）】");
        if (details.Count == 0)
        {
            _output.WriteLine("  (空)");
            return;
        }
        for (int i = 0; i < details.Count; i++)
        {
            var d = details[i];
            var type = d.IsCharge ? "チャージ" : (d.IsBus ? "バス    " : "鉄道    ");
            var route = d.IsCharge ? "-" :
                        d.IsBus ? (d.BusStops ?? "(未入力)") :
                        $"{d.EntryStation ?? "(null)"}→{d.ExitStation ?? "(null)"}";
            _output.WriteLine($"  [{i}] {d.UseDate:yyyy-MM-dd} {type} {route,-30} {d.Amount,5}円 残{d.Balance,5}円");
        }
    }

    /// <summary>
    /// GenerateByDateの結果を出力
    /// </summary>
    private void OutputResult(List<DailySummary> results)
    {
        _output.WriteLine("【出力（物品出納簿、古い順）】");
        _output.WriteLine($"  出力件数: {results.Count}");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            _output.WriteLine($"  [{i}] {r.Date:yyyy-MM-dd} IsCharge={r.IsCharge,-5} Summary=\"{r.Summary}\"");
        }
    }

    /// <summary>
    /// 入力と出力を両方出力
    /// </summary>
    private void OutputInputAndResult(List<LedgerDetail> input, List<DailySummary> output)
    {
        OutputInput(input);
        _output.WriteLine("");
        OutputResult(output);
    }

    #endregion

    #region カテゴリ1: GenerateByDate - 基本動作

    [Fact]
    public void TC001_履歴なし_空リストを返す()
    {
        // Arrange
        var details = new List<LedgerDetail>();

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().BeEmpty();
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC002_1日1回利用_1件を返す()
    {
        // Arrange: 12/9に天神→博多の片道利用
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4790)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(1);
        results[0].Date.Should().Be(new DateTime(2024, 12, 9));
        results[0].IsCharge.Should().BeFalse();
        results[0].Summary.Should().Be("鉄道（天神～博多）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC003_1日チャージのみ_1件を返す()
    {
        // Arrange: 12/5にチャージのみ
        var details = new List<LedgerDetail>
        {
            CreateCharge(new DateTime(2024, 12, 5), 3000, 8000)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(1);
        results[0].Date.Should().Be(new DateTime(2024, 12, 5));
        results[0].IsCharge.Should().BeTrue();
        results[0].Summary.Should().Be("役務費によりチャージ");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC004_1日利用とチャージ_利用が先_2件を返す()
    {
        // Arrange: 12/9に利用とチャージ（同日でも別行）
        // 時系列: 利用(3000→2790)→チャージ(2790→5790)
        // ICカード履歴は新しい順：[0]チャージ(新しい)、[1]利用(古い)
        // 残高チェーンにより時系列を判定し、利用→チャージの順で出力
        var details = new List<LedgerDetail>
        {
            CreateCharge(new DateTime(2024, 12, 9), 3000, 5790),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 2790)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(2);
        // 利用が先（時系列で古い）
        results[0].Date.Should().Be(new DateTime(2024, 12, 9));
        results[0].IsCharge.Should().BeFalse();
        results[0].Summary.Should().Be("鉄道（天神～博多）");
        // チャージが後（時系列で新しい）
        results[1].Date.Should().Be(new DateTime(2024, 12, 9));
        results[1].IsCharge.Should().BeTrue();
        results[1].Summary.Should().Be("役務費によりチャージ");
        OutputInputAndResult(details, results);
    }

    #endregion

    #region カテゴリ2: GenerateByDate - 複数日

    [Fact]
    public void TC005_3日間利用_古い順に3件を返す()
    {
        // Arrange: 11/30, 12/1, 12/2の3日間利用（ICカードは新しい順）
        var details = new List<LedgerDetail>
        {
            // 新しい順（ICカードから読み取った順）
            CreateRailwayUsage(new DateTime(2024, 12, 2), "天神", "博多", 210, 4370),
            CreateRailwayUsage(new DateTime(2024, 12, 1), "博多", "天神", 210, 4580),
            CreateRailwayUsage(new DateTime(2024, 11, 30), "天神", "博多", 210, 4790)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 古い順にソートされる
        results.Should().HaveCount(3);
        results[0].Date.Should().Be(new DateTime(2024, 11, 30));
        results[0].Summary.Should().Be("鉄道（天神～博多）");
        results[1].Date.Should().Be(new DateTime(2024, 12, 1));
        results[1].Summary.Should().Be("鉄道（博多～天神）");
        results[2].Date.Should().Be(new DateTime(2024, 12, 2));
        results[2].Summary.Should().Be("鉄道（天神～博多）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC006_3日間チャージ_古い順に3件を返す()
    {
        // Arrange: 12/1, 12/5, 12/8のチャージ（ICカードは新しい順）
        var details = new List<LedgerDetail>
        {
            CreateCharge(new DateTime(2024, 12, 8), 2000, 12000),
            CreateCharge(new DateTime(2024, 12, 5), 3000, 10000),
            CreateCharge(new DateTime(2024, 12, 1), 5000, 7000)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 古い順
        results.Should().HaveCount(3);
        results[0].Date.Should().Be(new DateTime(2024, 12, 1));
        results[1].Date.Should().Be(new DateTime(2024, 12, 5));
        results[2].Date.Should().Be(new DateTime(2024, 12, 8));
        results.Should().OnlyContain(item => item.IsCharge == true);
        results.Should().AllSatisfy(r => r.Summary.Should().Be("役務費によりチャージ"));
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC007_複数日_利用とチャージ混在_日付別に分離()
    {
        // Arrange: 11/30利用、12/1利用+チャージ、12/2チャージ
        // ICカード履歴は新しい順
        // 12/1の残額：チャージ(5210円)→利用(5000円)の順
        var details = new List<LedgerDetail>
        {
            // 12/2 チャージのみ
            CreateCharge(new DateTime(2024, 12, 2), 3000, 8000),
            // 12/1 利用とチャージ（利用が新しい[1]、チャージが古い[2]）
            CreateRailwayUsage(new DateTime(2024, 12, 1), "博多", "天神", 210, 5000),
            CreateCharge(new DateTime(2024, 12, 1), 2000, 5210),
            // 11/30 利用のみ
            CreateRailwayUsage(new DateTime(2024, 11, 30), "天神", "博多", 210, 3210)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 4件（11/30利用、12/1チャージ、12/1利用、12/2チャージ）
        results.Should().HaveCount(4);

        // 11/30: 利用
        results[0].Date.Should().Be(new DateTime(2024, 11, 30));
        results[0].IsCharge.Should().BeFalse();
        results[0].Summary.Should().Be("鉄道（天神～博多）");

        // 12/1: チャージが先（古い）
        results[1].Date.Should().Be(new DateTime(2024, 12, 1));
        results[1].IsCharge.Should().BeTrue();
        results[1].Summary.Should().Be("役務費によりチャージ");

        // 12/1: 利用が後（新しい）
        results[2].Date.Should().Be(new DateTime(2024, 12, 1));
        results[2].IsCharge.Should().BeFalse();
        results[2].Summary.Should().Be("鉄道（博多～天神）");

        // 12/2: チャージ
        results[3].Date.Should().Be(new DateTime(2024, 12, 2));
        results[3].IsCharge.Should().BeTrue();
        results[3].Summary.Should().Be("役務費によりチャージ");

        OutputInputAndResult(details, results);
    }

    #endregion

    #region カテゴリ3: GenerateByDate - 同日複数利用

    [Fact]
    public void TC008_同日往復_1件にまとめる()
    {
        // Arrange: 12/9に天神～博多の往復
        // ICカード履歴は新しい順：[0]帰り(新しい)、[1]行き(古い)
        // 往復表示は古い方（行き）を基準にする
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "天神", 210, 4580),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4790)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 同日は1件にまとめられる（古い方=行きを基準に）
        results.Should().HaveCount(1);
        results[0].Date.Should().Be(new DateTime(2024, 12, 9));
        results[0].IsCharge.Should().BeFalse();
        results[0].Summary.Should().Be("鉄道（天神～博多 往復）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC009_同日鉄道とバス_1件にまとめる()
    {
        // Arrange: 12/9に鉄道とバスを利用
        var details = new List<LedgerDetail>
        {
            CreateBusUsage(new DateTime(2024, 12, 9), 230, 4560, busStops: "天神～博多駅"),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4790)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(1);
        results[0].Date.Should().Be(new DateTime(2024, 12, 9));
        results[0].IsCharge.Should().BeFalse();
        results[0].Summary.Should().Be("鉄道（天神～博多）、バス（天神～博多駅）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC010_同日乗継_1件にまとめる()
    {
        // Arrange: 12/9に地下鉄乗継（天神→中洲川端→貝塚）
        // ICカード履歴は新しい順：[0]乗継後(新しい)、[1]最初の乗車(古い)
        // 実際の順序：天神→中洲川端（古い）→中洲川端→貝塚（新しい）
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "中洲川端", "貝塚", 260, 4530),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "中洲川端", 210, 4790)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 乗継として統合される
        results.Should().HaveCount(1);
        results[0].Date.Should().Be(new DateTime(2024, 12, 9));
        results[0].Summary.Should().Be("鉄道（天神～貝塚）");
        OutputInputAndResult(details, results);
    }

    #endregion

    #region カテゴリ4: GenerateByDate - 実務シナリオ

    [Fact]
    public void TC011_5日間通勤_5件を返す()
    {
        // Arrange: 5日間の通勤記録（各日往復）
        // TC008/TC012/TC014と同じデータパターンで統一
        // ICカード履歴は新しい順：[偶数] 帰り(新しい・残額低い)、[奇数] 行き(古い・残額高い)
        //
        // 時系列順（古い→新しい）：
        //   12/5 AM 天神→博多 残9790 → 12/5 PM 博多→天神 残9580
        //   12/6 AM 天神→博多 残9370 → 12/6 PM 博多→天神 残9160
        //   ...
        //   12/9 AM 天神→博多 残8110 → 12/9 PM 博多→天神 残7900
        //
        // ICカード格納順（新しい→古い）：
        //   [0] 12/9 PM 博多→天神 残7900 → [1] 12/9 AM 天神→博多 残8110
        //   [2] 12/8 PM 博多→天神 残8320 → [3] 12/8 AM 天神→博多 残8530
        //   ...
        //   [8] 12/5 PM 博多→天神 残9580 → [9] 12/5 AM 天神→博多 残9790
        var details = new List<LedgerDetail>();

        for (int day = 0; day < 5; day++)
        {
            var date = new DateTime(2024, 12, 9 - day);
            // [偶数インデックス] 博多→天神（帰り=夕方、残額が低い＝新しい取引）
            details.Add(CreateRailwayUsage(date, "博多", "天神", 210, 7900 + 420 * day));
            // [奇数インデックス] 天神→博多（行き=朝、残額が高い＝古い取引）
            details.Add(CreateRailwayUsage(date, "天神", "博多", 210, 8110 + 420 * day));
        }

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 5日間なので5件
        results.Should().HaveCount(5);
        // 古い順（12/5, 12/6, 12/7, 12/8, 12/9）
        results[0].Date.Should().Be(new DateTime(2024, 12, 5));
        results[4].Date.Should().Be(new DateTime(2024, 12, 9));
        // 往復の方向を明示的に検証（TC008/TC012/TC014と同じ結果になるはず）
        results.Should().AllSatisfy(r => r.Summary.Should().Be("鉄道（天神～博多 往復）"));
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC012_週間利用_チャージ含む_複数件()
    {
        // Arrange: 1週間の履歴（利用4日、チャージ1日）
        // ICカード履歴は新しい順
        // 通勤シナリオ：天神在住、博多勤務
        // - 行き（朝）：天神→博多（古い、インデックス大）
        // - 帰り（夕）：博多→天神（新しい、インデックス小）
        // 12/4の残額：チャージ(2260円)→利用(2050円)の順
        var details = new List<LedgerDetail>
        {
            // 12/9（月）利用 [0]帰り(新), [1]行き(古)
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "天神", 210, 3790),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4000),
            // 12/8（日）チャージ
            CreateCharge(new DateTime(2024, 12, 8), 3000, 4210),
            // 12/6（金）利用 [3]帰り(新), [4]行き(古)
            CreateRailwayUsage(new DateTime(2024, 12, 6), "博多", "天神", 210, 1210),
            CreateRailwayUsage(new DateTime(2024, 12, 6), "天神", "博多", 210, 1420),
            // 12/5（木）利用 [5]帰り(新), [6]行き(古)
            CreateRailwayUsage(new DateTime(2024, 12, 5), "博多", "天神", 210, 1630),
            CreateRailwayUsage(new DateTime(2024, 12, 5), "天神", "博多", 210, 1840),
            // 12/4（水）利用とチャージ（利用[7]が新しい、チャージ[8]が古い）
            // 12/4は片道のみ（博多→天神）
            CreateRailwayUsage(new DateTime(2024, 12, 4), "博多", "天神", 210, 2050),
            CreateCharge(new DateTime(2024, 12, 4), 2000, 2260),
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 6件（12/4チャージ、12/4利用、12/5利用、12/6利用、12/8チャージ、12/9利用）
        results.Should().HaveCount(6);

        // 12/4：チャージが先（古い）
        results[0].Date.Should().Be(new DateTime(2024, 12, 4));
        results[0].IsCharge.Should().BeTrue();

        // 12/4：利用が後（新しい）- 片道のみ
        results[1].Date.Should().Be(new DateTime(2024, 12, 4));
        results[1].IsCharge.Should().BeFalse();
        results[1].Summary.Should().Be("鉄道（博多～天神）");

        // 12/5, 12/6, 12/9：往復（古い方=天神→博多を基準に表示）
        results[2].Date.Should().Be(new DateTime(2024, 12, 5));
        results[2].Summary.Should().Be("鉄道（天神～博多 往復）");

        results[3].Date.Should().Be(new DateTime(2024, 12, 6));
        results[3].Summary.Should().Be("鉄道（天神～博多 往復）");

        results[4].Date.Should().Be(new DateTime(2024, 12, 8));
        results[4].IsCharge.Should().BeTrue();

        results[5].Date.Should().Be(new DateTime(2024, 12, 9));
        results[5].Summary.Should().Be("鉄道（天神～博多 往復）");

        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC013_出張パターン_乗継とバス()
    {
        // Arrange: 12/9の出張（乗継+バス）
        // ICカード履歴は新しい順：最新[0]→最古[3]
        // 実際の順序：天神→中洲川端→箱崎宮前（乗継）→バス2本
        var details = new List<LedgerDetail>
        {
            CreateBusUsage(new DateTime(2024, 12, 9), 190, 4110, busStops: "九大キャンパス～博多駅"),
            CreateBusUsage(new DateTime(2024, 12, 9), 230, 4300, busStops: "箱崎駅前～九大キャンパス"),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "中洲川端", "箱崎宮前", 260, 4530),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "中洲川端", 210, 4790),
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 鉄道は乗継統合、バスも乗り継ぎ統合される
        results.Should().HaveCount(1);
        results[0].Date.Should().Be(new DateTime(2024, 12, 9));
        results[0].Summary.Should().Be("鉄道（天神～箱崎宮前）、バス（箱崎駅前～博多駅）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC014_20件フル履歴()
    {
        // Arrange: ICカード履歴上限（20件）に近い複雑なデータ
        // 通勤シナリオ：天神在住、博多勤務
        // - 行き（朝）：天神→博多（古い、インデックス大）
        // - 帰り（夕）：博多→天神（新しい、インデックス小）
        var details = new List<LedgerDetail>
        {
            // Week2
            // 12/9: [0]帰り(新), [1]行き(古)
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "天神", 210, 2440),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 2650),
            // 12/8: 循環移動 天神→姪浜→西新→天神 + バス
            CreateBusUsage(new DateTime(2024, 12, 8), 190, 2860, busStops: "姪浜駅前～百道浜"),
            CreateRailwayUsage(new DateTime(2024, 12, 8), "西新", "天神", 260, 3050),
            CreateRailwayUsage(new DateTime(2024, 12, 8), "姪浜", "西新", 210, 3310),
            CreateRailwayUsage(new DateTime(2024, 12, 8), "天神", "姪浜", 260, 3520),
            // Week1
            // 12/6: [6]帰り(新), [7]行き(古)
            CreateRailwayUsage(new DateTime(2024, 12, 6), "博多", "天神", 210, 3780),
            CreateRailwayUsage(new DateTime(2024, 12, 6), "天神", "博多", 210, 3990),
            // 12/5: [8]帰り(新), [9]行き(古)
            CreateRailwayUsage(new DateTime(2024, 12, 5), "博多", "天神", 210, 4200),
            CreateRailwayUsage(new DateTime(2024, 12, 5), "天神", "博多", 210, 4410),
            // 先週
            // 12/4: [10-11]バス, [12]帰り(新), [13]行き(古)
            CreateBusUsage(new DateTime(2024, 12, 4), 230, 4620, busStops: "天神～博多駅"),
            CreateBusUsage(new DateTime(2024, 12, 4), 190, 4850, busStops: "博多駅～キャナルシティ前"),
            CreateRailwayUsage(new DateTime(2024, 12, 4), "博多", "天神", 210, 5040),
            CreateRailwayUsage(new DateTime(2024, 12, 4), "天神", "博多", 210, 5250),
            // 2週間前
            // 12/2: 別路線（西鉄）[14]帰り(新), [15]行き(古)
            CreateRailwayUsage(new DateTime(2024, 12, 2), "大橋", "薬院", 220, 5460),
            CreateRailwayUsage(new DateTime(2024, 12, 2), "薬院", "大橋", 220, 5680),
            // 12/1: [16]帰り(新), [17]行き(古)
            CreateRailwayUsage(new DateTime(2024, 12, 1), "博多", "天神", 210, 5900),
            CreateRailwayUsage(new DateTime(2024, 12, 1), "天神", "博多", 210, 6110),
            // 11/30: [18]帰り(新), [19]行き(古)
            CreateRailwayUsage(new DateTime(2024, 11, 30), "博多", "天神", 210, 6320),
            CreateRailwayUsage(new DateTime(2024, 11, 30), "天神", "博多", 210, 6530),
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 8日分（11/30, 12/1, 12/2, 12/4, 12/5, 12/6, 12/8, 12/9）
        results.Should().HaveCount(8);

        // 古い順（全て古い方を基準に往復表示）
        results[0].Date.Should().Be(new DateTime(2024, 11, 30));
        results[0].Summary.Should().Be("鉄道（天神～博多 往復）");

        results[1].Date.Should().Be(new DateTime(2024, 12, 1));
        results[1].Summary.Should().Be("鉄道（天神～博多 往復）");

        results[2].Date.Should().Be(new DateTime(2024, 12, 2));
        results[2].Summary.Should().Be("鉄道（薬院～大橋 往復）");

        // 12/4: 往復 + バス（バスは古い順）
        results[3].Date.Should().Be(new DateTime(2024, 12, 4));
        results[3].Summary.Should().Be("鉄道（天神～博多 往復）、バス（博多駅～キャナルシティ前、天神～博多駅）");

        results[4].Date.Should().Be(new DateTime(2024, 12, 5));
        results[4].Summary.Should().Be("鉄道（天神～博多 往復）");

        results[5].Date.Should().Be(new DateTime(2024, 12, 6));
        results[5].Summary.Should().Be("鉄道（天神～博多 往復）");

        // 12/8: 循環移動（天神→姪浜→西新→天神）は個別区間表示
        results[6].Date.Should().Be(new DateTime(2024, 12, 8));
        results[6].Summary.Should().Be("鉄道（天神～姪浜、姪浜～西新、西新～天神）、バス（姪浜駅前～百道浜）");

        results[7].Date.Should().Be(new DateTime(2024, 12, 9));
        results[7].Summary.Should().Be("鉄道（天神～博多 往復）");

        OutputInputAndResult(details, results);
    }

    #endregion

    #region カテゴリ5: GenerateByDate - バス利用

    [Fact]
    public void TC015_バス未入力_星マーク()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateBusUsage(new DateTime(2024, 12, 9), 230, 4770, busStops: null)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("バス（★）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC016_バス入力済()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            CreateBusUsage(new DateTime(2024, 12, 9), 230, 4770, busStops: "天神バスセンター～博多駅")
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("バス（天神バスセンター～博多駅）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC017_バス複数_同日()
    {
        // Arrange
        // ICカード履歴は新しい順：[0]2回目(新しい)、[1]1回目(古い)
        // 実際の順序：天神→博多駅（古い）→博多駅→キャナルシティ（新しい）
        var details = new List<LedgerDetail>
        {
            CreateBusUsage(new DateTime(2024, 12, 9), 190, 4350, busStops: "博多駅～キャナルシティ前"),
            CreateBusUsage(new DateTime(2024, 12, 9), 230, 4540, busStops: "天神バスセンター～博多駅")
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 乗り継ぎ統合される（博多駅で乗り継ぎ）
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("バス（天神バスセンター～キャナルシティ前）");
        OutputInputAndResult(details, results);
    }

    #endregion

    #region カテゴリ6: GenerateByDate - エッジケース

    [Fact]
    public void TC018_駅コード取得失敗_空出力()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2024, 12, 9),
                EntryStation = null,
                ExitStation = null,
                Amount = 260,
                Balance = 4740,
                IsCharge = false,
                IsBus = false
            }
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 摘要が空なので出力されない
        results.Should().BeEmpty();
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC019_入場記録のみ_空出力()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2024, 12, 9),
                EntryStation = "天神",
                ExitStation = null,
                Amount = 0,
                Balance = 5000,
                IsCharge = false,
                IsBus = false
            }
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().BeEmpty();
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC020_同一駅乗降()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2024, 12, 9),
                EntryStation = "天神",
                ExitStation = "天神",
                Amount = 0,
                Balance = 5000,
                IsCharge = false,
                IsBus = false
            }
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("鉄道（天神～天神）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC021_長い駅名()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2024, 12, 9),
                EntryStation = "南阿蘇水の生まれる里白水高原",
                ExitStation = "阿蘇下田城ふれあい温泉",
                Amount = 500,
                Balance = 4500,
                IsCharge = false,
                IsBus = false
            }
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("鉄道（南阿蘇水の生まれる里白水高原～阿蘇下田城ふれあい温泉）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC022_UseDateがnull_スキップ()
    {
        // Arrange
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = null,
                EntryStation = "天神",
                ExitStation = "博多",
                Amount = 210,
                Balance = 4790,
                IsCharge = false,
                IsBus = false
            },
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "天神", 210, 4580)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: UseDateがnullのレコードはスキップ
        results.Should().HaveCount(1);
        results[0].Date.Should().Be(new DateTime(2024, 12, 9));
        OutputInputAndResult(details, results);
    }

    #endregion

    #region カテゴリ7: 静的メソッド

    [Fact]
    public void TC023_GetLendingSummary()
    {
        var result = SummaryGenerator.GetLendingSummary();
        result.Should().Be("（貸出中）");
        _output.WriteLine($"GetLendingSummary() = \"{result}\"");
    }

    [Fact]
    public void TC024_GetChargeSummary()
    {
        var result = SummaryGenerator.GetChargeSummary();
        result.Should().Be("役務費によりチャージ");
        _output.WriteLine($"GetChargeSummary() = \"{result}\"");
    }

    [Fact]
    public void TC025_GetCarryoverFromPreviousYearSummary()
    {
        var result = SummaryGenerator.GetCarryoverFromPreviousYearSummary();
        result.Should().Be("前年度より繰越");
        _output.WriteLine($"GetCarryoverFromPreviousYearSummary() = \"{result}\"");
    }

    [Fact]
    public void TC026_GetCarryoverToNextYearSummary()
    {
        var result = SummaryGenerator.GetCarryoverToNextYearSummary();
        result.Should().Be("次年度へ繰越");
        _output.WriteLine($"GetCarryoverToNextYearSummary() = \"{result}\"");
    }

    [Theory]
    [InlineData(1, "1月計")]
    [InlineData(2, "2月計")]
    [InlineData(3, "3月計")]
    [InlineData(4, "4月計")]
    [InlineData(5, "5月計")]
    [InlineData(6, "6月計")]
    [InlineData(7, "7月計")]
    [InlineData(8, "8月計")]
    [InlineData(9, "9月計")]
    [InlineData(10, "10月計")]
    [InlineData(11, "11月計")]
    [InlineData(12, "12月計")]
    public void TC027_GetMonthlySummary(int month, string expected)
    {
        var result = SummaryGenerator.GetMonthlySummary(month);
        result.Should().Be(expected);
    }

    [Fact]
    public void TC028_GetCumulativeSummary()
    {
        var result = SummaryGenerator.GetCumulativeSummary();
        result.Should().Be("累計");
        _output.WriteLine($"GetCumulativeSummary() = \"{result}\"");
    }

    #endregion

    #region カテゴリ8: 従来メソッド（Generate）互換性テスト

    [Fact]
    public void TC029_Generate_履歴なし()
    {
        var details = new List<LedgerDetail>();
        var result = _generator.Generate(details);
        result.Should().BeEmpty();
        _output.WriteLine($"Generate() = \"{result}\"");
    }

    [Fact]
    public void TC030_Generate_チャージのみ()
    {
        var details = new List<LedgerDetail>
        {
            CreateCharge(new DateTime(2024, 12, 5), 3000, 8000)
        };
        var result = _generator.Generate(details);
        result.Should().Be("役務費によりチャージ");
        _output.WriteLine($"Generate() = \"{result}\"");
    }

    [Fact]
    public void TC031_Generate_鉄道片道()
    {
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4790)
        };
        var result = _generator.Generate(details);
        result.Should().Be("鉄道（天神～博多）");
        _output.WriteLine($"Generate() = \"{result}\"");
    }

    [Fact]
    public void TC032_Generate_鉄道往復()
    {
        // Generate()は入力順序に依存せず、残高ベースで古い順にソートする。
        // 天神→博多(残4790)が行き（残額高い＝先に利用）、博多→天神(残4580)が帰り（残額低い＝後に利用）。
        // → 行きの「天神～博多」を基準に往復表示
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "天神", 210, 4580),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4790)
        };
        var result = _generator.Generate(details);
        result.Should().Be("鉄道（天神～博多 往復）");
        _output.WriteLine($"Generate() = \"{result}\"");
    }

    [Fact]
    public void TC033_Generate_鉄道とバス混在()
    {
        var details = new List<LedgerDetail>
        {
            CreateBusUsage(new DateTime(2024, 12, 9), 230, 4560, busStops: "天神～博多駅"),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4790)
        };
        var result = _generator.Generate(details);
        result.Should().Be("鉄道（天神～博多）、バス（天神～博多駅）");
        _output.WriteLine($"Generate() = \"{result}\"");
    }

    [Fact]
    public void TC033B_Generate_残高ベース入力順序バリデーション_逆順入力でも正しく往復表示()
    {
        // Arrange: ICカード履歴の新しい順（博多→天神が先）でGenerate()に渡す。
        // 残高ベースのソートにより、残額が高い方（天神→博多=行き）が先と判断され、
        // 「天神～博多 往復」と正しく表示される。
        // ※ この挙動はGenerateRailwaySummary内の残高ソートが入力順序を自動補正する
        //   ことで実現される（呼び出し元が正しい順序で渡す必要がない）。
        var details = new List<LedgerDetail>
        {
            // ICカード履歴順（新しい順）: 帰り→行き
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "天神", 210, 4580),  // 帰り（残額低い=後に利用）
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4790)   // 行き（残額高い=先に利用）
        };

        // Act
        var result = _generator.Generate(details);

        // Assert: 残額ソートにより、行き（天神→博多）が基準になる
        result.Should().Be("鉄道（天神～博多 往復）");
        _output.WriteLine($"Generate() = \"{result}\"");
    }

    [Fact]
    public void TC033C_Generate_残高ベース入力順序バリデーション_乗継の逆順入力()
    {
        // Arrange: 乗継データが逆順（新しい順）で渡された場合でも正しく統合される
        var details = new List<LedgerDetail>
        {
            // ICカード履歴順（新しい順）: 乗継後→最初の乗車
            CreateRailwayUsage(new DateTime(2024, 12, 9), "中洲川端", "貝塚", 260, 4530),  // 2番目の乗車（残額低い）
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "中洲川端", 210, 4790)    // 1番目の乗車（残額高い）
        };

        // Act
        var result = _generator.Generate(details);

        // Assert: 残額ソートにより天神→中洲川端が先、乗継統合で天神～貝塚
        result.Should().Be("鉄道（天神～貝塚）");
        _output.WriteLine($"Generate() = \"{result}\"");
    }

    [Fact]
    public void TC033D_Generate_残高ベース入力順序バリデーション_3区間シャッフル入力()
    {
        // Arrange: 3区間の利用がシャッフルされた順序で渡された場合
        // 時系列順：天神→中洲川端(4790) → 中洲川端→箱崎宮前(4530) → 箱崎宮前→貝塚(4270)
        // 入力順（シャッフル）：2番目、3番目、1番目
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "中洲川端", "箱崎宮前", 260, 4530),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "箱崎宮前", "貝塚", 260, 4270),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "中洲川端", 210, 4790)
        };

        // Act: 残額ソートにより正しい時系列に並べ替え → 乗継統合
        var result = _generator.Generate(details);

        // Assert: 残額ソートで天神(4790)→中洲川端(4530)→箱崎宮前(4270)→貝塚と統合
        result.Should().Be("鉄道（天神～貝塚）");
        _output.WriteLine($"Generate() = \"{result}\"");
    }

    #endregion

    #region カテゴリ9: 福岡3社混在テスト

    [Fact]
    public void TC036_七隈線単独()
    {
        // Arrange: 七隈線のみ
        // ICカード履歴は新しい順：[0]帰り(新しい)、[1]行き(古い)
        // 往復は古い方（行き）を基準に表示
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神南", "六本松", 260, 4740),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "六本松", "天神南", 260, 5000)
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 古い方（行き=六本松→天神南）を基準に
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("鉄道（六本松～天神南 往復）");
        OutputInputAndResult(details, results);
    }

    /// <summary>
    /// Issue #878: 地下鉄天神駅と西鉄福岡(天神)駅が乗り継ぎ駅として同一視されることを確認
    /// </summary>
    [Fact]
    public void TC038_天神駅と西鉄福岡天神駅の乗り継ぎ()
    {
        // Arrange: 地下鉄空港線→西鉄天神大牟田線の乗り継ぎ（往復）
        // 行き: 博多→天神（地下鉄）→西鉄福岡(天神)→西鉄二日市（西鉄）
        // 帰り: 西鉄二日市→西鉄福岡(天神)（西鉄）→天神→博多（地下鉄）
        // ICカード履歴は新しい順
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 4410),      // 帰り地下鉄
            CreateRailwayUsage(new DateTime(2024, 12, 9), "西鉄二日市", "西鉄福岡(天神)", 380, 4620), // 帰り西鉄
            CreateRailwayUsage(new DateTime(2024, 12, 9), "西鉄福岡(天神)", "西鉄二日市", 380, 5000), // 行き西鉄
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "天神", 210, 5380)       // 行き地下鉄
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 天神と西鉄福岡(天神)が同一駅とみなされ、乗り継ぎとして連結される
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("鉄道（博多～西鉄二日市 往復）");
        OutputInputAndResult(details, results);
    }

    /// <summary>
    /// Issue #1017: 千早(JR)と西鉄千早が乗り継ぎ駅として同一視されることを確認
    /// </summary>
    [Fact]
    public void TC049_千早と西鉄千早の乗り継ぎ()
    {
        // Arrange: JR鹿児島本線→西鉄貝塚線の乗り継ぎ
        // 博多→千早（JR）→ 西鉄千早→西鉄福岡(天神)（西鉄）
        // 千早と西鉄千早は同一駅とみなされる
        // ICカード履歴は新しい順
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "西鉄千早", "西鉄福岡(天神)", 300, 4530),  // 西鉄（新しい）
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "千早", 170, 4830)                  // JR（古い）
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 千早と西鉄千早が同一駅とみなされ、乗り継ぎとして連結される
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("鉄道（博多～西鉄福岡(天神)）");
        OutputInputAndResult(details, results);
    }

    #endregion

    #region カテゴリ8: GenerateByDate - チャージ境界分割

    /// <summary>
    /// チャージが往復の間に挟まる場合、利用が分割されて往復にならないことを確認
    /// </summary>
    [Fact]
    public void TC039_チャージが往復の間に挟まる場合_利用が分割される()
    {
        // Arrange: 薬院→博多, チャージ, 博多→薬院（同一日）
        // ICカード履歴は新しい順
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "薬院", 310, 1380),   // 帰り（新しい）
            CreateCharge(new DateTime(2024, 12, 9), 1000, 1690),                        // チャージ
            CreateRailwayUsage(new DateTime(2024, 12, 9), "薬院", "博多", 310, 690),    // 行き（古い）
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 3件（利用1, チャージ, 利用2）- 往復にならない
        results.Should().HaveCount(3);
        OutputInputAndResult(details, results);

        // 古い順: 利用（薬院→博多）→ チャージ → 利用（博多→薬院）
        results[0].IsCharge.Should().BeFalse();
        results[0].Summary.Should().Be("鉄道（薬院～博多）");

        results[1].IsCharge.Should().BeTrue();

        results[2].IsCharge.Should().BeFalse();
        results[2].Summary.Should().Be("鉄道（博多～薬院）");
    }

    /// <summary>
    /// チャージが利用の前にある場合（挟まっていない）、利用は従来通り統合されることを確認
    /// </summary>
    [Fact]
    public void TC040_チャージが利用の前にある場合_利用は統合される()
    {
        // Arrange: チャージ→天神→博多→博多→天神（同一日）
        // TC012の12/4と同じパターン
        // ICカード履歴は新しい順
        var details = new List<LedgerDetail>
        {
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "天神", 210, 1580),  // 帰り（新しい）
            CreateRailwayUsage(new DateTime(2024, 12, 9), "天神", "博多", 210, 1790),  // 行き
            CreateCharge(new DateTime(2024, 12, 9), 1000, 2000),                       // チャージ（古い）
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 2件（チャージ, 往復利用）
        results.Should().HaveCount(2);
        OutputInputAndResult(details, results);

        results[0].IsCharge.Should().BeTrue();

        results[1].IsCharge.Should().BeFalse();
        results[1].Summary.Should().Be("鉄道（天神～博多 往復）");
    }

    /// <summary>
    /// 複数日にまたがるケースでチャージ境界分割が正しく動作することを確認
    /// </summary>
    [Fact]
    public void TC041_複数日_チャージ挟み込みのある日とない日の混在()
    {
        // Arrange: 12/8はチャージなし往復、12/9はチャージ挟み
        var details = new List<LedgerDetail>
        {
            // 12/9: 博多→薬院(新), チャージ, 薬院→博多(古)
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "薬院", 310, 1380),
            CreateCharge(new DateTime(2024, 12, 9), 1000, 1690),
            CreateRailwayUsage(new DateTime(2024, 12, 9), "薬院", "博多", 310, 690),
            // 12/8: 博多→天神(新), 天神→博多(古) - チャージなし
            CreateRailwayUsage(new DateTime(2024, 12, 8), "博多", "天神", 210, 1000),
            CreateRailwayUsage(new DateTime(2024, 12, 8), "天神", "博多", 210, 1210),
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 12/8は1件（往復）、12/9は3件（分割）
        results.Should().HaveCount(4);
        OutputInputAndResult(details, results);

        // 12/8: 往復
        results[0].Date.Should().Be(new DateTime(2024, 12, 8));
        results[0].Summary.Should().Be("鉄道（天神～博多 往復）");

        // 12/9: 分割
        results[1].Date.Should().Be(new DateTime(2024, 12, 9));
        results[1].Summary.Should().Be("鉄道（薬院～博多）");

        results[2].Date.Should().Be(new DateTime(2024, 12, 9));
        results[2].IsCharge.Should().BeTrue();

        results[3].Date.Should().Be(new DateTime(2024, 12, 9));
        results[3].Summary.Should().Be("鉄道（博多～薬院）");
    }

    #endregion

    #region Issue #942: 暗黙のポイント還元の判定

    /// <summary>
    /// Issue #942再現: 往復利用の後にポイント還元（金額が負、IsPointRedemption=false）があると
    /// 全てまとめられてしまう問題
    /// </summary>
    [Fact]
    public void TC042_GenerateByDate_暗黙のポイント還元が鉄道利用と分離される()
    {
        // Arrange: Issue #942の再現データ（新しい順）
        var details = new List<LedgerDetail>
        {
            // 最新: ポイント還元（金額が負、乗車駅あり・降車駅なし、IsPointRedemption=false）
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "薬院",
                ExitStation = null,
                Amount = -240,
                Balance = 1696,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false
            },
            // 博多→薬院（復路）
            CreateRailwayUsage(new DateTime(2026, 3, 10), "博多", "薬院", 210, 1456),
            // 薬院→博多（往路）
            CreateRailwayUsage(new DateTime(2026, 3, 10), "薬院", "博多", 210, 1666),
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: 鉄道往復とポイント還元が別行になること
        OutputInputAndResult(details, results);
        results.Should().HaveCount(2);

        results[0].Summary.Should().Be("鉄道（薬院～博多 往復）");
        results[0].IsPointRedemption.Should().BeFalse();

        results[1].Summary.Should().Be("ポイント還元");
        results[1].IsPointRedemption.Should().BeTrue();
    }

    /// <summary>
    /// Issue #942: Generate（レガシーAPI）でも暗黙のポイント還元が分離されること
    /// </summary>
    [Fact]
    public void TC043_Generate_暗黙のポイント還元が鉄道利用と分離される()
    {
        // Arrange: 往復 + 暗黙ポイント還元（新しい順）
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "薬院",
                ExitStation = null,
                Amount = -240,
                Balance = 1696,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false
            },
            CreateRailwayUsage(new DateTime(2026, 3, 10), "博多", "薬院", 210, 1456),
            CreateRailwayUsage(new DateTime(2026, 3, 10), "薬院", "博多", 210, 1666),
        };

        // Act
        var result = _generator.Generate(details);

        // Assert: 鉄道の往復のみが摘要に含まれ、ポイント還元は含まれない
        // （Generate()は利用のみを生成するため、暗黙ポイント還元は除外される）
        result.Should().Be("鉄道（薬院～博多 往復）");
    }

    /// <summary>
    /// Issue #942: 暗黙のポイント還元のみの場合
    /// </summary>
    [Fact]
    public void TC044_Generate_暗黙のポイント還元のみの場合はポイント還元を返す()
    {
        var details = new List<LedgerDetail>
        {
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "薬院",
                ExitStation = null,
                Amount = -240,
                Balance = 1696,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false
            }
        };

        var result = _generator.Generate(details);
        result.Should().Be("ポイント還元");
    }

    /// <summary>
    /// Issue #942: IsImplicitPointRedemption の判定ロジック
    /// </summary>
    [Fact]
    public void TC045_IsImplicitPointRedemption_各パターンの判定()
    {
        // 暗黙のポイント還元: 金額が負、チャージでもポイント還元フラグでもない
        SummaryGenerator.IsImplicitPointRedemption(new LedgerDetail
        {
            Amount = -240, IsCharge = false, IsPointRedemption = false
        }).Should().BeTrue();

        // 正の金額 → false
        SummaryGenerator.IsImplicitPointRedemption(new LedgerDetail
        {
            Amount = 210, IsCharge = false, IsPointRedemption = false
        }).Should().BeFalse();

        // チャージ → false（チャージは別処理）
        SummaryGenerator.IsImplicitPointRedemption(new LedgerDetail
        {
            Amount = -1000, IsCharge = true, IsPointRedemption = false
        }).Should().BeFalse();

        // 既にポイント還元フラグあり → false（明示的なので暗黙ではない）
        SummaryGenerator.IsImplicitPointRedemption(new LedgerDetail
        {
            Amount = -240, IsCharge = false, IsPointRedemption = true
        }).Should().BeFalse();

        // 金額がnull → false
        SummaryGenerator.IsImplicitPointRedemption(new LedgerDetail
        {
            Amount = null, IsCharge = false, IsPointRedemption = false
        }).Should().BeFalse();

        // 金額が0 → false
        SummaryGenerator.IsImplicitPointRedemption(new LedgerDetail
        {
            Amount = 0, IsCharge = false, IsPointRedemption = false
        }).Should().BeFalse();
    }

    /// <summary>
    /// Issue #942: 明示的ポイント還元と暗黙ポイント還元が混在する場合
    /// </summary>
    [Fact]
    public void TC046_GenerateByDate_明示的と暗黙のポイント還元が混在()
    {
        var details = new List<LedgerDetail>
        {
            // 暗黙のポイント還元
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                EntryStation = "薬院",
                ExitStation = null,
                Amount = -240,
                Balance = 1696,
                IsCharge = false,
                IsPointRedemption = false,
                IsBus = false
            },
            // 明示的ポイント還元
            new LedgerDetail
            {
                UseDate = new DateTime(2026, 3, 10),
                Amount = -100,
                Balance = 1456,
                IsCharge = false,
                IsPointRedemption = true,
                IsBus = false
            },
            // 鉄道利用
            CreateRailwayUsage(new DateTime(2026, 3, 10), "薬院", "博多", 210, 1356),
        };

        var results = _generator.GenerateByDate(details);

        OutputInputAndResult(details, results);
        results.Should().HaveCount(2);

        results[0].Summary.Should().Be("鉄道（薬院～博多）");
        results[0].IsPointRedemption.Should().BeFalse();

        results[1].Summary.Should().Be("ポイント還元");
        results[1].IsPointRedemption.Should().BeTrue();
    }

    #endregion

    #region Issue #1012: バス利用の摘要順序

    [Fact]
    public void TC047_バス複数_順序が時系列通り_Issue1012()
    {
        // Arrange
        // ICカード履歴は新しい順：[0]が最新、[3]が最古
        // 実際の時系列：薬院→博多(646)→博多→薬院(436)→薬院大通→西鉄平尾駅(286)→那の川→渡辺通一丁目(76)
        var details = new List<LedgerDetail>
        {
            // [0] 鉄道：薬院→博多（最新・残額646円）
            CreateRailwayUsage(new DateTime(2024, 12, 9), "薬院", "博多", 210, 646),
            // [1] 鉄道：博多→薬院（残額436円）
            CreateRailwayUsage(new DateTime(2024, 12, 9), "博多", "薬院", 210, 436),
            // [2] バス：薬院大通→西鉄平尾駅（残額286円）
            CreateBusUsage(new DateTime(2024, 12, 9), 150, 286, busStops: "薬院大通～西鉄平尾駅"),
            // [3] バス：那の川→渡辺通一丁目（最古・残額76円）
            CreateBusUsage(new DateTime(2024, 12, 9), 210, 76, busStops: "那の川～渡辺通一丁目"),
        };

        // Act
        var results = _generator.GenerateByDate(details);

        // Assert: バスも時系列順（薬院大通→西鉄平尾駅 が先、那の川→渡辺通一丁目 が後）
        results.Should().HaveCount(1);
        results[0].Summary.Should().Be("鉄道（薬院～博多 往復）、バス（薬院大通～西鉄平尾駅、那の川～渡辺通一丁目）");
        OutputInputAndResult(details, results);
    }

    [Fact]
    public void TC048_バス複数_Generate経由でも順序が正しい_Issue1012()
    {
        // Arrange
        // Generate()メソッドでもバスの順序が正しいことを確認
        var details = new List<LedgerDetail>
        {
            // ICカード履歴は新しい順
            CreateBusUsage(new DateTime(2024, 12, 9), 190, 200, busStops: "博多駅～天神"),
            CreateBusUsage(new DateTime(2024, 12, 9), 230, 390, busStops: "薬院大通～博多駅"),
            CreateBusUsage(new DateTime(2024, 12, 9), 150, 620, busStops: "西鉄平尾駅～薬院大通"),
        };

        // Act
        var result = _generator.Generate(details);

        // Assert: 時系列順（古い→新しい）で乗り継ぎ統合
        // 西鉄平尾駅→薬院大通→博多駅→天神 の乗り継ぎ
        result.Should().Be("バス（西鉄平尾駅～天神）");
    }

    #endregion
}
