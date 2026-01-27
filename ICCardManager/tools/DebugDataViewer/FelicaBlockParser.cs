using System;
using System.Collections.Generic;
using System.Text;

namespace DebugDataViewer
{
    /// <summary>
    /// FeliCa履歴ブロック（16バイト）の各フィールド解析結果
    /// </summary>
    public class FelicaFieldInfo
    {
        /// <summary>バイト位置の表示文字列（例: "[04-05]"）</summary>
        public string OffsetLabel { get; set; }

        /// <summary>フィールド名（日本語）</summary>
        public string FieldName { get; set; }

        /// <summary>16進数表現（例: "4A 2B"）</summary>
        public string HexValue { get; set; }

        /// <summary>2進数表現（例: "01001010 00101011"、日付フィールドはビットグループ表示）</summary>
        public string BinaryValue { get; set; }

        /// <summary>デコード結果（例: "2025/01/15"）</summary>
        public string DecodedValue { get; set; }

        /// <summary>補足説明（例: "年=25, 月=1, 日=15"）</summary>
        public string Detail { get; set; }
    }

    /// <summary>
    /// FeliCa履歴ブロック（16バイト）をフィールド単位にパースし、
    /// ビット単位の表示データを生成するユーティリティ
    /// </summary>
    public static class FelicaBlockParser
    {
        /// <summary>
        /// 16バイトの生データをフィールドごとに分解する
        /// </summary>
        /// <param name="rawBytes">FeliCa履歴ブロック（16バイト）</param>
        /// <returns>フィールド解析結果のリスト（9項目）</returns>
        public static List<FelicaFieldInfo> Parse(byte[] rawBytes)
        {
            if (rawBytes == null || rawBytes.Length < 16)
            {
                return new List<FelicaFieldInfo>();
            }

            var fields = new List<FelicaFieldInfo>();

            // [00] 機器種別
            fields.Add(new FelicaFieldInfo
            {
                OffsetLabel = "[00]",
                FieldName = "機器種別",
                HexValue = $"{rawBytes[0]:X2}",
                BinaryValue = FormatBinary(rawBytes[0]),
                DecodedValue = $"0x{rawBytes[0]:X2}",
                Detail = ""
            });

            // [01] 利用種別
            fields.Add(new FelicaFieldInfo
            {
                OffsetLabel = "[01]",
                FieldName = "利用種別",
                HexValue = $"{rawBytes[1]:X2}",
                BinaryValue = FormatBinary(rawBytes[1]),
                DecodedValue = DecodeUsageType(rawBytes[1]),
                Detail = ""
            });

            // [02] 支払種別
            fields.Add(new FelicaFieldInfo
            {
                OffsetLabel = "[02]",
                FieldName = "支払種別",
                HexValue = $"{rawBytes[2]:X2}",
                BinaryValue = FormatBinary(rawBytes[2]),
                DecodedValue = $"0x{rawBytes[2]:X2}",
                Detail = ""
            });

            // [03] 入出場種別
            fields.Add(new FelicaFieldInfo
            {
                OffsetLabel = "[03]",
                FieldName = "入出場種別",
                HexValue = $"{rawBytes[3]:X2}",
                BinaryValue = FormatBinary(rawBytes[3]),
                DecodedValue = $"0x{rawBytes[3]:X2}",
                Detail = ""
            });

            // [04-05] 日付（ビットフィールド）
            fields.Add(ParseDateField(rawBytes[4], rawBytes[5]));

            // [06-07] 入場駅コード（ビッグエンディアン）
            fields.Add(ParseStationCodeField("[06-07]", "入場駅コード", rawBytes[6], rawBytes[7]));

            // [08-09] 出場駅コード（ビッグエンディアン）
            fields.Add(ParseStationCodeField("[08-09]", "出場駅コード", rawBytes[8], rawBytes[9]));

            // [0A-0B] 残額（リトルエンディアン）
            fields.Add(ParseBalanceField(rawBytes[10], rawBytes[11]));

            // [0C-0F] 予備
            fields.Add(new FelicaFieldInfo
            {
                OffsetLabel = "[0C-0F]",
                FieldName = "予備",
                HexValue = $"{rawBytes[12]:X2} {rawBytes[13]:X2} {rawBytes[14]:X2} {rawBytes[15]:X2}",
                BinaryValue = $"{FormatBinary(rawBytes[12])} {FormatBinary(rawBytes[13])} {FormatBinary(rawBytes[14])} {FormatBinary(rawBytes[15])}",
                DecodedValue = "--",
                Detail = ""
            });

            return fields;
        }

