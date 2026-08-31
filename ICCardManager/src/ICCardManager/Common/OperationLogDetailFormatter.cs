using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ICCardManager.Common
{
    /// <summary>
    /// 監査ログ（<c>operation_log</c>）に記録された利用明細（<see cref="Models.Ledger.Details"/>）を
    /// 人間が読める文字列へ整形する（Issue #1979）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OperationLogExcelExportService.GetFieldNameMap</c> の <c>"ledger"</c> に <c>Details</c> の
    /// エントリが無かったため、6 年保存の <c>BeforeData</c> / <c>AfterData</c> に値があるのに
    /// 操作ログ画面・Excel からは明細が一切見えなかった。<c>FormatPropertyValue</c> は
    /// 単一のスカラー値を前提にしており、配列 of オブジェクトはマップへ 1 行足すだけでは描画できない。
    /// </para>
    /// <para>
    /// 整形の手段は本クラスただ 1 つに寄せる（画面 <c>OperationLogSearchViewModel</c> と
    /// Excel <c>OperationLogExcelExportService</c> の双方が使う）。手段が 2 通りあると、
    /// モデルへ列を足したとき片方だけが更新される
    /// （`.claude/rules/development-conventions.md`「同じ論理的な処理に手段が 2 通りあるか」Issue #1763）。
    /// 区間表記は <see cref="RouteDisplayFormatter"/> へ委譲する（バスのラベル・プレースホルダは
    /// 組織設定由来のため直書きしない。Issue #1818）。
    /// </para>
    /// </remarks>
    public static class OperationLogDetailFormatter
    {
        /// <summary>JSON のプロパティ名（<c>System.Text.Json</c> 既定の PascalCase）。</summary>
        public const string DetailsPropertyName = "Details";

        /// <summary>表示ラベル。</summary>
        public const string DetailsDisplayName = "利用明細";

        /// <summary>
        /// Excel の「変更前 / 変更後」列で 1 台帳あたりに展開する明細の上限。
        /// </summary>
        /// <remarks>
        /// 交通系ICカードの履歴読み取りは最大 20 件（`.claude/rules/development-conventions.md`）だが、
        /// 統合を繰り返した台帳はそれを超え得る。Excel のセルは 32,767 文字が上限で、
        /// 監査成果物として提出する行の高さも際限なく伸びるため上限を設ける。
        /// 超過分は件数で示し、全件は履歴画面（明細ダイアログ）で確認する。
        /// </remarks>
        internal const int MaxExpandedDetailLines = 20;

        /// <summary>操作ログ画面の「詳細」列に載せる明細差分の上限件数。</summary>
        internal const int MaxSummarizedDetailChanges = 3;

        /// <summary>操作ログ画面の「詳細」列で 1 明細に許す文字数（既存の値と同じ 30 文字）。</summary>
        internal const int MaxSummarizedValueLength = 30;

        private const string AbsentDetailText = "（なし）";

        /// <summary>
        /// 明細配列を「N件」＋番号付きの行へ展開する。
        /// </summary>
        /// <param name="details">明細配列の <see cref="JsonElement"/>。</param>
        /// <param name="indent">各明細行の字下げ。</param>
        /// <returns>
        /// 展開した文字列。<b>明細が 0 件・<c>null</c>・配列でない場合は <c>null</c></b>
        /// （＝行を出さない）。明細を持たない台帳の監査記録に「利用明細: 0件」という
        /// 情報量ゼロの行が全件へ付くのを避けるため。
        /// </returns>
        public static string FormatDetailsBlock(JsonElement details, string indent)
        {
            if (details.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var items = details.EnumerateArray().ToList();
            if (items.Count == 0)
            {
                return null;
            }

            var lines = new List<string> { $"{items.Count}件" };
            var shown = Math.Min(items.Count, MaxExpandedDetailLines);
            for (var i = 0; i < shown; i++)
            {
                lines.Add($"{indent}{i + 1}. {FormatDetailLine(items[i])}");
            }

            if (items.Count > shown)
            {
                lines.Add($"{indent}…ほか{items.Count - shown}件（履歴画面の明細で確認）");
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 明細 1 件を 1 行の文字列へ整形する。
        /// </summary>
        /// <remarks>
        /// 順序（<c>SequenceNumber</c>）とグループ（<c>GroupId</c>）を併記するのは、
        /// 統合・分割の監査でこの 2 つが「どの明細がどう動いたか」そのものだからである。
        /// 統合は摘要を再生成するために <c>SequenceNumber</c> を一時再採番し（Issue #1959）、
        /// <c>GroupId</c> は摘要のグループ分けを決める（Issue #484 / #1858）。
        /// </remarks>
        public static string FormatDetailLine(JsonElement detail)
        {
            if (detail.ValueKind != JsonValueKind.Object)
            {
                return detail.ToString();
            }

            var parts = new List<string>();

            var useDate = GetDateTime(detail, "UseDate");
            if (useDate.HasValue)
            {
                parts.Add(DisplayFormatters.FormatDate(useDate.Value));
            }

            parts.Add(RouteDisplayFormatter.Format(
                isCharge: GetBoolean(detail, "IsCharge"),
                isPointRedemption: GetBoolean(detail, "IsPointRedemption"),
                isBus: GetBoolean(detail, "IsBus"),
                busStops: GetString(detail, "BusStops"),
                entryStation: GetString(detail, "EntryStation"),
                exitStation: GetString(detail, "ExitStation"),
                fallback: "区間不明"));

            var amount = GetInt(detail, "Amount");
            if (amount.HasValue)
            {
                parts.Add($"{amount.Value:N0}円");
            }

            var balance = GetInt(detail, "Balance");
            if (balance.HasValue)
            {
                parts.Add($"残{balance.Value:N0}円");
            }

            var attributes = new List<string>();
            var sequenceNumber = GetInt(detail, "SequenceNumber");
            if (sequenceNumber.HasValue)
            {
                attributes.Add($"順序{sequenceNumber.Value}");
            }

            var groupId = GetInt(detail, "GroupId");
            if (groupId.HasValue)
            {
                attributes.Add($"グループ{groupId.Value}");
            }

            var text = string.Join(" ", parts);
            return attributes.Count > 0
                ? $"{text}（{string.Join("、", attributes)}）"
                : text;
        }

        /// <summary>
        /// 変更前後の明細配列を索引で突き合わせ、変化した明細だけを返す。
        /// </summary>
        /// <returns>
        /// (索引は 1 始まり, 変更前の行, 変更後の行) の一覧。片側にしか無い明細は
        /// 反対側が「（なし）」になる。変化が無ければ空。
        /// </returns>
        public static IReadOnlyList<(int Index, string Before, string After)> DiffDetailLines(
            JsonElement? before, JsonElement? after)
        {
            var beforeLines = EnumerateDetailLines(before);
            var afterLines = EnumerateDetailLines(after);

            var result = new List<(int, string, string)>();
            var count = Math.Max(beforeLines.Count, afterLines.Count);
            for (var i = 0; i < count; i++)
            {
                var b = i < beforeLines.Count ? beforeLines[i] : AbsentDetailText;
                var a = i < afterLines.Count ? afterLines[i] : AbsentDetailText;
                if (b != a)
                {
                    result.Add((i + 1, b, a));
                }
            }

            return result;
        }

        /// <summary>
        /// Excel の「変更内容」列に載せる明細差分（1 行 1 明細）。変化が無ければ空文字。
        /// </summary>
        public static string SummarizeDetailChangesForExport(JsonElement? before, JsonElement? after)
        {
            var diffs = DiffDetailLines(before, after);
            return string.Join("\n", diffs.Select(d =>
                $"{DetailsDisplayName}[{d.Index}]: {d.Before} → {d.After}"));
        }

        /// <summary>
        /// 操作ログ画面の「詳細」列に載せる明細差分。DataGrid のセルに収まるよう
        /// 件数と 1 件あたりの文字数を切り詰める。変化が無ければ空文字。
        /// </summary>
        public static string SummarizeDetailChangesForScreen(string beforeJson, string afterJson)
        {
            var before = TryGetDetailsElement(beforeJson);
            var after = TryGetDetailsElement(afterJson);
            var diffs = DiffDetailLines(before, after);
            if (diffs.Count == 0)
            {
                return string.Empty;
            }

            var shown = diffs.Take(MaxSummarizedDetailChanges).Select(d =>
                $"{DetailsDisplayName}[{d.Index}]: {Truncate(d.Before)}→{Truncate(d.After)}");
            var text = string.Join("、", shown);

            if (diffs.Count > MaxSummarizedDetailChanges)
            {
                text += $"、ほか{diffs.Count - MaxSummarizedDetailChanges}件";
            }

            return text;
        }

        /// <summary>
        /// 統合・分割の監査ログ向けに、明細件数の推移を返す（例: 「明細 2件・3件 → 5件」）。
        /// </summary>
        /// <remarks>
        /// 統合は <c>BeforeData</c> が台帳の配列、分割は <c>AfterData</c> が台帳の配列になるため、
        /// どちらの側もオブジェクト／配列の両方を受け付ける。
        /// 明細がどこにも無ければ空文字を返し、余計な行を出さない。
        /// </remarks>
        public static string SummarizeDetailCountTransition(string beforeJson, string afterJson)
        {
            var before = CountDetailsPerLedger(beforeJson);
            var after = CountDetailsPerLedger(afterJson);

            if (before.Sum() == 0 && after.Sum() == 0)
            {
                return string.Empty;
            }

            return $"明細 {FormatCounts(before)} → {FormatCounts(after)}";
        }

        /// <summary>
        /// 台帳 JSON（オブジェクト or 配列）から <c>Details</c> の要素を取り出す。
        /// 配列（統合の変更前・分割の変更後）は明細を連結した 1 つの並びとして扱えないため
        /// <c>null</c> を返す（索引での突き合わせが成立しないため）。
        /// </summary>
        private static JsonElement? TryGetDetailsElement(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!doc.RootElement.TryGetProperty(DetailsPropertyName, out var details))
                {
                    return null;
                }

                // JsonDocument の Dispose 後も参照できるようクローンする
                return details.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static IReadOnlyList<int> CountDetailsPerLedger(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return Array.Empty<int>();
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return doc.RootElement.EnumerateArray().Select(CountDetails).ToList();
                }

                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return new[] { CountDetails(doc.RootElement) };
                }

                return Array.Empty<int>();
            }
            catch (JsonException)
            {
                return Array.Empty<int>();
            }
        }

        private static int CountDetails(JsonElement ledger)
        {
            if (ledger.ValueKind != JsonValueKind.Object)
            {
                return 0;
            }

            if (!ledger.TryGetProperty(DetailsPropertyName, out var details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            return details.GetArrayLength();
        }

        private static string FormatCounts(IReadOnlyList<int> counts)
        {
            if (counts.Count == 0)
            {
                return "0件";
            }

            return string.Join("・", counts.Select(c => $"{c}件"));
        }

        private static IReadOnlyList<string> EnumerateDetailLines(JsonElement? details)
        {
            if (!details.HasValue || details.Value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return details.Value.EnumerateArray().Select(FormatDetailLine).ToList();
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxSummarizedValueLength)
            {
                return value;
            }

            return value.Substring(0, MaxSummarizedValueLength) + "...";
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) ||
                prop.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return prop.GetString();
        }

        private static bool GetBoolean(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) &&
                   prop.ValueKind == JsonValueKind.True;
        }

        private static int? GetInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) ||
                prop.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return prop.TryGetInt32(out var value) ? value : (int?)null;
        }

        private static DateTime? GetDateTime(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop) ||
                prop.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return DateTime.TryParse(
                prop.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                ? value
                : (DateTime?)null;
        }
    }
}
