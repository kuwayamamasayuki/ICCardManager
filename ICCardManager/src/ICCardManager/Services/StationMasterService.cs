using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Reflection;
using ICCardManager.Common;

namespace ICCardManager.Services
{
/// <summary>
    /// 駅コードから駅名を解決するサービス
    /// </summary>
    /// <remarks>
    /// 埋め込みリソースのCSVファイルから全国の駅データを読み込み、
    /// エリアコード+路線コード+駅コードから駅名を検索する。
    /// カード種別に応じて適切なエリアを優先的に検索する。
    /// </remarks>
    public class StationMasterService
    {
        private static readonly Lazy<StationMasterService> _instance = new(() => new StationMasterService());

        /// <summary>
        /// シングルトンインスタンス
        /// </summary>
        public static StationMasterService Instance => _instance.Value;

        /// <summary>
        /// 駅データのディクショナリ
        /// キー: (areaCode, lineCode, stationCode), 値: 駅名
        /// </summary>
        private readonly Dictionary<(int areaCode, int lineCode, int stationCode), string> _stations = new();

        /// <summary>
        /// 路線名のディクショナリ
        /// キー: (areaCode, lineCode), 値: 路線名
        /// </summary>
        private readonly Dictionary<(int areaCode, int lineCode), string> _lineNames = new();

        /// <summary>
        /// 各エリアで利用可能な路線コードのセット（検索最適化用）
        /// </summary>
        private readonly Dictionary<int, HashSet<int>> _areaLineCodes = new();

        /// <summary>
        /// データロード済みフラグ
        /// </summary>
        private bool _isLoaded;

        private readonly object _loadLock = new();

        /// <summary>
        /// カード種別ごとの優先エリアコード
        /// </summary>
        /// <remarks>
        /// エリアコード:
        /// 0 = JR東日本・関東圏
        /// 1 = JR西日本・関西圏
        /// 2 = JR東海・中部圏
        /// 3 = JR九州・九州圏
        /// </remarks>
        private static readonly Dictionary<CardType, int[]> CardTypeAreaPriority = new()
        {
            { CardType.Hayakaken, new[] { 3, 0, 1, 2 } },  // はやかけん: 九州優先
            { CardType.SUGOCA, new[] { 3, 0, 1, 2 } },     // SUGOCA: 九州優先
            { CardType.Nimoca, new[] { 3, 0, 1, 2 } },     // nimoca: 九州優先
            { CardType.Suica, new[] { 0, 1, 2, 3 } },      // Suica: 関東優先
            { CardType.PASMO, new[] { 0, 1, 2, 3 } },      // PASMO: 関東優先
            { CardType.Kitaca, new[] { 0, 3, 1, 2 } },     // Kitaca: 北海道(0に含む)→九州
            { CardType.ICOCA, new[] { 1, 0, 2, 3 } },      // ICOCA: 関西優先
            { CardType.PiTaPa, new[] { 1, 0, 2, 3 } },     // PiTaPa: 関西優先
            { CardType.TOICA, new[] { 2, 0, 1, 3 } },      // TOICA: 中部優先
            { CardType.Manaca, new[] { 2, 0, 1, 3 } },     // manaca: 中部優先
            { CardType.Unknown, new[] { 0, 1, 2, 3 } },    // 不明: 関東から順に検索
        };

        private StationMasterService()
        {
            // 遅延初期化のためコンストラクタでは読み込まない
        }

        /// <summary>
        /// 駅データを読み込む
        /// </summary>
        public void EnsureLoaded()
        {
            if (_isLoaded) return;

            lock (_loadLock)
            {
                if (_isLoaded) return;

                try
                {
                    LoadFromEmbeddedResource();
                    _isLoaded = true;
                    System.Diagnostics.Debug.WriteLine($"駅マスタ読み込み完了: {_stations.Count}件, {_lineNames.Count}路線");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"駅マスタ読み込みエラー: {ex.Message}");
                    LoadFallbackData();
                    _isLoaded = true;
                }
            }
        }

        /// <summary>
        /// 駅コードから駅名を取得（カード種別指定なし、全エリア検索）
        /// </summary>
        /// <param name="stationCode">駅コード（上位バイト:路線コード, 下位バイト:駅番号）</param>
        /// <returns>駅名。見つからない場合は"駅{XX}-{YY}"形式</returns>
        public string GetStationName(int stationCode)
        {
            return GetStationName(stationCode, CardType.Unknown);
        }

