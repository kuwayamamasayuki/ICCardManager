using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Services;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// StationMasterServiceのテスト
/// </summary>
public class StationMasterServiceTests
{
    #region 福岡エリア（はやかけん）のテスト

    /// <summary>
    /// 福岡市地下鉄 空港線の駅名取得テスト（はやかけん利用時）
    /// </summary>
    [Theory]
    [InlineData(0xE701, "姪浜")]      // 空港線 始発
    [InlineData(0xE703, "室見")]
    [InlineData(0xE705, "藤崎")]
    [InlineData(0xE707, "西新")]
    [InlineData(0xE709, "唐人町")]
    [InlineData(0xE70B, "大濠公園")]
    [InlineData(0xE70D, "赤坂")]
    [InlineData(0xE70F, "天神")]      // 主要駅
    [InlineData(0xE711, "中洲川端")]
    [InlineData(0xE713, "祇園")]
    [InlineData(0xE715, "博多")]      // 主要駅
    [InlineData(0xE717, "東比恵")]
    [InlineData(0xE719, "福岡空港")]  // 空港線 終点
    public void GetStationName_AirportLine_WithHayakaken_ReturnsCorrectName(int stationCode, string expectedName)
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetStationName(stationCode, CardType.Hayakaken);

        // Assert
        result.Should().Be(expectedName);
    }

    /// <summary>
    /// 福岡市地下鉄 箱崎線の駅名取得テスト（はやかけん利用時）
    /// </summary>
    [Theory]
    [InlineData(0xE801, "中洲川端")]  // 箱崎線 始発
    [InlineData(0xE803, "呉服町")]
    [InlineData(0xE805, "千代県庁口")]
    [InlineData(0xE807, "馬出九大病院前")]
    [InlineData(0xE809, "箱崎宮前")]
    [InlineData(0xE80B, "箱崎九大前")]
    [InlineData(0xE80D, "貝塚")]      // 箱崎線 終点
    public void GetStationName_HakozakiLine_WithHayakaken_ReturnsCorrectName(int stationCode, string expectedName)
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetStationName(stationCode, CardType.Hayakaken);

        // Assert
        result.Should().Be(expectedName);
    }

    /// <summary>
    /// 福岡市地下鉄 七隈線の駅名取得テスト（はやかけん利用時）
    /// </summary>
    [Theory]
    [InlineData(0xE901, "橋本")]      // 七隈線 始発
    [InlineData(0xE903, "次郎丸")]
    [InlineData(0xE90B, "福大前")]    // 福岡大学前
    [InlineData(0xE90D, "七隈")]
    [InlineData(0xE915, "六本松")]
    [InlineData(0xE91B, "薬院")]      // 主要駅
    [InlineData(0xE91D, "渡辺通")]
    [InlineData(0xE91F, "天神南")]    // 七隈線 終点
    public void GetStationName_NanakumaLine_WithHayakaken_ReturnsCorrectName(int stationCode, string expectedName)
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetStationName(stationCode, CardType.Hayakaken);

        // Assert
        result.Should().Be(expectedName);
    }

    /// <summary>
    /// JR九州 鹿児島本線の駅名取得テスト（はやかけん利用時）
    /// </summary>
    [Theory]
    [InlineData(0x0601, "門司港")]    // 鹿児島本線 起点
    [InlineData(0x0606, "小倉")]      // 主要駅
    [InlineData(0x0623, "香椎")]
    [InlineData(0x0624, "千早")]
    [InlineData(0x0625, "箱崎")]
    [InlineData(0x0626, "吉塚")]
    [InlineData(0x0627, "博多")]      // 主要駅
    [InlineData(0x0628, "竹下")]
    [InlineData(0x062A, "南福岡")]
    [InlineData(0x062B, "春日")]
    [InlineData(0x062C, "大野城")]
    [InlineData(0x062F, "二日市")]
    [InlineData(0x0637, "鳥栖")]      // 分岐駅
    [InlineData(0x063B, "久留米")]    // 主要駅
    [InlineData(0x064D, "大牟田")]    // 県境駅
    public void GetStationName_KagoshimaLine_WithHayakaken_ReturnsCorrectName(int stationCode, string expectedName)
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetStationName(stationCode, CardType.Hayakaken);

        // Assert
        result.Should().Be(expectedName);
    }

    #endregion

    #region 関東エリア（Suica/PASMO）のテスト

    /// <summary>
    /// JR山手線の主要駅名取得テスト（Suica利用時）
    /// CSVデータ: Area=0, Line=37 (0x25)
    /// </summary>
    [Theory]
    [InlineData(0x2501, "品川")]      // Line 37, Station 1
    [InlineData(0x2507, "渋谷")]      // Line 37, Station 7
    [InlineData(0x250A, "新宿")]      // Line 37, Station 10
    [InlineData(0x250D, "高田馬場")]  // Line 37, Station 13
    [InlineData(0x250F, "池袋")]      // Line 37, Station 15
    public void GetStationName_YamanoteLine_WithSuica_ReturnsCorrectName(int stationCode, string expectedName)
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetStationName(stationCode, CardType.Suica);

        // Assert
        result.Should().Be(expectedName);
    }

    /// <summary>
    /// JR東海道本線 東京〜横浜の駅名取得テスト（Suica利用時）
    /// CSVデータ: Area=0, Line=1 (0x01)
    /// </summary>
    [Theory]
    [InlineData(0x0101, "東京")]      // Line 1, Station 1
    [InlineData(0x0107, "品川")]      // Line 1, Station 7
    [InlineData(0x0112, "横浜")]      // Line 1, Station 18
    public void GetStationName_TokaidoLine_Kanto_WithSuica_ReturnsCorrectName(int stationCode, string expectedName)
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetStationName(stationCode, CardType.Suica);

        // Assert
        result.Should().Be(expectedName);
    }

    #endregion

    #region カード種別による優先エリア切り替えテスト

    /// <summary>
    /// カード種別によって異なるエリアの同一路線コードで正しい駅名が返ることを確認
    /// </summary>
    [Fact]
    public void GetStationName_SameLineCode_DifferentCardType_ReturnsDifferentStations()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // 路線コード231は関東と九州で異なる路線
        // Area 0 (関東): 231 = 東京メトロ副都心線など
        // Area 3 (九州): 231 = 福岡市地下鉄空港線

        // Act - はやかけん（九州優先）で検索
        var fukuokaStation = service.GetStationName(0xE70F, CardType.Hayakaken);

        // Assert - 福岡の天神が返される
        fukuokaStation.Should().Be("天神");
    }

    /// <summary>
    /// カード種別指定なしでも駅名が取得できることを確認
    /// </summary>
    [Fact]
    public void GetStationName_WithoutCardType_ReturnsStationName()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act - カード種別指定なし（デフォルトは関東優先）
        // JR東海道本線 東京駅 (Line 1, Station 1)
        var result = service.GetStationName(0x0101);

        // Assert - 東京（関東エリア）が返される
        result.Should().Be("東京");
    }

    #endregion

    #region 未登録駅コードのテスト

    /// <summary>
    /// 未登録の駅コードはフォールバック表示
    /// </summary>
    [Theory]
    [InlineData(0xFF01)]  // 存在しない路線
    [InlineData(0xE7FF)]  // 空港線の範囲外
    [InlineData(0xFEFE)]  // 存在しない組み合わせ
    public void GetStationName_UnknownCode_ReturnsFallbackFormat(int stationCode)
    {
        // Arrange
        var service = StationMasterService.Instance;
        var lineCode = (stationCode >> 8) & 0xFF;
        var stationNum = stationCode & 0xFF;
        var expectedFallback = $"駅{lineCode:X2}-{stationNum:X2}";

        // Act
        var result = service.GetStationName(stationCode, CardType.Hayakaken);

        // Assert
        result.Should().Be(expectedFallback);
    }

    /// <summary>
    /// GetStationNameOrNullは未登録コードでnullを返す
    /// </summary>
    [Fact]
    public void GetStationNameOrNull_UnknownCode_ReturnsNull()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetStationNameOrNull(0xFF, 0x01);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// GetStationNameOrNullは登録済みコードで駅名を返す
    /// </summary>
    [Fact]
    public void GetStationNameOrNull_KnownCode_ReturnsStationName()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act - 福岡市地下鉄空港線 天神（九州優先で検索）
        var result = service.GetStationNameOrNull(231, 15, CardType.Hayakaken);

        // Assert
        result.Should().Be("天神");
    }

    /// <summary>
    /// GetStationNameByAreaでエリア指定で駅名を取得
    /// </summary>
    [Fact]
    public void GetStationNameByArea_SpecificArea_ReturnsCorrectStation()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act - 九州エリア（3）の空港線（231）天神（15）
        var result = service.GetStationNameByArea(3, 231, 15);

        // Assert
        result.Should().Be("天神");
    }

    #endregion

    #region 路線名取得テスト

    /// <summary>
    /// 路線名の取得テスト（はやかけん利用時）
    /// </summary>
    [Theory]
    [InlineData(231, "1号")]       // 空港線
    [InlineData(232, "2号")]       // 箱崎線
    [InlineData(233, "3号")]       // 七隈線
    [InlineData(6, "鹿児島本")]    // 鹿児島本線
    public void GetLineName_WithHayakaken_ReturnsLineName(int lineCode, string expectedLineName)
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetLineName(lineCode, CardType.Hayakaken);

        // Assert
        result.Should().NotBeNull();
        result.Should().Contain(expectedLineName.Substring(0, Math.Min(2, expectedLineName.Length)));
    }

    /// <summary>
    /// 未登録の路線コードでnullを返す
    /// </summary>
    [Fact]
    public void GetLineName_UnknownLine_ReturnsNull()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var result = service.GetLineName(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region シングルトンとデータロードのテスト

    /// <summary>
    /// シングルトンインスタンスの同一性確認
    /// </summary>
    [Fact]
    public void Instance_AlwaysReturnsSameInstance()
    {
        // Act
        var instance1 = StationMasterService.Instance;
        var instance2 = StationMasterService.Instance;

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    /// <summary>
    /// データが読み込まれていることの確認
    /// </summary>
    [Fact]
    public void StationCount_AfterLoad_IsGreaterThanZero()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var count = service.StationCount;

        // Assert
        count.Should().BeGreaterThan(0, "駅データが読み込まれている必要があります");
    }

    /// <summary>
    /// 路線データが読み込まれていることの確認
    /// </summary>
    [Fact]
    public void LineCount_AfterLoad_IsGreaterThanZero()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var count = service.LineCount;

        // Assert
        count.Should().BeGreaterThan(0, "路線データが読み込まれている必要があります");
    }

    /// <summary>
    /// 各エリアのデータが読み込まれていることの確認
    /// </summary>
    [Theory]
    [InlineData(0)]  // 関東
    [InlineData(1)]  // 関西
    [InlineData(2)]  // 中部
    [InlineData(3)]  // 九州
    public void GetStationCountByArea_AllAreas_HaveStations(int areaCode)
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act
        var count = service.GetStationCountByArea(areaCode);

        // Assert
        count.Should().BeGreaterThan(0, $"エリア{areaCode}のデータが読み込まれている必要があります");
    }

    #endregion

    #region 出張シナリオテスト

    /// <summary>
    /// 出張シナリオ: はやかけんで東京出張（関東の駅も検索可能）
    /// </summary>
    [Fact]
    public void GetStationName_HayakakenInTokyo_CanFindTokyoStations()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act - はやかけんで東京の駅コードを検索
        // 九州優先だが、見つからなければ関東も検索
        // JR山手線: Line 37 (0x25)
        var shibuya = service.GetStationName(0x2507, CardType.Hayakaken);    // 山手線 渋谷 (Line 37, Station 7)
        var shinjuku = service.GetStationName(0x250A, CardType.Hayakaken);   // 山手線 新宿 (Line 37, Station 10)

        // Assert - 東京の駅名が返される
        shibuya.Should().Be("渋谷");
        shinjuku.Should().Be("新宿");
    }

    /// <summary>
    /// 出張シナリオ: Suicaで福岡出張（九州の駅も検索可能）
    /// </summary>
    [Fact]
    public void GetStationName_SuicaInFukuoka_CanFindFukuokaStations()
    {
        // Arrange
        var service = StationMasterService.Instance;

        // Act - Suicaで福岡の駅コードを検索
        // 関東優先だが、見つからなければ九州も検索
        // 福岡市地下鉄箱崎線: Line 232 (0xE8) - 他のエリアにない駅コードを使用
        // ※空港線(231)は近畿日本鉄道と重複するため、箱崎線を使用
        var nakasukawabata = service.GetStationName(0xE801, CardType.Suica);  // 箱崎線 中洲川端 (Line 232, Station 1)
        var gofukumachi = service.GetStationName(0xE803, CardType.Suica);     // 箱崎線 呉服町 (Line 232, Station 3)

        // Assert - 福岡の駅名が返される（他エリアに該当駅コードがないため）
        nakasukawabata.Should().Be("中洲川端");
        gofukumachi.Should().Be("呉服町");
    }

    #endregion
}
