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
    [InlineData(0x062F, CardType.Hayakaken, "都府楼南")]  // 旧 006-046 → 047（Issue #1680 再採番反映。二日市は 0x0630 へ移動）
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

    // JR各社 2015〜2025年の新駅・改称（全国監査、Issue #1674）
    // FeliCa の線区駅順コードは JR 全社が Area 0（全国共通サイバネ体系）に格納される。
    // リージョン適合カード（非 Area 0 優先）で正しく解決される＝私鉄エリアと衝突しないことを担保する。
    [InlineData(0x3F11, CardType.Suica, "幕張豊砂")]        // 京葉線 2023年開業
    [InlineData(0x5523, CardType.Suica, "上所")]            // 越後線 2025年開業
    [InlineData(0x015F, CardType.TOICA, "御厨")]            // 東海道本線(JR東海) 2020年開業
    [InlineData(0x01F9, CardType.ICOCA, "摩耶")]            // 東海道本線(JR神戸線) 2016年開業
    [InlineData(0x0A20, CardType.ICOCA, "東姫路")]          // 山陽本線 2016年開業
    [InlineData(0x0A5E, CardType.ICOCA, "寺家")]            // 山陽本線 2017年開業
    [InlineData(0x01DC, CardType.ICOCA, "JR総持寺")]        // 東海道本線(JR京都線) 2018年開業
    [InlineData(0x6A05, CardType.ICOCA, "衣摺加美北")]      // おおさか東線 2018年開業
    [InlineData(0x0B02, CardType.ICOCA, "梅小路京都西")]    // 山陰本線(嵯峨野線) 2019年開業
    [InlineData(0x6CC3, CardType.ICOCA, "JR野江")]          // おおさか東線 北区間 2019年開業
    [InlineData(0x6CC9, CardType.ICOCA, "南吹田")]          // おおさか東線 北区間 2019年開業
    [InlineData(0x0666, CardType.SUGOCA, "西熊本")]         // 鹿児島本線 2016年開業
    [InlineData(0x7814, CardType.Hayakaken, "糸島高校前")]  // 筑肥線 2019年開業
    [InlineData(0x5E4A, CardType.SUGOCA, "大村車両基地")]   // 大村線 2020年開業
    [InlineData(0x5E4C, CardType.SUGOCA, "新大村")]         // 大村線 2022年開業（西九州新幹線接続）
    [InlineData(0x24CF, CardType.Kitaca, "ロイズタウン")]   // 札沼線(学園都市線) 2022年開業
    [InlineData(0x24D0, CardType.Kitaca, "太美")]           // 札沼線 2022年改称（旧 石狩太美）
    [InlineData(0x24D2, CardType.Kitaca, "当別")]           // 札沼線 2022年改称（旧 石狩当別）

    // 再採番を伴う新駅4駅と再採番された既存駅（Issue #1676）
    // 新駅挿入による繰り下げ（仙石線のみ繰り上げ）は隣接する空きスロットで吸収され、
    // 路線末尾まで連鎖しない。ysrl 現行値（code.php?region=0&line=NNN）で路線全体を突合済み。
    [InlineData(0x0105, CardType.Suica, "田町")]            // 東海道本線 旧 001-006 → 005（空き005へ移動）
    [InlineData(0x0106, CardType.Suica, "高輪ゲートウェイ")] // 東海道本線 2020年開業
    [InlineData(0x0A68, CardType.ICOCA, "新白島")]          // 山陽本線 2015年開業（旧 横川のスロット）
    [InlineData(0x0A69, CardType.ICOCA, "横川")]            // 山陽本線 旧 010-104 → 105
    [InlineData(0x0A6A, CardType.ICOCA, "西広島")]          // 山陽本線 旧 010-105 → 106（空き106が吸収）
    [InlineData(0x2399, CardType.Suica, "陸前小野")]        // 仙石線 旧 035-154 → 153（空き153へ繰り上げ）
    [InlineData(0x239A, CardType.Suica, "鹿妻")]            // 仙石線 旧 035-155 → 154
    [InlineData(0x239B, CardType.Suica, "矢本")]            // 仙石線 旧 035-156 → 155
    [InlineData(0x239C, CardType.Suica, "東矢本")]          // 仙石線 旧 035-157 → 156
    [InlineData(0x239D, CardType.Suica, "陸前赤井")]        // 仙石線 旧 035-158 → 157
    [InlineData(0x239E, CardType.Suica, "石巻あゆみ野")]    // 仙石線 2016年開業
    [InlineData(0x239F, CardType.Suica, "蛇田")]            // 仙石線 159 再採番の影響なし（隣接駅ガード）
    [InlineData(0x3182, CardType.Suica, "前潟")]            // 田沢湖線 2023年開業
    [InlineData(0x3183, CardType.Suica, "大釜")]            // 田沢湖線 旧 049-130 → 131
    [InlineData(0x3184, CardType.Suica, "小岩井")]          // 田沢湖線 旧 049-131 → 132
    [InlineData(0x3185, CardType.Suica, "雫石")]            // 田沢湖線 旧 049-132 → 133（空き133が吸収）
    [InlineData(0x3186, CardType.Suica, "春木場")]          // 田沢湖線 134 再採番の影響なし（隣接駅ガード）

    // 同一線区の路線単位突合で検出した空きスロット新駅（Issue #1676）
    [InlineData(0x017A, CardType.TOICA, "相見")]            // 東海道本線(JR東海) 2012年開業（#1674監査窓外）
    [InlineData(0x0A22, CardType.ICOCA, "手柄山平和公園")]  // 山陽本線 2026年開業（#1674監査窓外）

    // region=0 全線区突合で検出した「同一コード・異名」乖離の解消 — A. 再採番系（Issue #1680）
    // ysrl 現行値（code.php?region=0&line=NNN）で各路線を丸ごと突合して反映。
    // なお ysrl 側で 006-077（大牟田/新大牟田）・006-087（玉名/新玉名）は在来線と新幹線が
    // 同一コードに重複掲載されているため、IC利用実態のある在来線名を維持している。
    // 東北本線（紫波中央 1998年開業）
    [InlineData(0x029D, CardType.Suica, "石鳥谷")]          // 旧 002-158 → 157
    [InlineData(0x029E, CardType.Suica, "日詰")]            // 旧 002-159 → 158
    [InlineData(0x029F, CardType.Suica, "紫波中央")]        // 1998年開業
    [InlineData(0x02A0, CardType.Suica, "古館")]            // 160 再採番の影響なし（隣接駅ガード）
    // 鹿児島本線（再採番・改称。都府楼南 0x062F は上の鹿児島本線ブロックで検証）
    [InlineData(0x0630, CardType.Hayakaken, "二日市")]      // 旧 006-047 → 048
    [InlineData(0x0631, CardType.Hayakaken, "天拝山")]      // 049 再採番の影響なし（隣接駅ガード）
    [InlineData(0x0643, CardType.Hayakaken, "筑後船小屋")]  // 2011年改称（旧 船小屋）
    [InlineData(0x0657, CardType.SUGOCA, "玉名")]           // ysrl 側の重複掲載のため在来線名を維持
    [InlineData(0x0668, CardType.SUGOCA, "富合")]           // 2011年開業
    [InlineData(0x0669, CardType.SUGOCA, "宇土")]           // 旧 006-104 → 105
    // 日豊本線
    [InlineData(0x071B, CardType.SUGOCA, "三毛門")]         // 旧 007-026 → 027
    [InlineData(0x071C, CardType.SUGOCA, "吉富")]           // 旧 007-027 → 028
    [InlineData(0x071D, CardType.SUGOCA, "中津")]           // 旧 007-028 → 029
    // 山陰本線（繰り上げ・繰り下げ混在の再採番＋梶栗郷台地 2008年開業）
    [InlineData(0x0B64, CardType.ICOCA, "安来")]            // 旧 011-101 → 100
    [InlineData(0x0B65, CardType.ICOCA, "荒島")]            // 旧 011-102 → 101
    [InlineData(0x0B66, CardType.ICOCA, "揖屋")]            // 旧 011-103 → 102
    [InlineData(0x0B68, CardType.ICOCA, "東松江")]          // 104 再採番の影響なし（隣接駅ガード）
    [InlineData(0x0B6D, CardType.ICOCA, "来待")]            // 旧 011-108 → 109
    [InlineData(0x0B6E, CardType.ICOCA, "宍道")]            // 旧 011-109 → 110
    [InlineData(0x0B6F, CardType.ICOCA, "荘原")]            // 旧 011-110 → 111
    [InlineData(0x0B70, CardType.ICOCA, "直江")]            // 旧 011-111 → 112
    [InlineData(0x0B72, CardType.ICOCA, "出雲市")]          // 旧 011-112 → 114
    [InlineData(0x0B73, CardType.ICOCA, "西出雲")]          // 旧 011-113 → 115
    [InlineData(0x0B74, CardType.ICOCA, "出雲神西")]        // 旧 011-114 → 116
    [InlineData(0x0B75, CardType.ICOCA, "江南")]            // 旧 011-115 → 117
    [InlineData(0x0B76, CardType.ICOCA, "小田")]            // 旧 011-116 → 118
    [InlineData(0x0B77, CardType.ICOCA, "田儀")]            // 旧 011-117 → 119
    [InlineData(0x0BC2, CardType.ICOCA, "安岡")]            // 旧 011-195 → 194
    [InlineData(0x0BC3, CardType.ICOCA, "梶栗郷台地")]      // 2008年開業
    // 予讃線（南伊予 2020年開業。JR四国は ICOCA エリア）
    [InlineData(0x1047, CardType.ICOCA, "南伊予")]          // 2020年開業
    [InlineData(0x1048, CardType.ICOCA, "伊予横田")]        // 旧 016-071 → 072
    [InlineData(0x1049, CardType.ICOCA, "鳥ノ木")]          // 旧 016-072 → 073
    [InlineData(0x104A, CardType.ICOCA, "伊予市")]          // 旧 016-073 → 074
    [InlineData(0x104B, CardType.ICOCA, "向井原")]          // 旧 016-074 → 075
    // 奥羽本線（泉外旭川 2021年開業）
    [InlineData(0x1255, CardType.Suica, "泉外旭川")]        // 2021年開業
    [InlineData(0x1256, CardType.Suica, "土崎")]            // 旧 018-085 → 086
    // 磐越西線（郡山富田 2016年開業）
    [InlineData(0x1F82, CardType.Suica, "郡山富田")]        // 2016年開業
    [InlineData(0x1F83, CardType.Suica, "喜久田")]          // 旧 031-130 → 131
    // 横須賀線・品鶴線（武蔵小杉 2010年開業。CSV は新川崎の2行重複データも修正）
    [InlineData(0x2A0B, CardType.Suica, "新川崎")]
    [InlineData(0x2A0C, CardType.Suica, "武蔵小杉")]        // 2010年開業
    [InlineData(0x2A0D, CardType.Suica, "西大井")]          // 旧 042-014 → 013
    // 烏山線
    [InlineData(0x3005, CardType.Suica, "大金")]            // 旧 048-006 → 005
    [InlineData(0x3006, CardType.Suica, "小塙")]            // 旧 048-007 → 006
    // 小海線（CSV の中込2行重複データを修正）
    [InlineData(0x5319, CardType.Suica, "中込")]            // 025 影響なし（隣接駅ガード）
    [InlineData(0x531A, CardType.Suica, "滑津")]
    // 大村線（新大村 2022年開業に伴う再採番の残り。新駅自体は #1674 で追加済み）
    [InlineData(0x5E4D, CardType.SUGOCA, "諏訪")]           // 旧 094-076 → 077
    [InlineData(0x5E4E, CardType.SUGOCA, "大村")]           // 旧 094-077 → 078

    // region=0 全線区突合で検出した「同一コード・異名」乖離の解消 — B. 駅名改称・表記修正（Issue #1680、コード不変）
    [InlineData(0x0207, CardType.Suica, "鶯谷")]            // 東北本線 表記修正（旧 鴬谷）
    [InlineData(0x051B, CardType.Suica, "龍ケ崎市")]        // 常磐線 2020年改称（旧 佐貫）
    [InlineData(0x0F17, CardType.Kitaca, "名寄高校")]       // 宗谷本線 2022年移転改称（旧 東風連）
    [InlineData(0x21D0, CardType.Kitaca, "渡島当別")]       // 道南いさりび鉄道（渡島鶴岡は2014年廃止）
    [InlineData(0x264F, CardType.ICOCA, "仁方")]            // 呉線 誤記修正（仁万は山陰本線の駅）
    [InlineData(0x2F04, CardType.Suica, "文挾")]            // 日光線 表記修正
    [InlineData(0x3DC8, CardType.Kitaca, "奥津軽いまべつ")] // 海峡線 2016年改称（旧 津軽今別）
    [InlineData(0x4022, CardType.TOICA, "常葉大学前")]      // 天竜浜名湖線 2022年改称（旧 浜松大学前）
    [InlineData(0x4025, CardType.TOICA, "岡地")]            // 天竜浜名湖線 2022年改称（旧 気賀高校前）
    [InlineData(0x501B, CardType.ICOCA, "越前下山")]        // 越美北線 データ不備修正（駅名への「九頭竜線」混入）
    [InlineData(0x501C, CardType.ICOCA, "九頭竜湖")]        // 越美北線 データ不備修正（同上）
    [InlineData(0x50CE, CardType.Kitaca, "摩周")]           // 釧網本線 注記残骸の除去（旧称 弟子屈は Note 列へ）
    [InlineData(0x5853, CardType.SUGOCA, "江北")]           // 長崎本線 2022年改称（旧 肥前山口）
    [InlineData(0x5A02, CardType.ICOCA, "コウノトリの郷")]  // 宮津線 2015年改称（旧 但馬三江）
    [InlineData(0x6917, CardType.ICOCA, "寝屋川公園")]      // 片町線 2019年改称（旧 東寝屋川）
    [InlineData(0x8129, CardType.PASMO, "御花畑")]          // 秩父本線 表記修正
    [InlineData(0x824A, CardType.PASMO, "東京国際クルーズターミナル")] // ゆりかもめ 2019年改称（旧 船の科学館）
    [InlineData(0x824E, CardType.PASMO, "東京ビッグサイト")]           // ゆりかもめ 2019年改称（旧 国際展示場正門）
    [InlineData(0x9D12, CardType.PASMO, "獨協大学前")]      // 伊勢崎線 2017年改称（旧 松原団地）
    [InlineData(0xA30D, CardType.PASMO, "江曽島")]          // 宇都宮線(東武) 表記修正
    [InlineData(0xB003, CardType.PASMO, "四ツ木")]          // 押上線 表記修正
    [InlineData(0xB104, CardType.PASMO, "京成金町")]        // 金町線（旧 金町）
    [InlineData(0xC00E, CardType.PASMO, "多摩湖")]          // 多摩湖線 2021年改称（旧 西武遊園地）
    [InlineData(0xC00F, CardType.PASMO, "西武園ゆうえんち")] // 山口線 2021年改称（旧 遊園地西）
    [InlineData(0xD03B, CardType.PASMO, "南町田グランベリーパーク")] // 田園都市線 2019年改称
    [InlineData(0xD514, CardType.PASMO, "花月総持寺")]      // 京急本線 2020年改称（旧 花月園前）
    [InlineData(0xD519, CardType.PASMO, "京急東神奈川")]    // 京急本線 2020年改称（旧 仲木戸）
    [InlineData(0xD606, CardType.PASMO, "羽田空港第3ターミナル")]       // 京急空港線 2020年改称
    [InlineData(0xD607, CardType.PASMO, "羽田空港第1・第2ターミナル")] // 京急空港線 2020年改称
    [InlineData(0xD706, CardType.PASMO, "大師橋")]          // 京急大師線 2020年改称。LineCode 215 は Area 3（西鉄）にも存在するため関東優先の PASMO で解決
    [InlineData(0xD806, CardType.PASMO, "逗子・葉山")]      // 京急逗子線 2020年改称（旧 新逗子）
    [InlineData(0xE530, CardType.PASMO, "四ツ谷")]          // 丸ノ内線 表記修正
    [InlineData(0xE704, CardType.PASMO, "池袋")]            // 副都心線 表記修正（旧 池袋駅）。LineCode 231 は Area 3（福岡市地下鉄空港線）にも存在するため関東優先の PASMO で解決
    [InlineData(0xE72E, CardType.PASMO, "四ツ谷")]          // 南北線 表記修正
    [InlineData(0xEE01, CardType.PASMO, "リゾートゲートウェイ・ステーション")]   // ディズニーリゾートライン 正式駅名へ統一
    [InlineData(0xEE02, CardType.PASMO, "東京ディズニーランド・ステーション")]   // 同上
    [InlineData(0xEE04, CardType.PASMO, "ベイサイド・ステーション")]             // 同上
    [InlineData(0xEE06, CardType.PASMO, "東京ディズニーシー・ステーション")]     // 同上
    [InlineData(0xFE16, CardType.PASMO, "富士フイルム前")]  // 大雄山線 正式表記（旧 富士フィルム前）
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