        /// <summary>
        /// 駅コードから駅名を取得（カード種別指定、優先エリアから検索）
        /// </summary>
        /// <param name="stationCode">駅コード（上位バイト:路線コード, 下位バイト:駅番号）</param>
        /// <param name="cardType">カード種別（検索優先エリアの決定に使用）</param>
        /// <returns>駅名。見つからない場合は"駅{XX}-{YY}"形式</returns>
        public string GetStationName(int stationCode, CardType cardType)
        {
            EnsureLoaded();

            var lineCode = (stationCode >> 8) & 0xFF;
            var stationNum = stationCode & 0xFF;

            // カード種別に応じた優先順序でエリアを検索
            var areaPriority = GetAreaPriority(cardType);

            foreach (var areaCode in areaPriority)
            {
                if (_stations.TryGetValue((areaCode, lineCode, stationNum), out var stationName))
                {
                    return stationName;
                }
            }

            // 見つからない場合はコードをそのまま表示
            return $"駅{lineCode:X2}-{stationNum:X2}";
        }

        /// <summary>
        /// エリアコードを指定して駅名を取得
        /// </summary>
        /// <param name="areaCode">エリアコード</param>
        /// <param name="lineCode">路線コード</param>
        /// <param name="stationNum">駅番号</param>
        /// <returns>駅名。見つからない場合はnull</returns>
        public string GetStationNameByArea(int areaCode, int lineCode, int stationNum)
        {
            EnsureLoaded();

            if (_stations.TryGetValue((areaCode, lineCode, stationNum), out var stationName))
            {
                return stationName;
            }

            return null;
        }

        /// <summary>
        /// 路線コードと駅番号から駅名を取得（全エリア検索）
        /// </summary>
        /// <param name="lineCode">路線コード</param>
        /// <param name="stationNum">駅番号</param>
        /// <returns>駅名。見つからない場合はnull</returns>
        public string GetStationNameOrNull(int lineCode, int stationNum)
        {
            return GetStationNameOrNull(lineCode, stationNum, CardType.Unknown);
        }

        /// <summary>
        /// 路線コードと駅番号から駅名を取得（カード種別で優先エリア指定）
        /// </summary>
        /// <param name="lineCode">路線コード</param>
        /// <param name="stationNum">駅番号</param>
        /// <param name="cardType">カード種別</param>
        /// <returns>駅名。見つからない場合はnull</returns>
        public string GetStationNameOrNull(int lineCode, int stationNum, CardType cardType)
        {
            EnsureLoaded();

            var areaPriority = GetAreaPriority(cardType);

            foreach (var areaCode in areaPriority)
            {
                if (_stations.TryGetValue((areaCode, lineCode, stationNum), out var stationName))
                {
                    return stationName;
                }
            }

            return null;
        }

        /// <summary>
        /// 路線コードから路線名を取得
        /// </summary>
        /// <param name="lineCode">路線コード</param>
        /// <returns>路線名。見つからない場合はnull</returns>
        public string GetLineName(int lineCode)
        {
            return GetLineName(lineCode, CardType.Unknown);
        }

        /// <summary>
        /// 路線コードから路線名を取得（カード種別で優先エリア指定）
        /// </summary>
        /// <param name="lineCode">路線コード</param>
        /// <param name="cardType">カード種別</param>
        /// <returns>路線名。見つからない場合はnull</returns>
        public string GetLineName(int lineCode, CardType cardType)
        {
            EnsureLoaded();

            var areaPriority = GetAreaPriority(cardType);

            foreach (var areaCode in areaPriority)
            {
                if (_lineNames.TryGetValue((areaCode, lineCode), out var lineName))
                {
                    return lineName;
                }
            }

            return null;
        }

        /// <summary>
        /// カード種別から優先エリアコードの配列を取得
        /// </summary>
        private static int[] GetAreaPriority(CardType cardType)
        {
            if (CardTypeAreaPriority.TryGetValue(cardType, out var priority))
            {
                return priority;
            }
            return CardTypeAreaPriority[CardType.Unknown];
        }

        /// <summary>
        /// 埋め込みリソースからCSVを読み込む
        /// </summary>
        private void LoadFromEmbeddedResource()
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("ICCardManager.Resources.StationCode.csv");

            if (stream == null)
            {
                throw new InvalidOperationException("駅コードマスタリソースが見つかりません");
            }

            using var reader = new StreamReader(stream);

            // ヘッダー行をスキップ
            var header = reader.ReadLine();
            if (header == null) return;

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var fields = ParseCsvLine(line);
                if (fields.Length < 6) continue;

