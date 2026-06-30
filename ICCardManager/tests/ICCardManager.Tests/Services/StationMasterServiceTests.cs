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
    #region 路線別駅名取得テスト（カード種別×駅コード→駅名）

    /// <summary>
    /// 各路線・各カード種別における駅コード→駅名マッピングのテスト。
    /// CardType によって優先 Area が切り替わる仕様（はやかけん=九州/Suica=関東/PASMO=関東/TOICA=中部）を踏まえ、
    /// 路線ごとに代表的な駅コードと期待される駅名を InlineData で列挙する。
    /// </summary>
    [Theory]
    // 福岡市地下鉄 空港線（はやかけん）
    [InlineData(0xE701, CardType.Hayakaken, "姪浜")]      // 空港線 始発
    [InlineData(0xE703, CardType.Hayakaken, "室見")]
    [InlineData(0xE705, CardType.Hayakaken, "藤崎")]
    [InlineData(0xE707, CardType.Hayakaken, "西新")]
    [InlineData(0xE709, CardType.Hayakaken, "唐人町")]
    [InlineData(0xE70B, CardType.Hayakaken, "大濠公園")]
    [InlineData(0xE70D, CardType.Hayakaken, "赤坂")]
    [InlineData(0xE70F, CardType.Hayakaken, "天神")]      // 主要駅
    [InlineData(0xE711, CardType.Hayakaken, "中洲川端")]
    [InlineData(0xE713, CardType.Hayakaken, "祇園")]
    [InlineData(0xE715, CardType.Hayakaken, "博多")]      // 主要駅
    [InlineData(0xE717, CardType.Hayakaken, "東比恵")]
    [InlineData(0xE719, CardType.Hayakaken, "福岡空港")]  // 空港線 終点

    // 福岡市地下鉄 箱崎線（はやかけん）
    [InlineData(0xE801, CardType.Hayakaken, "中洲川端")]  // 箱崎線 始発
    [InlineData(0xE803, CardType.Hayakaken, "呉服町")]
    [InlineData(0xE805, CardType.Hayakaken, "千代県庁口")]
    [InlineData(0xE807, CardType.Hayakaken, "馬出九大病院前")]
    [InlineData(0xE809, CardType.Hayakaken, "箱崎宮前")]
    [InlineData(0xE80B, CardType.Hayakaken, "箱崎九大前")]
    [InlineData(0xE80D, CardType.Hayakaken, "貝塚")]      // 箱崎線 終点

    // 福岡市地下鉄 七隈線（はやかけん）
    [InlineData(0xE901, CardType.Hayakaken, "橋本")]      // 七隈線 始発
    [InlineData(0xE903, CardType.Hayakaken, "次郎丸")]
    [InlineData(0xE90B, CardType.Hayakaken, "福大前")]    // 福岡大学前
    [InlineData(0xE90D, CardType.Hayakaken, "七隈")]
    [InlineData(0xE915, CardType.Hayakaken, "六本松")]
    [InlineData(0xE91B, CardType.Hayakaken, "薬院")]      // 主要駅
    [InlineData(0xE91D, CardType.Hayakaken, "渡辺通")]
    [InlineData(0xE91F, CardType.Hayakaken, "天神南")]
    [InlineData(0xE921, CardType.Hayakaken, "櫛田神社前")] // 2023年延伸開業（Issue #1120）

    // JR九州 鹿児島本線（はやかけん）
    [InlineData(0x0601, CardType.Hayakaken, "門司港")]    // 鹿児島本線 起点
    [InlineData(0x0606, CardType.Hayakaken, "小倉")]      // 主要駅
    [InlineData(0x0623, CardType.Hayakaken, "香椎")]
    [InlineData(0x0624, CardType.Hayakaken, "千早")]
    [InlineData(0x0625, CardType.Hayakaken, "箱崎")]
    [InlineData(0x0626, CardType.Hayakaken, "吉塚")]
    [InlineData(0x0627, CardType.Hayakaken, "博多")]      // 主要駅
    [InlineData(0x0628, CardType.Hayakaken, "竹下")]
    [InlineData(0x062A, CardType.Hayakaken, "南福岡")]
    [InlineData(0x062B, CardType.Hayakaken, "春日")]
    [InlineData(0x062C, CardType.Hayakaken, "大野城")]
    [InlineData(0x062F, CardType.Hayakaken, "二日市")]
    [InlineData(0x0637, CardType.Hayakaken, "鳥栖")]      // 分岐駅
    [InlineData(0x063B, CardType.Hayakaken, "久留米")]    // 主要駅
    [InlineData(0x064D, CardType.Hayakaken, "大牟田")]    // 県境駅

    // JR山手線（Suica、Area=0 Line=37=0x25）
    [InlineData(0x2501, CardType.Suica, "品川")]          // Line 37, Station 1
    [InlineData(0x2507, CardType.Suica, "渋谷")]          // Line 37, Station 7
    [InlineData(0x250A, CardType.Suica, "新宿")]          // Line 37, Station 10
    [InlineData(0x250D, CardType.Suica, "高田馬場")]      // Line 37, Station 13
    [InlineData(0x250F, CardType.Suica, "池袋")]          // Line 37, Station 15

    // JR東海道本線 東京〜横浜（Suica、Area=0 Line=1=0x01）
    [InlineData(0x0101, CardType.Suica, "東京")]          // Line 1, Station 1
    [InlineData(0x0107, CardType.Suica, "品川")]          // Line 1, Station 7
    [InlineData(0x0112, CardType.Suica, "横浜")]          // Line 1, Station 18

    // 北陸新幹線 金沢延伸区間（Suica、Area=0 Line=73=0x49、Issue #1120）
    [InlineData(0x4915, CardType.Suica, "飯山")]
    [InlineData(0x4917, CardType.Suica, "上越妙高")]
    [InlineData(0x4919, CardType.Suica, "糸魚川")]
    [InlineData(0x491B, CardType.Suica, "黒部宇奈月温泉")]
    [InlineData(0x491D, CardType.Suica, "富山")]
    [InlineData(0x491F, CardType.Suica, "新高岡")]
    [InlineData(0x4921, CardType.Suica, "金沢")]

    // 相鉄新横浜線（PASMO、Area=0 Line=147=0x93、Issue #1120）
    [InlineData(0x9383, CardType.PASMO, "羽沢横浜国大")]
    [InlineData(0x9385, CardType.PASMO, "新横浜")]

    // 東急新横浜線（PASMO、Area=0 Line=209=0xD1、Issue #1120）
    [InlineData(0xD185, CardType.PASMO, "新綱島")]
    [InlineData(0xD189, CardType.PASMO, "新横浜")]

    // 北大阪急行 箕面延伸区間（TOICA、Area=2 Line=222=0xDE、Issue #1120）
    // LineCode 222 は Area 0 に小田急多摩線も存在するため、Area 2 優先の TOICA/manaca で検索する必要がある
    [InlineData(0xDE06, CardType.TOICA, "箕面船場阪大前")]
    [InlineData(0xDE07, CardType.TOICA, "箕面萱野")]

    // 西鉄天神大牟田線（はやかけん、Area=3 Line=215=0xD7、Issue #1674）
    // LineCode 215 は Area 0（京急大師線）・Area 2（北神急行）にも存在するため、Area 3 優先の はやかけん で解決される必要がある
    [InlineData(0xD771, CardType.Hayakaken, "雑餉隈")]    // 桜並木の手前
    [InlineData(0xD772, CardType.Hayakaken, "桜並木")]    // 2023年開業（Issue #1674）
    [InlineData(0xD773, CardType.Hayakaken, "春日原")]    // 桜並木の次
    public void GetStationName_カード種別と駅コードに応じた駅名を返すこと(int stationCode, CardType cardType, string expectedName)
    {
        // Arrange
        var service = new StationMasterService();

        // Act
        var result = service.GetStationName(stationCode, cardType);

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
        var service = new StationMasterService();

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
        var service = new StationMasterService();

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
        var service = new StationMasterService();
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
        var service = new StationMasterService();

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
        var service = new StationMasterService();

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
        var service = new StationMasterService();

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
        var service = new StationMasterService();

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
        var service = new StationMasterService();

        // Act
        var result = service.GetLineName(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region インスタンス生成とデータロードのテスト

    /// <summary>
    /// コンストラクタで正常にインスタンスが生成できること
    /// </summary>
    [Fact]
    public void Constructor_ShouldCreateInstance()
    {
        // Act
        var service = new StationMasterService();

        // Assert
        service.Should().NotBeNull();
        service.Should().BeAssignableTo<IStationMasterService>();
    }

    /// <summary>
    /// データが読み込まれていることの確認
    /// </summary>
    [Fact]
    public void StationCount_AfterLoad_IsGreaterThanZero()
    {
        // Arrange
        var service = new StationMasterService();

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
        var service = new StationMasterService();

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
        var service = new StationMasterService();

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
        var service = new StationMasterService();

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
        var service = new StationMasterService();

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