        /// <summary>
        /// フィールド情報を整形されたテキストに変換する
        /// </summary>
        public static string FormatAsText(List<FelicaFieldInfo> fields)
        {
            if (fields == null || fields.Count == 0)
            {
                return "(解析データなし)";
            }

            var sb = new StringBuilder();

            sb.AppendLine("=== ビット単位フィールド解析 ===");
            sb.AppendLine();

            // ヘッダー
            sb.AppendLine(
                $"{"Offset",-8}" +
                $"{"フィールド",-14}" +
                $"{"Hex",-14}" +
                $"{"Binary",-40}" +
                $"{"デコード値",-20}" +
                "詳細");

            sb.AppendLine(
                $"{"------",-8}" +
                $"{"----------",-14}" +
                $"{"---",-14}" +
                $"{"------",-40}" +
                $"{"----------",-20}" +
                "----");

            foreach (var field in fields)
            {
                var line =
                    $"{field.OffsetLabel,-8}" +
                    $"{field.FieldName,-14}" +
                    $"{field.HexValue,-14}" +
                    $"{field.BinaryValue,-40}" +
                    $"{field.DecodedValue,-20}" +
                    field.Detail;

                sb.AppendLine(line);
            }

            return sb.ToString();
        }

        /// <summary>
        /// バイト値を8桁の2進数文字列に変換する
        /// </summary>
        public static string FormatBinary(byte value)
        {
            return Convert.ToString(value, 2).PadLeft(8, '0');
        }

        /// <summary>
        /// 利用種別をデコードする
        /// </summary>
        private static string DecodeUsageType(byte usageType)
        {
            switch (usageType)
            {
                case 0x02: return "チャージ (0x02)";
                case 0x07: return "物販 (0x07)";
                default: return $"乗車 (0x{usageType:X2})";
            }
        }

        /// <summary>
        /// 日付フィールド（バイト4-5）をビットフィールドとして解析する
        /// </summary>
        public static FelicaFieldInfo ParseDateField(byte high, byte low)
        {
            var dateValue = (high << 8) | low;
            var yearOffset = (dateValue >> 9) & 0x7F;
            var month = (dateValue >> 5) & 0x0F;
            var day = dateValue & 0x1F;
            var year = 2000 + yearOffset;

            // ビットグループ表示: [YYYYYYY][MMMM][DDDDD]
            var bits16 = Convert.ToString(dateValue, 2).PadLeft(16, '0');
            var yearBits = bits16.Substring(0, 7);
            var monthBits = bits16.Substring(7, 4);
            var dayBits = bits16.Substring(11, 5);
            var groupedBinary = $"[{yearBits}][{monthBits}][{dayBits}]";

            // デコード値
            string decodedValue;
            if (month >= 1 && month <= 12 && day >= 1 && day <= 31)
            {
                decodedValue = $"{year}/{month:D2}/{day:D2}";
            }
            else
            {
                decodedValue = "(無効な日付)";
            }

            return new FelicaFieldInfo
            {
                OffsetLabel = "[04-05]",
                FieldName = "日付",
                HexValue = $"{high:X2} {low:X2}",
                BinaryValue = groupedBinary,
                DecodedValue = decodedValue,
                Detail = $"年(bit15-9)={yearOffset}(+2000), 月(bit8-5)={month}, 日(bit4-0)={day}"
            };
        }

        /// <summary>
        /// 駅コードフィールド（ビッグエンディアン16bit）を解析する
        /// </summary>
        private static FelicaFieldInfo ParseStationCodeField(string offsetLabel, string fieldName, byte high, byte low)
        {
            var code = (high << 8) | low;

            return new FelicaFieldInfo
            {
                OffsetLabel = offsetLabel,
                FieldName = fieldName,
                HexValue = $"{high:X2} {low:X2}",
                BinaryValue = $"{FormatBinary(high)} {FormatBinary(low)}",
                DecodedValue = code > 0 ? $"0x{code:X4} ({code})" : "なし (0)",
                Detail = "BE"
            };
        }

        /// <summary>
        /// 残額フィールド（リトルエンディアン16bit）を解析する
        /// </summary>
        public static FelicaFieldInfo ParseBalanceField(byte low, byte high)
        {
            var balance = low + (high << 8);

            return new FelicaFieldInfo
            {
                OffsetLabel = "[0A-0B]",
                FieldName = "残額",
                HexValue = $"{low:X2} {high:X2}",
                BinaryValue = $"{FormatBinary(low)} {FormatBinary(high)}",
                DecodedValue = $"\u00a5{balance:N0}",
                Detail = $"LE (0x{balance:X4})"
            };
        }
    }
}