                if (!int.TryParse(fields[0], out var areaCode)) continue;
                if (!int.TryParse(fields[1], out var lineCode)) continue;
                if (!int.TryParse(fields[2], out var stationCode)) continue;

                var lineName = fields[4].Trim();
                var stationName = fields[5].Trim();

                // 空の駅名はスキップ
                if (string.IsNullOrEmpty(stationName)) continue;

                // 路線コードが1バイトに収まる場合のみ登録
                if (lineCode <= 255)
                {
                    var stationKey = (areaCode, lineCode, stationCode);

                    // 重複がある場合は最初のエントリを優先
                    if (!_stations.ContainsKey(stationKey))
                    {
                        _stations[stationKey] = stationName;
                    }

                    // エリアごとの路線コードを記録
                    if (!_areaLineCodes.ContainsKey(areaCode))
                    {
                        _areaLineCodes[areaCode] = new HashSet<int>();
                    }
                    _areaLineCodes[areaCode].Add(lineCode);
                }

                // 路線名も登録
                if (!string.IsNullOrEmpty(lineName))
                {
                    var lineKey = (areaCode, lineCode);
                    if (!_lineNames.ContainsKey(lineKey))
                    {
                        _lineNames[lineKey] = lineName;
                    }
                }
            }
        }

        /// <summary>
        /// CSVの1行をパース（カンマ区切り、クォート対応）
        /// </summary>
        private static string[] ParseCsvLine(string line)
        {
            var result = new List<string>();
            var inQuotes = false;
            var currentField = new System.Text.StringBuilder();

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(currentField.ToString());
                    currentField.Clear();
                }
                else
                {
                    currentField.Append(c);
                }
            }

            result.Add(currentField.ToString());
            return result.ToArray();
        }

        /// <summary>
        /// フォールバック用の主要駅データ（全国対応）
        /// </summary>
        private void LoadFallbackData()
        {
            // === 九州エリア (Area 3) ===

            // 福岡市地下鉄 空港線 (LineCode=231)
            var airportLine = new (int station, string name)[]
            {
                (1, "姪浜"), (3, "室見"), (5, "藤崎"), (7, "西新"),
                (9, "唐人町"), (11, "大濠公園"), (13, "赤坂"), (15, "天神"),
                (17, "中洲川端"), (19, "祇園"), (21, "博多"), (23, "東比恵"),
                (25, "福岡空港")
            };
            foreach (var (station, name) in airportLine)
            {
                _stations[(3, 231, station)] = name;
            }
            _lineNames[(3, 231)] = "空港線";

            // 福岡市地下鉄 箱崎線 (LineCode=232)
            var hakozakiLine = new (int station, string name)[]
            {
                (1, "中洲川端"), (3, "呉服町"), (5, "千代県庁口"),
                (7, "馬出九大病院前"), (9, "箱崎宮前"), (11, "箱崎九大前"),
                (13, "貝塚")
            };
            foreach (var (station, name) in hakozakiLine)
            {
                _stations[(3, 232, station)] = name;
            }
            _lineNames[(3, 232)] = "箱崎線";

            // 福岡市地下鉄 七隈線 (LineCode=233)
            var nanakumaLine = new (int station, string name)[]
            {
                (1, "橋本"), (3, "次郎丸"), (5, "賀茂"), (7, "野芥"),
                (9, "梅林"), (11, "福大前"), (13, "七隈"), (15, "金山"),
                (17, "茶山"), (19, "別府"), (21, "六本松"), (23, "桜坂"),
                (25, "薬院大通"), (27, "薬院"), (29, "渡辺通"), (31, "天神南")
            };
            foreach (var (station, name) in nanakumaLine)
            {
                _stations[(3, 233, station)] = name;
            }
            _lineNames[(3, 233)] = "七隈線";

            // === 関東エリア (Area 0) ===

            // JR山手線 (LineCode=2)
            var yamanoteLine = new (int station, string name)[]
            {
                (1, "東京"), (2, "有楽町"), (3, "新橋"), (4, "浜松町"),
                (5, "田町"), (6, "品川"), (7, "大崎"), (8, "五反田"),
                (9, "目黒"), (10, "恵比寿"), (11, "渋谷"), (12, "原宿"),
                (13, "代々木"), (14, "新宿"), (15, "新大久保"), (16, "高田馬場"),
                (17, "目白"), (18, "池袋"), (19, "大塚"), (20, "巣鴨"),
                (21, "駒込"), (22, "田端"), (23, "西日暮里"), (24, "日暮里"),
                (25, "鶯谷"), (26, "上野"), (27, "御徒町"), (28, "秋葉原"),
                (29, "神田")
            };
            foreach (var (station, name) in yamanoteLine)
            {
                _stations[(0, 2, station)] = name;
            }
            _lineNames[(0, 2)] = "山手線";

            // JR中央線 (LineCode=5)
            var chuoLine = new (int station, string name)[]
            {
                (1, "東京"), (2, "神田"), (3, "御茶ノ水"), (4, "四ツ谷"),
                (5, "新宿"), (6, "中野"), (7, "高円寺"), (8, "阿佐ヶ谷"),
                (9, "荻窪"), (10, "西荻窪"), (11, "吉祥寺"), (12, "三鷹"),
                (13, "武蔵境"), (14, "東小金井"), (15, "武蔵小金井"), (16, "国分寺"),
                (17, "西国分寺"), (18, "国立"), (19, "立川"), (20, "日野"),
                (21, "豊田"), (22, "八王子"), (23, "西八王子"), (24, "高尾")
            };
            foreach (var (station, name) in chuoLine)
            {
                _stations[(0, 5, station)] = name;
            }
            _lineNames[(0, 5)] = "中央線";

            // JR鹿児島本線 (Area 0, LineCode=6) - JR九州だがArea 0に登録されている
            var kagoshimaLine = new (int station, string name)[]
            {
                (1, "門司港"), (2, "小森江"), (3, "門司"), (6, "小倉"),
                (35, "香椎"), (36, "千早"), (37, "箱崎"), (38, "吉塚"),
                (39, "博多"), (40, "竹下"), (42, "南福岡"), (43, "春日"),
                (44, "大野城"), (47, "二日市"), (55, "鳥栖"), (59, "久留米"),
                (77, "大牟田")
            };
            foreach (var (station, name) in kagoshimaLine)
            {
                _stations[(0, 6, station)] = name;
            }
            _lineNames[(0, 6)] = "鹿児島本線";

            // === 関西エリア (Area 1) ===

            // JR大阪環状線 (LineCode=11)
            var osakaLoopLine = new (int station, string name)[]
            {
                (1, "大阪"), (2, "福島"), (3, "野田"), (4, "西九条"),
                (5, "弁天町"), (6, "大正"), (7, "芦原橋"), (8, "今宮"),
                (9, "新今宮"), (10, "天王寺"), (11, "寺田町"), (12, "桃谷"),
                (13, "鶴橋"), (14, "玉造"), (15, "森ノ宮"), (16, "大阪城公園"),
                (17, "京橋"), (18, "桜ノ宮"), (19, "天満")
            };
            foreach (var (station, name) in osakaLoopLine)
            {
                _stations[(1, 11, station)] = name;
            }
            _lineNames[(1, 11)] = "大阪環状線";

            // === 中部エリア (Area 2) ===

            // JR東海道本線 名古屋周辺 (LineCode=1)
            var tokaidoNagoya = new (int station, string name)[]
            {
                (1, "名古屋"), (2, "尾頭橋"), (3, "金山"), (4, "熱田"),
                (5, "笠寺"), (6, "大高"), (7, "南大高"), (8, "共和"),
                (9, "大府"), (10, "逢妻"), (11, "刈谷")
            };
            foreach (var (station, name) in tokaidoNagoya)
            {
                _stations[(2, 1, station)] = name;
            }
            _lineNames[(2, 1)] = "東海道本線";

            // エリアごとの路線コードを記録
            _areaLineCodes[0] = new HashSet<int> { 2, 5, 6 };
            _areaLineCodes[1] = new HashSet<int> { 11 };
            _areaLineCodes[2] = new HashSet<int> { 1 };
            _areaLineCodes[3] = new HashSet<int> { 231, 232, 233 };

            System.Diagnostics.Debug.WriteLine($"フォールバックデータ読み込み完了: {_stations.Count}件");
        }

        /// <summary>
        /// 登録されている駅数を取得（テスト用）
        /// </summary>
        public int StationCount
        {
            get
            {
                EnsureLoaded();
                return _stations.Count;
            }
        }

        /// <summary>
        /// 登録されている路線数を取得（テスト用）
        /// </summary>
        public int LineCount
        {
            get
            {
                EnsureLoaded();
                return _lineNames.Count;
            }
        }

        /// <summary>
        /// 指定エリアの駅数を取得（テスト用）
        /// </summary>
        public int GetStationCountByArea(int areaCode)
        {
            EnsureLoaded();
            return _stations.Count(s => s.Key.areaCode == areaCode);
        }
    }
}
