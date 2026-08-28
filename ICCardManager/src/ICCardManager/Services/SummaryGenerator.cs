using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICCardManager.Common;
using ICCardManager.Models;

namespace ICCardManager.Services
{
/// <summary>
    /// 日別摘要の結果
    /// </summary>
    public class DailySummary
    {
        /// <summary>
        /// 利用日
        /// </summary>
        public DateTime Date { get; set; }

        /// <summary>
        /// 摘要文字列
        /// </summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>
        /// チャージかどうか
        /// </summary>
        public bool IsCharge { get; set; }

        /// <summary>
        /// ポイント還元かどうか
        /// </summary>
        public bool IsPointRedemption { get; set; }
    }

    /// <summary>
    /// 交通系ICカードの利用履歴から摘要文字列を生成するサービスです。
    /// </summary>
    /// <remarks>
    /// <para>
    /// このクラスは物品出納簿の「摘要」列に表示する文字列を生成します。
    /// 以下のパターンの摘要を生成できます：
    /// </para>
    /// <list type="table">
    /// <listheader>
    /// <term>パターン</term>
    /// <description>出力例</description>
    /// </listheader>
    /// <item>
    /// <term>単純片道</term>
    /// <description>鉄道（A駅～B駅）</description>
    /// </item>
    /// <item>
    /// <term>往復</term>
    /// <description>鉄道（A駅～B駅 往復）</description>
    /// </item>
    /// <item>
    /// <term>乗継</term>
    /// <description>鉄道（A駅～C駅）※途中駅は省略</description>
    /// </item>
    /// <item>
    /// <term>複数区間</term>
    /// <description>鉄道（A駅～B駅、C駅～D駅）</description>
    /// </item>
    /// <item>
    /// <term>片側駅名不明</term>
    /// <description>鉄道（A駅～?）※駅名を解決できなかった側は「?」。
    /// ただし運賃 0 円の片側欠落（入場記録のみ）は従来どおり出力しない（Issue #1735）</description>
    /// </item>
    /// <item>
    /// <term>バス混在</term>
    /// <description>鉄道（A駅～B駅）、バス（★） ※鉄道・バスのブロックは利用順（時系列）に
    /// 並ぶため、バスが先なら「バス（★）、鉄道（A駅～B駅）」になる。鉄道→バス→鉄道のように
    /// 交互に利用した場合はブロックも交互に並ぶ（Issue #1904）</description>
    /// </item>
    /// <item>
    /// <term>チャージ</term>
    /// <description>役務費によりチャージ（企業会計部局設定時は「旅費によりチャージ」。<see cref="OrganizationOptions"/>）</description>
    /// </item>
    /// <item>
    /// <term>ポイント還元</term>
    /// <description>ポイント還元</description>
    /// </item>
    /// <item>
    /// <term>払戻し</term>
    /// <description>払戻しによる払出</description>
    /// </item>
    /// </list>
    /// <para>
    /// バス利用時は「★」マークが表示され、後からバス停名を入力できます。
    /// </para>
    /// </remarks>
    public class SummaryGenerator
    {
        /// <summary>
        /// 駅名を解決できなかった側に充てるプレースホルダ（Issue #1735）
        /// </summary>
        /// <remarks>
        /// StationCode.csv 未収録の新駅などで片側の駅名だけが解決できなかった鉄道明細を、
        /// 摘要から黙って落とさず「A駅～?」の形で経路に採用するために使う。
        /// CSVインポートの明細説明文（CsvImportService.Detail.cs）と同じ表記。
        /// </remarks>
        internal const string UnknownStationPlaceholder = "?";

        private readonly DepartmentType _departmentType;

        /// <summary>
        /// 組織固有設定（Issue #974）
        /// </summary>
        private static OrganizationOptions _options = new();

        /// <summary>
        /// 設定値が空だった場合のフォールバック元（既定値の単一の真実源、Issue #1818）
        /// </summary>
        /// <remarks>
        /// リテラル（「バス」「★」）を直書きせず <see cref="SummaryTextOptions"/> の
        /// 既定値を参照する（<see cref="GetMidYearCarryoverLikePattern"/> のフォールバックと同じ流儀）。
        /// </remarks>
        private static readonly SummaryTextOptions DefaultSummaryText = new();

        /// <summary>
        /// TransferStationGroups のHashSet版キャッシュ
        /// </summary>
        private static List<HashSet<string>> _transferStationGroups = BuildTransferStationGroups(new OrganizationOptions());

        /// <summary>
        /// 組織固有設定を注入（起動時に1回だけ呼ぶ）
        /// </summary>
        public static void Configure(OrganizationOptions options)
        {
            _options = options ?? new OrganizationOptions();
            _transferStationGroups = BuildTransferStationGroups(_options);
        }

        /// <summary>
        /// 同一視グループだけを差し替える（Issue #1905）
        /// </summary>
        /// <param name="groups">同一とみなす駅名・バス停名のグループ</param>
        /// <remarks>
        /// <see cref="Configure"/> は起動時に 1 回だけ呼ぶ想定だが、同一視グループは
        /// システム管理画面から運用中に編集できる。<see cref="SummaryGenerator"/> は
        /// Singleton で静的状態を持つため、保存後に本メソッドで反映しないと
        /// アプリを再起動するまで新しいグループが効かない。
        ///
        /// 差し替えるのはグループだけで、<see cref="_options"/> の他の項目
        /// （摘要テキスト・生成ルールの ON/OFF）は保持する
        /// （<c>development-conventions.md</c>「UPDATE の SET 句は、その経路で本当に編集する列に限る」
        /// と同じ判断。全体を差し替えると呼び出し元が組み立て損ねた項目が既定値へ落ちる）。
        /// </remarks>
        public static void ApplyTransferStationGroups(IEnumerable<IEnumerable<string>> groups)
        {
            _options.SummaryRules.TransferStationGroups = (groups ?? Enumerable.Empty<IEnumerable<string>>())
                .Where(g => g != null)
                .Select(g => g.ToList())
                .ToList();
            _transferStationGroups = BuildTransferStationGroups(_options);
        }

        /// <summary>
        /// 現在有効な同一視グループを取得する（Issue #1905）
        /// </summary>
        /// <remarks>
        /// 編集画面（<c>TransferStationGroupViewModel</c>）は DB を正とするため
        /// <c>ITransferStationGroupService.GetGroupsAsync</c> から読み、本メソッドは使わない。
        /// これは <see cref="ApplyTransferStationGroups"/> が静的状態へ反映されたことを
        /// 外から確かめるための観測点（テストが使用）。呼び出し元が書き換えても
        /// 静的状態に影響しないようコピーを返す。
        /// </remarks>
        public static List<List<string>> GetTransferStationGroups()
            => _options.SummaryRules.TransferStationGroups
                .Select(g => g.ToList())
                .ToList();

        /// <summary>
        /// 設定をデフォルトにリセット（テスト用）
        /// </summary>
        internal static void ResetToDefaults()
        {
            _options = new OrganizationOptions();
            _transferStationGroups = BuildTransferStationGroups(_options);
        }

        /// <summary>
        /// バス利用のラベル（組織設定 <c>SummaryText.BusLabel</c> 由来、Issue #1818）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 摘要の生成・判定・抽出は、いずれもこのプロパティ（および本プロパティから導出する
        /// <see cref="FormatBusSummary"/> / <see cref="ExtractBusStopBlocks"/> /
        /// <see cref="TryExtractBusStops"/> / <see cref="ContainsBusLabel"/>）を経由すること。
        /// 生成側だけが設定値を使い判定側がリテラルを直書きすると、ラベルを
        /// 「乗合自動車」等へ変更した組織で判定だけが追従しない（Issue #1604 / #1749 と同型の乖離）。
        /// </para>
        /// <para>
        /// 空文字・空白のみの設定は既定値へフォールバックする。空ラベルを許すと
        /// <see cref="ExtractBusStopBlocks"/> がラベルを失って全角開き括弧だけを
        /// 開始記号とし、鉄道の括弧（「鉄道（A駅～B駅）」）まで拾ってバス停名として取り込むため
        /// （<see cref="IsMidYearCarryoverSummary"/> の不正正規表現フォールバックと同じ方針）。
        /// </para>
        /// <para>
        /// 汎用/固有の別: 交通系固有（バス混在表記）。
        /// </para>
        /// </remarks>
        public static string BusLabel => Coalesce(
            _options.SummaryText?.BusLabel, DefaultSummaryText.BusLabel);

        /// <summary>
        /// バス停名未入力時のプレースホルダ（組織設定 <c>SummaryText.BusPlaceholder</c> 由来、Issue #1818）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 未入力判定（<see cref="HasIncompleteBusStop"/> / <see cref="IsBusStopPlaceholder"/>）も
        /// 本プロパティから導出する。判定側が「★」を直書きすると、プレースホルダを
        /// 「※」等へ変更した組織でバス停名未入力の警告が常に 0 件になる。
        /// </para>
        /// <para>
        /// 空文字・空白のみの設定は既定値へフォールバックする。空プレースホルダを許すと
        /// <c>Contains("")</c> が常に true になり、すべての履歴が「未入力」と判定される。
        /// </para>
        /// <para>
        /// 汎用/固有の別: 交通系固有（バス混在表記）。
        /// </para>
        /// </remarks>
        public static string BusPlaceholder => Coalesce(
            _options.SummaryText?.BusPlaceholder, DefaultSummaryText.BusPlaceholder);

        /// <summary>
        /// 設定値が空（null／空白のみ）なら既定値へフォールバックする
        /// </summary>
        private static string Coalesce(string? configured, string fallback)
            => string.IsNullOrWhiteSpace(configured) ? fallback : configured;

        /// <summary>
        /// バス区間の摘要表記を生成（Issue #1818）
        /// </summary>
        /// <param name="busStops">バス停名（未入力の場合は <see cref="BusPlaceholder"/> を渡す）</param>
        /// <returns>「バス（A～B）」形式の文字列</returns>
        /// <remarks>
        /// 摘要生成だけでなく、表示整形（<c>Common.RouteDisplayFormatter</c>）・
        /// CSVインポートの明細説明文・テストデータ生成も本メソッドを通す。
        /// 書式（ラベル＋全角括弧）を 1 か所に閉じることで、
        /// <see cref="ExtractBusStopBlocks"/> の抽出対象と生成物が必ず対応する。
        /// 汎用/固有の別: 交通系固有。
        /// </remarks>
        public static string FormatBusSummary(string busStops)
            => $"{BusLabel}{FullWidthOpenParenthesis}{busStops}{FullWidthCloseParenthesis}";

        /// <summary>
        /// 摘要の書式に使う全角開き括弧（Issue #1914）
        /// </summary>
        /// <remarks>
        /// 生成（<see cref="FormatBusSummary"/>）と抽出（<see cref="ExtractBusStopBlocks"/>）・
        /// 対応判定（<see cref="HasBalancedFullWidthParentheses"/>）が同じ文字を見ることを
        /// 定数で保証する。片方だけ半角へ変えるといった乖離を作れなくするため。
        /// </remarks>
        internal const char FullWidthOpenParenthesis = '（';

        /// <summary>
        /// 摘要の書式に使う全角閉じ括弧（Issue #1914）
        /// </summary>
        /// <remarks>
        /// <see cref="FullWidthOpenParenthesis"/> と対。
        /// </remarks>
        internal const char FullWidthCloseParenthesis = '）';

        /// <summary>
        /// 全角括弧の対応が取れているかを判定する（Issue #1914）
        /// </summary>
        /// <param name="text">検査対象（null / 空文字は「対応が取れている」とみなす）</param>
        /// <returns>開き括弧と閉じ括弧が対応していれば true</returns>
        /// <remarks>
        /// <para>
        /// 摘要は「ラベル＋全角括弧」の区切り書式である一方、バス停名は自由入力のため
        /// 対応の取れない括弧（「天神）西口」等）を含み得る。この状態の摘要は
        /// <b>どこがブロックの終端なのかを決められない</b>ため、
        /// <see cref="ExtractBusStopBlocks"/> はこの判定が false の摘要から一切抽出しない。
        /// </para>
        /// <para>
        /// 局所的なマッチでは検出できない（「バス（天神）西口～博多）」は
        /// 「バス（天神）」というブロックとして<b>そのまま成立して見える</b>）が、
        /// 摘要全体を数えれば括弧の過不足として現れる。
        /// </para>
        /// <para>
        /// <b>ただし過不足の検出は万能ではない。</b>バス停名の中で閉じが開きに先行し、
        /// かつ個数が釣り合う入力（「天神）～博多（」→ 摘要「バス（天神）～博多（）」）は
        /// 全体では対応が取れており、区切りとしても<b>「バス（天神）」＋後続の自由文</b>と
        /// 解釈できてしまう（後続の自由文は正当な摘要なので塞げない）。この形は
        /// 入力側（<c>BusStopInputViewModel.CollectSaveWarnings</c> /
        /// <c>LedgerRowEditViewModel.Validate</c>）の警告で気付かせる。
        /// </para>
        /// <para>
        /// 半角括弧は対象外。摘要の書式に使うのは全角括弧だけであり、
        /// 半角括弧はバス停名の一部として自由に使えるため。
        /// </para>
        /// <para>
        /// 汎用/固有の別: 交通系固有（摘要の書式に対する判定）。
        /// </para>
        /// </remarks>
        public static bool HasBalancedFullWidthParentheses(string? text)
        {
            var depth = 0;
            foreach (var c in text ?? string.Empty)
            {
                if (c == FullWidthOpenParenthesis)
                {
                    depth++;
                }
                else if (c == FullWidthCloseParenthesis)
                {
                    depth--;
                    // 閉じが先行した時点で対応は取れない（「）（」を通さない）
                    if (depth < 0) return false;
                }
            }

            return depth == 0;
        }

        /// <summary>
        /// 摘要からバス停名部分を抽出（Issue #1818）
        /// </summary>
        /// <param name="summary">摘要文字列</param>
        /// <param name="busStops">抽出したバス停名（「、」区切りの複数件を含む）。
        /// バスブロックが複数ある場合（Issue #1904 の時系列摘要）は全ブロック分を
        /// 摘要中の出現順（＝時系列順）に「、」で結合して返す。失敗時は空文字</param>
        /// <returns>抽出できた場合 true</returns>
        /// <remarks>
        /// 摘要の直接編集で <c>LedgerDetail.BusStops</c> が取り残される問題（Issue #983）の
        /// 同期処理から使う。汎用/固有の別: 交通系固有。
        /// </remarks>
        public static bool TryExtractBusStops(string? summary, out string busStops)
        {
            var blocks = ExtractBusStopBlocks(summary);
            busStops = string.Join("、", blocks);
            return blocks.Count > 0;
        }

        /// <summary>
        /// 摘要からバス停名をブロック（「バス（…）」）単位で抽出する（Issue #1904）
        /// </summary>
        /// <param name="summary">摘要文字列</param>
        /// <returns>各ブロックのバス停名を摘要中の出現順に並べたリスト。バスブロックが無ければ空</returns>
        /// <remarks>
        /// <para>
        /// バス明細が 1 件の同期処理はブロック区切りを保ったまま先頭ブロックだけを
        /// 書き戻す必要がある（結合テキスト「A～B、C～D」を 1 明細へ書き込むと
        /// <see cref="ParseBusRoute"/> で解析できない値が台帳に残る）ため、
        /// 結合前のブロック列を返す本メソッドを別に置く。
        /// 汎用/固有の別: 交通系固有（バス混在表記）。
        /// </para>
        /// <para>
        /// Issue #1914: 抽出は正規表現ではなく<b>全角括弧の深さを数える走査</b>で行う。
        /// 従来の非貪欲パターン（<c>（(.+?)）</c>、Issue #1905 で 1 段の入れ子まで拡張）は
        /// バス停名に対応の取れない <c>）</c> が含まれると最初の <c>）</c> で打ち切られ、
        /// 「天神）西口～博多」から断片「天神」を返していた。断片は
        /// <c>LedgerDetail.BusStops</c> へ書き戻されるため、6 年保存の台帳から
        /// 実際に乗降した場所が静かに失われる。
        /// </para>
        /// <para>
        /// 深さを数える方式は入れ子の段数に上限が無い（Issue #1905 の往復併記は 1 段だが、
        /// バス停名自体が括弧を含んでも扱える）。一方で<b>対応の取れない括弧は
        /// 原理的に終端を決められない</b>ため、
        /// <see cref="HasBalancedFullWidthParentheses"/> が false の摘要からは
        /// 1 ブロックも抽出しない（部分的に信じて断片を書き戻さない）。
        /// </para>
        /// <para>
        /// ブロックの開始は「ラベル＋全角開き括弧」の連なりで探す。ラベル自体が
        /// 全角括弧を含む設定（「バス（市営）」等、Issue #1818）でも本文の開始位置を
        /// 取り違えないようにするため。
        /// </para>
        /// </remarks>
        internal static List<string> ExtractBusStopBlocks(string? summary)
        {
            var blocks = new List<string>();
            var text = summary ?? string.Empty;

            // 対応が取れていない摘要は、どこがブロックの終端かを決められない
            if (!HasBalancedFullWidthParentheses(text)) return blocks;

            var opener = BusLabel + FullWidthOpenParenthesis;
            var searchFrom = 0;

            while (searchFrom < text.Length)
            {
                var openerIndex = text.IndexOf(opener, searchFrom, StringComparison.Ordinal);
                if (openerIndex < 0) break;

                var contentStart = openerIndex + opener.Length;
                var depth = 1;
                var index = contentStart;

                for (; index < text.Length; index++)
                {
                    if (text[index] == FullWidthOpenParenthesis)
                    {
                        depth++;
                    }
                    else if (text[index] == FullWidthCloseParenthesis)
                    {
                        depth--;
                        if (depth == 0) break;
                    }
                }

                // 対応検証済みのため到達しないが、断片を返さない側へ倒す
                if (depth != 0) break;

                var content = text.Substring(contentStart, index - contentStart);
                searchFrom = index + 1;

                // 中身が空のブロック（「バス（）」）はバス停名として扱わない。
                // 生成側（FormatBusSummary）は未入力でも BusPlaceholder を入れるため
                // 本来は生成され得ず、摘要の手編集でのみ現れる形である。
                // ブロックとして数えると SyncBusStopsFromSummary が
                // LedgerDetail.BusStops を空文字で上書きし、6 年保存の台帳から
                // 実際に乗降した場所が静かに失われる（従来の正規表現は本文を
                // 1 文字以上要求していたため、この形には一致しなかった）。
                if (string.IsNullOrWhiteSpace(content)) continue;

                blocks.Add(content);
            }

            return blocks;
        }

        /// <summary>
        /// 摘要にバス利用が含まれるかを判定（Issue #1818）
        /// </summary>
        /// <remarks>
        /// バス停入力ダイアログの起動判定（<c>MainViewModel</c>）から使う。
        /// 汎用/固有の別: 交通系固有。
        /// </remarks>
        public static bool ContainsBusLabel(string? summary)
            => summary?.Contains(BusLabel) == true;

        /// <summary>
        /// 摘要にバス停名未入力のプレースホルダが残っているかを判定（Issue #1818）
        /// </summary>
        /// <remarks>
        /// バス停名未入力警告の集計（<c>WarningService</c> / <c>IncompleteBusStopViewModel</c>）と
        /// 入力後の一覧更新判定（<c>IncompleteBusStopDialog</c>）から使う。
        /// 汎用/固有の別: 交通系固有。
        /// </remarks>
        public static bool HasIncompleteBusStop(string? summary)
            => summary?.Contains(BusPlaceholder) == true;

        /// <summary>
        /// バス停名がプレースホルダ（未入力）そのものかを判定（Issue #1818）
        /// </summary>
        /// <remarks>
        /// <see cref="HasIncompleteBusStop"/> が摘要に対する部分一致であるのに対し、
        /// 本メソッドは <c>LedgerDetail.BusStops</c> 単体に対する完全一致。
        /// 汎用/固有の別: 交通系固有。
        /// </remarks>
        public static bool IsBusStopPlaceholder(string? busStops)
            => busStops == BusPlaceholder;

        /// <summary>
        /// TransferStationGroups を List&lt;List&lt;string&gt;&gt; から List&lt;HashSet&lt;string&gt;&gt; に変換
        /// </summary>
        /// <remarks>
        /// Issue #1905: 名前を共有するグループどうしは 1 つに併合し、同一視を
        /// 真の同値関係（反射・対称・<b>推移</b>律を満たす）にする。
        ///
        /// 併合しないと [A, B] と [B, C] が登録されたとき A ≡ B、B ≡ C なのに
        /// A ≢ C という非推移的な判定になり、<see cref="CanonicalStation"/> による
        /// 正規化（<see cref="GetRemainingRoutes"/> のキー突合が依存する）が成立しない。
        /// 既定のグループ（天神/西鉄福岡(天神)、千早/西鉄千早）は互いに素なので挙動は変わらない。
        ///
        /// 併合が要るのは、本 Issue で管理者が画面からグループを登録できるようになり、
        /// 「天神日銀前と天神中央郵便局前」「天神中央郵便局前と天神北」のように
        /// 重なるグループが実際に作られ得るため。
        /// </remarks>
        private static List<HashSet<string>> BuildTransferStationGroups(OrganizationOptions options)
        {
            var merged = new List<HashSet<string>>();

            foreach (var group in options.SummaryRules.TransferStationGroups)
            {
                var names = new HashSet<string>(group.Where(n => !string.IsNullOrWhiteSpace(n)));
                if (names.Count == 0)
                {
                    continue;
                }

                // 既存グループのうち 1 つでも名前を共有するものをすべて吸収する
                var overlapping = merged.Where(m => m.Overlaps(names)).ToList();
                foreach (var m in overlapping)
                {
                    names.UnionWith(m);
                    merged.Remove(m);
                }

                merged.Add(names);
            }

            return merged;
        }

        /// <summary>
        /// 駅名・バス停名を同一視グループの代表名へ正規化する（Issue #1905）
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="AreTransferStations"/> は「2 つが同一か」しか答えられないため、
        /// 名前を辞書のキーにしている処理（<see cref="GetRemainingRoutes"/> の往復消費枠）では使えない。
        /// 正規化を挟むと <c>CanonicalStation(a) == CanonicalStation(b)</c> が
        /// <c>AreTransferStations(a, b)</c> と等価になり、辞書のキーとして扱えるようになる。
        /// </para>
        /// <para>
        /// 代表名はグループ内で順序が安定するよう序数比較で最小のものを選ぶ。
        /// 代表名は突合にのみ使い、<b>摘要へ出力する名前には使わない</b>
        /// （利用者が実際に乗降した停留所の名前をそのまま表示するため）。
        /// </para>
        /// </remarks>
        private static string CanonicalStation(string station)
        {
            foreach (var group in _transferStationGroups)
            {
                if (group.Contains(station))
                {
                    // .NET Framework 4.8 には Enumerable.Min(IComparer) のオーバーロードが無い
                    return group.OrderBy(n => n, StringComparer.Ordinal).First();
                }
            }

            return station;
        }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="departmentType">部署種別（チャージ摘要の切替に使用）</param>
        public SummaryGenerator(DepartmentType departmentType = DepartmentType.MayorOffice)
        {
            _departmentType = departmentType;
        }

        /// <summary>
        /// DI用コンストラクタ。組織固有設定と部署種別をコンストラクタで注入します。
        /// </summary>
        /// <param name="departmentType">部署種別（チャージ摘要の切替に使用）</param>
        /// <param name="options">組織固有設定</param>
        public SummaryGenerator(DepartmentType departmentType, OrganizationOptions options)
            : this(departmentType)
        {
            // DI経由で生成された場合、静的フィールドも設定する
            // （静的メソッドが参照するため、DI経由の初期化でも静的状態を更新）
            Configure(options);
        }

        /// <summary>
        /// 金額が負でチャージでもポイント還元フラグでもないレコードを暗黙のポイント還元として判定
        /// </summary>
        /// <remarks>
        /// Issue #942: ICカードの生データでは、ポイント還元が乗車駅ありの負金額レコードとして
        /// 記録されることがある（IsPointRedemption=falseのまま）。
        /// 金額が負＝カードに入金されている＝チャージまたはポイント還元であるため、
        /// IsCharge=falseかつIsPointRedemption=falseで金額が負のレコードはポイント還元とみなす。
        /// </remarks>
        internal static bool IsImplicitPointRedemption(LedgerDetail detail)
        {
            return detail.Amount.HasValue
                && detail.Amount.Value < 0
                && !detail.IsCharge
                && !detail.IsPointRedemption;
        }

        /// <summary>
        /// 2つの駅・バス停が同一とみなせるかどうかを判定
        /// </summary>
        /// <param name="station1">駅名・バス停名1</param>
        /// <param name="station2">駅名・バス停名2</param>
        /// <returns>同一（完全一致または同一グループ内）の場合true</returns>
        /// <remarks>
        /// 判定は名前の文字列比較のみで、鉄道／バスの区別を持たない。したがって
        /// 同一視グループ（<c>SummaryRules.TransferStationGroups</c>）は
        /// <b>バス停にもそのまま適用される</b>（Issue #1905。道路を挟んで向かい合う
        /// 「天神日銀前」と「天神中央郵便局前」のような実質同一の停留所を登録する用途）。
        ///
        /// <see cref="BuildTransferStationGroups"/> がグループを同値類へ併合済みのため、
        /// 本メソッドは <c>CanonicalStation(a) == CanonicalStation(b)</c> と等価。
        /// </remarks>
        private static bool AreTransferStations(string station1, string station2)
        {
            // 完全一致
            if (station1 == station2)
            {
                return true;
            }

            // 同一グループ内かチェック
            foreach (var group in _transferStationGroups)
            {
                if (group.Contains(station1) && group.Contains(station2))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 利用履歴詳細から日付ごとの摘要リストを生成します。
        /// </summary>
        /// <param name="details">利用履歴詳細のリスト（ICカードから取得した新しい順）</param>
        /// <returns>日別摘要のリスト（古い順）</returns>
        /// <remarks>
        /// <para>このメソッドは以下の処理を行います：</para>
        /// <list type="bullet">
        /// <item><description>日付ごとにグループ化</description></item>
        /// <item><description>利用（鉄道・バス）とチャージを別行として分離</description></item>
        /// <item><description>古い順（時系列順）にソート</description></item>
        /// </list>
        /// <para>
        /// ICカードの履歴は新しい順で格納されているため、
        /// インデックスが大きいほど古いデータとして処理します。
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// var generator = new SummaryGenerator(DepartmentType.MayorOffice);
        /// var summaries = generator.GenerateByDate(usageDetails);
        /// foreach (var summary in summaries)
        /// {
        ///     Console.WriteLine($"{summary.Date:yyyy/MM/dd}: {summary.Summary}");
        /// }
        /// </code>
        /// </example>
        /// <seealso cref="Generate"/>
        public List<DailySummary> GenerateByDate(IEnumerable<LedgerDetail> details)
        {
            var detailList = details.ToList();

            if (detailList.Count == 0)
            {
                return new List<DailySummary>();
            }

            var results = new List<DailySummary>();

            // 入力順にインデックスを付与（ICカード履歴は新しい順なので、インデックスが大きいほど古い）
            var indexedDetails = detailList
                .Select((d, index) => (Detail: d, Index: index))
                .Where(x => x.Detail.UseDate.HasValue)
                .ToList();

            // 日付でグループ化（古い順にソート）
            var groupedByDate = indexedDetails
                .GroupBy(x => x.Detail.UseDate!.Value.Date)
                .OrderBy(g => g.Key);

            foreach (var dateGroup in groupedByDate)
            {
                var date = dateGroup.Key;
                var dayItems = dateGroup.ToList();

                // ポイント還元を先に分離（ポイント還元は個別DailySummaryだがチャージ境界にはしない）
                // Issue #942: 明示的フラグ + 暗黙のポイント還元（金額が負でチャージでもない）を両方分離
                var pointRedemptionItems = dayItems
                    .Where(x => x.Detail.IsPointRedemption || IsImplicitPointRedemption(x.Detail)).ToList();

                // 残りの項目（利用+チャージ）を時系列順（古い順＝インデックス降順）にソート
                var usageAndChargeItems = dayItems
                    .Where(x => !x.Detail.IsPointRedemption && !IsImplicitPointRedemption(x.Detail))
                    .OrderByDescending(x => x.Index)
                    .ToList();

                // 出力候補を作成（最古のインデックスと共に）
                var summariesToAdd = new List<(int OldestIndex, DailySummary Summary)>();

                // チャージ境界で利用グループを分割しながら摘要を生成
                var currentUsageGroup = new List<(LedgerDetail Detail, int Index)>();

                foreach (var item in usageAndChargeItems)
                {
                    if (item.Detail.IsCharge)
                    {
                        // 溜まった利用グループを先に出力
                        if (currentUsageGroup.Count > 0)
                        {
                            var usageDetails = currentUsageGroup.Select(x => x.Detail).ToList();
                            var usageSummary = GenerateUsageSummary(usageDetails);
                            if (!string.IsNullOrEmpty(usageSummary))
                            {
                                var oldestIndex = currentUsageGroup.Max(x => x.Index);
                                summariesToAdd.Add((oldestIndex, new DailySummary
                                {
                                    Date = date,
                                    Summary = usageSummary,
                                    IsCharge = false,
                                    IsPointRedemption = false
                                }));
                            }
                            currentUsageGroup.Clear();
                        }

                        // チャージを出力
                        summariesToAdd.Add((item.Index, new DailySummary
                        {
                            Date = date,
                            Summary = GetChargeSummary(_departmentType),
                            IsCharge = true,
                            IsPointRedemption = false
                        }));
                    }
                    else
                    {
                        // 利用: グループに追加
                        currentUsageGroup.Add(item);
                    }
                }

                // 残りの利用グループを出力
                if (currentUsageGroup.Count > 0)
                {
                    var usageDetails = currentUsageGroup.Select(x => x.Detail).ToList();
                    var usageSummary = GenerateUsageSummary(usageDetails);
                    if (!string.IsNullOrEmpty(usageSummary))
                    {
                        var oldestIndex = currentUsageGroup.Max(x => x.Index);
                        summariesToAdd.Add((oldestIndex, new DailySummary
                        {
                            Date = date,
                            Summary = usageSummary,
                            IsCharge = false,
                            IsPointRedemption = false
                        }));
                    }
                }

                // ポイント還元がある場合はポイント還元摘要を追加
                if (pointRedemptionItems.Count > 0)
                {
                    var oldestIndex = pointRedemptionItems.Max(x => x.Index);
                    summariesToAdd.Add((oldestIndex, new DailySummary
                    {
                        Date = date,
                        Summary = GetPointRedemptionSummary(),
                        IsCharge = false,
                        IsPointRedemption = true
                    }));
                }

                // 古い順（インデックス降順）にソートして追加
                foreach (var item in summariesToAdd.OrderByDescending(x => x.OldestIndex))
                {
                    results.Add(item.Summary);
                }
            }

            return results;
        }

        /// <summary>
        /// 利用（鉄道・バス）の摘要を生成
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #1904: 従来は鉄道→バスの固定順で結合していたため、バスが先の時系列でも
        /// 摘要は鉄道が先頭になっていた。時系列順（利用順）に同一モードの連続区間（run）
        /// 単位でブロック化し、「バス（X～Y）、鉄道（A駅～B駅）」のように利用順で結合する。
        /// </para>
        /// <para>
        /// 往復・乗継統合は run 内でのみ働く（間にバスを挟む鉄道往復は run が分かれるため
        /// 「往復」表記にならない。時系列忠実性を優先する設計判断）。
        /// 明示グループ（GroupId）は <see cref="CoalesceExplicitGroups"/> で 1 単位として扱う。
        /// </para>
        /// </remarks>
        private string GenerateUsageSummary(List<LedgerDetail> usageDetails)
        {
            var sortedDetails = SortChronologically(usageDetails);
            var runs = SplitIntoModeRuns(CoalesceExplicitGroups(sortedDetails));

            var summaryParts = new List<string>();

            foreach (var run in runs)
            {
                if (run[0].IsBus)
                {
                    summaryParts.Add(FormatBusSummary(GenerateBusSummary(run)));
                }
                else
                {
                    var railwaySummary = GenerateRailwaySummary(run);
                    if (!string.IsNullOrEmpty(railwaySummary))
                    {
                        summaryParts.Add($"{_options.SummaryText.RailwayLabel}（{railwaySummary}）");
                    }
                }
            }

            return string.Join(RouteSeparator, summaryParts);
        }

        /// <summary>
        /// 明示グループ（GroupId）の明細を、グループ内で時系列最古の明細の位置へ隣接配置する（Issue #1904）
        /// </summary>
        /// <param name="sortedDetails">時系列順（古い順）にソート済みの明細リスト</param>
        /// <remarks>
        /// 時系列上非連続なグループ（間に別モードの利用を挟む）でも、利用者が「1つの利用」と
        /// 指定した明細群（Issue #484 / #633 / #1816）が run 分割で分かれないようにする。
        /// モードが混在するグループはモード別に分け、各モードの最古位置へ配置する
        /// （鉄道とバスの摘要生成が別系統のため）。
        /// 汎用/固有の別: 交通系固有（鉄道・バス混在の摘要組み立て）。
        /// </remarks>
        private static List<LedgerDetail> CoalesceExplicitGroups(List<LedgerDetail> sortedDetails)
        {
            if (!sortedDetails.Any(d => d.GroupId.HasValue))
            {
                return sortedDetails;
            }

            var result = new List<LedgerDetail>(sortedDetails.Count);
            var emittedGroups = new HashSet<(int GroupId, bool IsBus)>();

            foreach (var detail in sortedDetails)
            {
                if (!detail.GroupId.HasValue)
                {
                    result.Add(detail);
                    continue;
                }

                var key = (GroupId: detail.GroupId.Value, detail.IsBus);
                if (!emittedGroups.Add(key))
                {
                    // 既にグループ最古の位置でまとめて追加済み
                    continue;
                }

                result.AddRange(sortedDetails.Where(d =>
                    d.GroupId == key.GroupId && d.IsBus == key.IsBus));
            }

            return result;
        }

        /// <summary>
        /// 隣接する同一モード（<see cref="LedgerDetail.IsBus"/>）の明細を連続区間（run）へ分割する（Issue #1904）
        /// </summary>
        /// <param name="details">時系列順に並んだ明細リスト</param>
        /// <returns>時系列順の run のリスト。各 run は同一モードの明細のみを含む</returns>
        /// <remarks>汎用/固有の別: 交通系固有（鉄道・バス混在の摘要組み立て）。</remarks>
        private static List<List<LedgerDetail>> SplitIntoModeRuns(List<LedgerDetail> details)
        {
            var runs = new List<List<LedgerDetail>>();

            foreach (var detail in details)
            {
                var lastRun = runs.Count > 0 ? runs[runs.Count - 1] : null;
                if (lastRun == null || lastRun[0].IsBus != detail.IsBus)
                {
                    lastRun = new List<LedgerDetail>();
                    runs.Add(lastRun);
                }
                lastRun.Add(detail);
            }

            return runs;
        }

        /// <summary>
        /// 利用履歴詳細から摘要文字列を生成（従来メソッド・互換性のため維持）
        /// </summary>
        /// <param name="details">利用履歴詳細のリスト（ICカードから取得した新しい順）</param>
        /// <returns>摘要文字列</returns>
        /// <remarks>
        /// <para>
        /// ICカード履歴は新しい順で格納されているため、内部で古い順（時系列順）に
        /// 変換してから処理します。これにより、往復検出時に出発点が正しく
        /// 摘要の先頭に表示されます。
        /// </para>
        /// <para>
        /// 例：薬院→博多→薬院の往復移動は「薬院～博多 往復」と表示されます。
        /// </para>
        /// </remarks>
        /// <seealso cref="GenerateByDate"/>
        public virtual string Generate(IEnumerable<LedgerDetail> details)
        {
            // ICカード履歴は新しい順で格納されているため、
            // 逆順にして古い順（時系列順）に変換する (Issue #336)
            var detailList = details.Reverse().ToList();

            if (detailList.Count == 0)
            {
                return string.Empty;
            }

            // チャージのみの場合
            if (detailList.All(d => d.IsCharge))
            {
                return GetChargeSummary(_departmentType);
            }

            // ポイント還元のみの場合
            // Issue #942: 暗黙のポイント還元（金額が負でチャージでもない）も含めて判定
            if (detailList.All(d => d.IsPointRedemption || IsImplicitPointRedemption(d)))
            {
                return _options.SummaryText.PointRedemption;
            }

            // Issue #1904: 鉄道/バスの二分割は GenerateUsageSummary に一本化
            //（固定順の結合をやめ、時系列順の run 単位で結合する）
            var usageDetails = detailList
                .Where(d => !d.IsCharge && !d.IsPointRedemption && !IsImplicitPointRedemption(d))
                .ToList();

            return GenerateUsageSummary(usageDetails);
        }

        /// <summary>
        /// 利用履歴をSequenceNumber/UseDate/Balanceで時系列順（古い順）にソート
        /// </summary>
        /// <remarks>
        /// <para>
        /// Issue #548, #880: FeliCa互換でrowid（=SequenceNumber）が小さいほど新しい（後に利用した）。
        /// DESCで大きいrowid（古い）を先にして時系列順に。
        /// SequenceNumberが0（未設定）の場合はBalance降順を使用。
        /// </para>
        /// <para>
        /// Issue #1904（コードレビュー指摘）: 第一キーは rowid ではなく UseDate。
        /// 単一バッチ（1回の返却で読み取った履歴）では日付昇順と rowid 降順が一致するため
        /// 等価だが、**統合済み台帳（Issue #837 / #1458）では別バッチ由来の rowid が日付と
        /// 無関係に交錯し得る**。日付をまたぐ統合行で rowid を第一キーにすると、摘要の
        /// ブロック順・バス停対応付けが日付と矛盾する。同一日付内は従来どおり rowid 降順が
        /// 第一（同日の時刻はすべて 00:00 で保存され、残高チェーンは循環し得るため。
        /// business-logic.md「同一日内の順序は id では決まらない」の裏面として、同日内の
        /// タイブレークは rowid が最も強い）。
        /// </para>
        /// </remarks>
        internal static List<LedgerDetail> SortChronologically(List<LedgerDetail> trips)
        {
            return trips
                .OrderBy(t => t.UseDate ?? DateTime.MaxValue)
                .ThenByDescending(t => t.SequenceNumber > 0 ? t.SequenceNumber : int.MinValue)
                .ThenByDescending(t => t.Balance ?? 0)
                .ToList();
        }

        /// <summary>
        /// 摘要を再生成したときにバス停名が現れる順序で、バス明細を返す（Issue #1904）
        /// </summary>
        /// <param name="details">台帳の明細リスト（順序は問わない）</param>
        /// <returns>バス明細のみを、摘要中のバス停名の出現順に並べたリスト</returns>
        /// <remarks>
        /// <para>
        /// 摘要からバス停名を抽出して明細へ書き戻す同期処理
        /// （<c>LedgerMergeService.SyncBusStopsFromSummary</c> /
        /// <c>LedgerRowEditViewModel.SyncBusStopsFromSummaryAsync</c>）は、抽出した
        /// バス停名（摘要中の出現順）と明細を位置で対応付ける。その対応が成立するのは
        /// **明細の並びが生成側の出力順と一致するときだけ**なので、並び順の定義を
        /// 消費側に書き写さず、生成パイプラインと同じ手順
        /// （<see cref="SortChronologically"/> → <see cref="CoalesceExplicitGroups"/> →
        /// <see cref="SplitIntoModeRuns"/> → run 内の GroupId 優先順）を本メソッドに集約する。
        /// </para>
        /// <para>
        /// GroupId を含む run では <see cref="GenerateBusSummaryWithGroupId"/> と同じく
        /// 「グループ（最古 UseDate 順、各グループ内は時系列）→ 未グループ」の順になる。
        /// 往復・乗継統合（<see cref="BuildRouteSummary"/>）が起きた場合は摘要側の
        /// バス停数が明細数より少なくなるが、同期側の件数一致ガードが書き戻しを
        /// 抑止するため、本メソッドは統合前の順序を返せば足りる。
        /// 汎用/固有の別: 交通系固有（バス混在表記）。
        /// </para>
        /// </remarks>
        internal static List<LedgerDetail> GetBusStopEmissionOrder(IEnumerable<LedgerDetail> details)
        {
            var usageDetails = details
                .Where(d => !d.IsCharge && !d.IsPointRedemption && !IsImplicitPointRedemption(d))
                .ToList();

            var runs = SplitIntoModeRuns(CoalesceExplicitGroups(SortChronologically(usageDetails)));

            var result = new List<LedgerDetail>();
            foreach (var run in runs)
            {
                if (!run[0].IsBus)
                {
                    continue;
                }

                var sortedRun = SortChronologically(run);
                if (sortedRun.Any(t => t.GroupId.HasValue))
                {
                    // GenerateBusSummaryWithGroupId と同じ出力順
                    var groupedTrips = sortedRun
                        .Where(t => t.GroupId.HasValue)
                        .GroupBy(t => t.GroupId!.Value)
                        .OrderBy(g => g.Min(t => t.UseDate ?? DateTime.MaxValue));
                    foreach (var group in groupedTrips)
                    {
                        result.AddRange(SortChronologically(group.ToList()));
                    }

                    result.AddRange(sortedRun.Where(t => !t.GroupId.HasValue));
                }
                else
                {
                    result.AddRange(sortedRun);
                }
            }

            return result;
        }

        /// <summary>
        /// 経路リストに対して乗り継ぎ統合→往復検出→文字列整形の共通パイプラインを実行
        /// </summary>
        /// <param name="routes">経路の(Entry, Exit)タプルリスト（時系列順）</param>
        /// <returns>「A～B、C～D 往復」形式の摘要文字列。空リストの場合はstring.Empty</returns>
        /// <remarks>
        /// <para>
        /// Issue #1916: 乗継統合は貪欲（前から順に繋げられるだけ繋げる）に行われるため、
        /// 往復の<b>往路</b>が直前チェーンの延長に消費されて往復が検出できなくなる形がある
        /// （薬院→天神／天神→博多／博多→天神 で「薬院～博多、博多～天神」となり、
        /// 通しでは乗っていない「薬院～博多」が 6 年保存の台帳へ入っていた）。
        /// </para>
        /// <para>
        /// 単純な先読みガード（「次の経路がその次と往復ペアを成すなら延長しない」）は
        /// TC038 を退行させる。TC038 では往路チェーン（博多→天神→西鉄二日市）の
        /// <b>統合が完成してから</b>復路が逆走として畳まれる必要があり、
        /// 往路の部分区間（西鉄福岡(天神)→西鉄二日市）が局所的に往復ペアに見えても
        /// 統合を止めてはならないためである。局所的な形では優先順位を決められない。
        /// </para>
        /// <para>
        /// そこで統合結果を 1 つに決め打たず、「延長を抑止した候補」を並べて往復検出まで通し、
        /// <b>往復として説明できた元区間（明細）の件数</b>で選ぶ。
        /// #1916 の事例では抑止した候補だけが往復を検出でき（2 区間）、既定解を上書きする。
        /// 同点時はブロック数が少ない側を採るが、<b>「ブロック数が少ない側＝貪欲な既定解」ではない</b>
        /// — 抑止した候補が既定解よりブロック数を減らすこともある。同点はあくまで
        /// 「同じ回で見つかった候補どうしの優劣付け」にのみ使い、既定解を置き換える条件は
        /// <b>カバレッジの厳密な増加</b>である（下記）。
        /// なお TC038 型（往路チェーンの統合が完成してから復路が畳まれる形）は
        /// <see cref="HasUnexplainedThroughRoute"/> が false になるため候補探索へ入らず、
        /// 比較そのものに到達しない（TC038 を守っているのはゲートであって選択指標ではない）。
        /// </para>
        /// <para>
        /// <b>抑止点は 1 つに限らない（コードレビューで検出）。</b> 同じ形が 1 日に 2 回起きると
        /// （薬院→天神／天神→博多／博多→天神／天神→大橋／大橋→西鉄二日市／西鉄二日市→大橋）、
        /// 1 箇所の抑止では片方しか救えず、ブロック数の同点処理がもう片方を切り捨てて
        /// <b>本 Issue が消そうとしている「薬院～博多」がそのまま残っていた</b>。
        /// 抑止点を 1 つずつ増やし、<b>カバレッジが厳密に増える間だけ</b>採用する
        /// （増えなくなったら打ち切る）。これで既定解を置き換える条件が
        /// 「往復としてより多く説明できた」ことだけになり、同点処理は判断の主役から外れる。
        /// </para>
        /// <para>
        /// <b>4 つの判断点（カバレッジ指標・同点処理・往復のバランス判定・抑止点の累積）は、
        /// いずれも「経路が連結していない日」でだけ結果を変える。</b>
        /// 4 駅・3〜5 区間の全 271,296 通りを総当たりで比較した実測で、
        /// 降車地と次の乗車地が一致する（連結する）列では 1 通りも差が出ない。
        /// 実データでは鉄道区間の間に徒歩やバスが挟まるためこの形は現実に起こるが、
        /// 個別の判断点を単体テストで固定するには合成的な入力が要る
        /// （<c>Issue1916_*</c> の後半 3 件がこれにあたる）。
        /// なお「生の経路に逆方向のペアが無ければ探索は空振りする」という足切りは
        /// <b>不健全なので置かない</b> — 乗継統合は元の経路に無い区間（A→B→C から A～C）を
        /// 作るため、統合後に初めて逆方向のペアが成立する日がある（実測で 72 通り）。
        /// </para>
        /// <para>
        /// <b>候補が往復を捏造していないことを確かめる（コードレビューで検出）。</b>
        /// 薬院→天神／天神→博多／博多→薬院／薬院→大橋（一方通行のループ＋後続区間）では、
        /// 1 区間の往路（薬院→天神）と<b>2 区間へ統合された復路</b>（天神→博多→薬院）が
        /// ペアになり「薬院～天神 往復」という<b>起きていない往復</b>が作られて、
        /// 実際に用務のあった博多が 6 年保存の台帳から消えていた。本物の往復は行きと帰りで
        /// 同じ場所を通るため<b>両側の区間数が一致する</b>。区間数の非対称は
        /// 「統合が隠した場所を往復と言い張っている」合図なので、
        /// <see cref="HasOnlyBalancedRoundTrips"/> を満たす候補だけを採用対象にする
        /// （満たさなければ既定解のまま＝従来挙動）。
        /// </para>
        /// </remarks>
        private string BuildRouteSummary(List<(string Entry, string Exit)> routes)
        {
            if (routes.Count == 0)
            {
                return string.Empty;
            }

            // Issue #878: 乗り継ぎ統合を往復判定より先に行う
            // Issue #974: EnableTransferConsolidation で ON/OFF 可能
            if (!_options.SummaryRules.EnableTransferConsolidation)
            {
                var withoutConsolidation = routes
                    .Select(r => new ConsolidatedRoute(r.Entry, r.Exit, 1))
                    .ToList();
                return FormatRouteBlocks(EvaluateCandidate(withoutConsolidation));
            }

            var suppressedIndices = new HashSet<int>();
            var best = EvaluateCandidate(ConsolidateRoutes(routes, suppressedIndices));

            // Issue #1916: 延長を抑止した候補を試し、往復カバレッジで選び直す。
            // 往復検出が無効なら候補を比べる意味がない（すべてカバレッジ 0 になる）。
            if (_options.SummaryRules.EnableRoundTripDetection
                && HasUnexplainedThroughRoute(best))
            {
                // 抑止点を 1 つずつ増やす。1 周で何も改善しなければ打ち切るため、
                // 反復回数は経路数を超えない。
                for (int round = 1; round < routes.Count; round++)
                {
                    var bestIndex = -1;
                    RouteCandidate bestCandidate = default;

                    for (int suppressAt = 1; suppressAt < routes.Count; suppressAt++)
                    {
                        if (suppressedIndices.Contains(suppressAt))
                        {
                            continue;
                        }

                        suppressedIndices.Add(suppressAt);
                        var candidate = EvaluateCandidate(
                            ConsolidateRoutes(routes, suppressedIndices));
                        suppressedIndices.Remove(suppressAt);

                        // 採用の条件はカバレッジの厳密な増加。同点は「同じ回で見つかった
                        // 候補どうし」の優劣付けにのみ使う（IsBetterCandidate）。
                        if (candidate.RoundTripLegCoverage <= best.RoundTripLegCoverage
                            || !HasOnlyBalancedRoundTrips(candidate))
                        {
                            continue;
                        }

                        if (bestIndex < 0 || IsBetterCandidate(candidate, bestCandidate))
                        {
                            bestIndex = suppressAt;
                            bestCandidate = candidate;
                        }
                    }

                    if (bestIndex < 0)
                    {
                        break;
                    }

                    suppressedIndices.Add(bestIndex);
                    best = bestCandidate;
                }
            }

            return FormatRouteBlocks(best);
        }

        /// <summary>
        /// 候補が検出した往復がすべて「行きと帰りで区間数が一致する」か（Issue #1916）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 本物の往復は行きと帰りで同じ場所を通るため、統合後の両側が束ねている
        /// 元区間の件数（<see cref="ConsolidatedRoute.LegCount"/>）は一致する。
        /// 区間数が非対称な「往復」は、片側の統合が隠した場所をもう片側が通っていないことを意味し、
        /// <b>起きていない往復を主張しつつ実際に用務のあった場所を台帳から消す</b>。
        /// </para>
        /// <para>
        /// 実例（コードレビューで検出）: 薬院→天神／天神→博多／博多→薬院／薬院→大橋 では、
        /// 1 区間の往路（薬院→天神）と 2 区間へ統合された復路（天神→博多→薬院）がペアになり
        /// 「鉄道（薬院～天神 往復、薬院～大橋）」となって博多が消えていた。
        /// </para>
        /// <para>
        /// 判定は<b>候補の採用可否にだけ</b>使う。既定解（貪欲統合）の往復は従来どおり扱う
        /// — ここで既定解まで弾くと、本 Issue と無関係な既存の摘要が変わってしまう。
        /// </para>
        /// </remarks>
        private static bool HasOnlyBalancedRoundTrips(RouteCandidate candidate)
            => candidate.RoundTrips.All(rt =>
                candidate.ConsolidatedRoutes[rt.ForwardIndex].LegCount
                    == candidate.ConsolidatedRoutes[rt.ReverseIndex].LegCount);

        /// <summary>
        /// 統合候補 1 つに往復検出を通した結果（Issue #1916）
        /// </summary>
        private readonly struct RouteCandidate
        {
            public RouteCandidate(
                List<ConsolidatedRoute> consolidatedRoutes,
                List<RoundTrip> roundTrips,
                List<(int Index, string Entry, string Exit)> remainingRoutes,
                int roundTripLegCoverage)
            {
                ConsolidatedRoutes = consolidatedRoutes;
                RoundTrips = roundTrips;
                RemainingRoutes = remainingRoutes;
                RoundTripLegCoverage = roundTripLegCoverage;
            }

            /// <summary>この候補の乗継統合結果（時系列順）</summary>
            public List<ConsolidatedRoute> ConsolidatedRoutes { get; }

            /// <summary>検出できた往復</summary>
            public List<RoundTrip> RoundTrips { get; }

            /// <summary>往復に消費されなかった経路（統合後リスト内の添字付き）</summary>
            public List<(int Index, string Entry, string Exit)> RemainingRoutes { get; }

            /// <summary>往復として説明できた<b>元の</b>区間（明細）の件数</summary>
            public int RoundTripLegCoverage { get; }

            /// <summary>摘要に並ぶブロック数（往復ブロック＋余りブロック）</summary>
            public int BlockCount => RoundTrips.Count + RemainingRoutes.Count;
        }

        /// <summary>
        /// 統合候補に往復検出を通し、選択に使う指標を算出する（Issue #1916）
        /// </summary>
        private RouteCandidate EvaluateCandidate(List<ConsolidatedRoute> consolidatedRoutes)
        {
            var asPairs = consolidatedRoutes
                .Select(r => (Entry: r.Start, Exit: r.End)).ToList();

            // Issue #974: EnableRoundTripDetection で ON/OFF 可能
            if (_options.SummaryRules.EnableRoundTripDetection && asPairs.Count >= 2)
            {
                var roundTrips = DetectRoundTrips(asPairs);
                if (roundTrips.Count > 0)
                {
                    var remaining = GetRemainingRoutes(asPairs, roundTrips);

                    // カバレッジは統合後の本数ではなく元の区間数で数える。
                    // 本数で数えると「往路 2 区間＋復路 2 区間が 1 往復に畳まれた解」より
                    // 「往復 2 組へ分解した解」のほうが高く出てしまうため。
                    // 注意: この数え方は TC038 が守っているわけではない。TC038 は
                    // HasUnexplainedThroughRoute が false で候補探索へ入らないため、
                    // 本式を roundTrips.Count * 2 へ変えても既存テストは全件緑になる
                    // （＝この指標を固定する回帰テストはまだ無い）。
                    var coverage = roundTrips.Sum(rt =>
                        consolidatedRoutes[rt.ForwardIndex].LegCount
                        + consolidatedRoutes[rt.ReverseIndex].LegCount);

                    return new RouteCandidate(consolidatedRoutes, roundTrips, remaining, coverage);
                }
            }

            var allAsRemaining = consolidatedRoutes
                .Select((r, index) => (Index: index, Entry: r.Start, Exit: r.End))
                .ToList();
            return new RouteCandidate(
                consolidatedRoutes, new List<RoundTrip>(), allAsRemaining, 0);
        }

        /// <summary>
        /// 「乗継統合が作った通し区間のうち、往復として説明できていないもの」があるか（Issue #1916）
        /// </summary>
        /// <remarks>
        /// <para>
        /// #1916 が消したい欠陥は「<b>通しでは乗っていない区間</b>が摘要に現れ、
        /// 6 年保存の台帳へ入る」ことである。したがって別解を探す価値があるのは、
        /// 既定解に「複数の明細を束ねた区間（<see cref="ConsolidatedRoute.LegCount"/> ≥ 2）」が
        /// あり、かつそれが往復として説明されていないときに限る。
        /// </para>
        /// <para>
        /// このゲートが無いと、統合が一切起きていない既定解まで作り替えてしまう。
        /// 実際 TC014 の 12/8（天神→姪浜→西新→天神 の 3 区間循環）は #878 の設計により
        /// 各区間が個別表示されており通し区間を捏造していないのに、
        /// 延長を抑止した候補が「天神～姪浜 往復」を作って既定解を上書きしていた。
        /// </para>
        /// </remarks>
        private static bool HasUnexplainedThroughRoute(RouteCandidate candidate)
            => candidate.RemainingRoutes.Any(
                r => candidate.ConsolidatedRoutes[r.Index].LegCount >= 2);

        /// <summary>
        /// 統合候補どうしを比較する（Issue #1916）
        /// </summary>
        /// <remarks>
        /// 第 1 指標は「往復として説明できた元区間の件数」。実際の移動をより多く
        /// 往復として言い当てられる解を選ぶ。同点なら統合が進んでいる（ブロック数が少ない）側を採る。
        /// それも同点なら先に評価した側を維持する — 最初の比較では既定解が残るが、
        /// 2 回目以降は候補どうしの比較になるため「先に評価した側＝既定解」とは限らない。
        /// </remarks>
        private static bool IsBetterCandidate(RouteCandidate candidate, RouteCandidate current)
        {
            if (candidate.RoundTripLegCoverage != current.RoundTripLegCoverage)
            {
                return candidate.RoundTripLegCoverage > current.RoundTripLegCoverage;
            }

            return candidate.BlockCount < current.BlockCount;
        }

        /// <summary>
        /// 選ばれた候補を摘要文字列へ整形する（Issue #1916）
        /// </summary>
        /// <remarks>
        /// ブロックは<b>利用順（時系列）</b>に並べる。統合後リストは時系列順なので、
        /// 往復ブロックは往路の添字、余りブロックは自身の添字で並べ替えればよい。
        /// かつては往復ブロックを先頭へ寄せていたが、#1916 のように余りが時系列で
        /// 先に来る形（薬院～天神 → 天神～博多 往復）では移動順と食い違っていた。
        /// 鉄道／バスのブロックを利用順に並べる #1904 と同じ考え方をブロック内にも適用する。
        /// </remarks>
        private string FormatRouteBlocks(RouteCandidate candidate)
        {
            var blocks = candidate.RoundTrips
                .Select(rt => (Order: rt.ForwardIndex, Text: FormatRoundTrip(rt)))
                .Concat(candidate.RemainingRoutes
                    .Select(r => (Order: r.Index, Text: $"{r.Entry}～{r.Exit}")))
                .OrderBy(b => b.Order)
                .Select(b => b.Text);

            return string.Join(RouteSeparator, blocks);
        }

        /// <summary>
        /// 往復 1 組を摘要の表記へ整形する（Issue #1905）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 端点の名前が往路と復路で異なる場合（同一視グループへ登録された別名の
        /// 駅・バス停で乗降した場合）は「往路の名前（復路の名前）」と併記する。
        /// 例: 天神日銀前で乗車し、帰りは道路を挟んだ天神中央郵便局前で降車した往復は
        /// 「天神日銀前（天神中央郵便局前）～下原中央 往復」。
        /// </para>
        /// <para>
        /// 併記するのは、6 年保存される台帳から<b>実際に乗降した場所</b>が失われないようにするため。
        /// 同一視は「往復としてまとめてよい」という判断にだけ使い、
        /// どちらか一方の名前で代表させることはしない。
        /// </para>
        /// <para>
        /// 名前が完全に一致する通常の往復では括弧を付けない（「A～B 往復」のまま）。
        /// </para>
        /// </remarks>
        private string FormatRoundTrip(RoundTrip roundTrip)
        {
            var start = FormatEndpoint(roundTrip.Start, roundTrip.ReturnExit);
            var end = FormatEndpoint(roundTrip.End, roundTrip.ReturnEntry);
            return $"{start}～{end}{_options.SummaryText.RoundTripSuffix}";
        }

        /// <summary>
        /// 往復の端点を「往路の名前（復路の名前）」形式へ整形する（Issue #1905）
        /// </summary>
        private static string FormatEndpoint(string outboundName, string returnName)
            => string.Equals(outboundName, returnName, StringComparison.Ordinal)
                ? outboundName
                : $"{outboundName}（{returnName}）";

        /// <summary>
        /// 検出した往復 1 組（Issue #1905）
        /// </summary>
        /// <remarks>
        /// <see cref="Start"/> / <see cref="End"/> は往路の乗車地・降車地で、
        /// 往復の同一性（<see cref="GetRemainingRoutes"/> の消費枠のキー）はこの 2 つで決まる。
        /// <see cref="ReturnEntry"/> / <see cref="ReturnExit"/> は復路の乗車地・降車地で、
        /// 同一視グループにより <see cref="End"/> / <see cref="Start"/> と<b>同一視されるが
        /// 名前は異なり得る</b>。摘要への併記にのみ使う。
        /// </remarks>
        private readonly struct RoundTrip
        {
            public RoundTrip(
                string start,
                string end,
                string returnEntry,
                string returnExit,
                int forwardIndex,
                int reverseIndex)
            {
                Start = start;
                End = end;
                ReturnEntry = returnEntry;
                ReturnExit = returnExit;
                ForwardIndex = forwardIndex;
                ReverseIndex = reverseIndex;
            }

            /// <summary>往路が統合後リストの何番目か（Issue #1916。並び順とカバレッジ算出に使う）</summary>
            public int ForwardIndex { get; }

            /// <summary>復路が統合後リストの何番目か（Issue #1916）</summary>
            public int ReverseIndex { get; }

            /// <summary>往路の乗車地</summary>
            public string Start { get; }

            /// <summary>往路の降車地（折り返し点）</summary>
            public string End { get; }

            /// <summary>復路の乗車地（<see cref="End"/> と同一視される）</summary>
            public string ReturnEntry { get; }

            /// <summary>復路の降車地（<see cref="Start"/> と同一視される）</summary>
            public string ReturnExit { get; }
        }

        /// <summary>
        /// 鉄道利用の摘要文字列を生成します。
        /// </summary>
        /// <param name="trips">鉄道利用の履歴詳細リスト</param>
        /// <returns>「A駅～B駅」形式の摘要文字列。往復の場合は「A駅～B駅 往復」形式</returns>
        /// <remarks>
        /// <para>アルゴリズム：</para>
        /// <list type="number">
        /// <item><description>GroupIdが設定されている場合、同じGroupIdの経路を1つの乗り継ぎとして統合</description></item>
        /// <item><description>GroupIdが未設定の場合、往復パターン（A→B、B→A）を検出して「A駅～B駅 往復」として統合</description></item>
        /// <item><description>GroupIdが未設定の場合、乗継パターン（降車駅=次の乗車駅）を検出して「始発駅～終着駅」として統合</description></item>
        /// <item><description>循環移動（始点=終点）の場合は統合せず個別表示</description></item>
        /// </list>
        /// </remarks>
        private string GenerateRailwaySummary(List<LedgerDetail> trips)
        {
            if (trips.Count == 0)
            {
                return string.Empty;
            }

            var sortedTrips = SortChronologically(trips);

            // Issue #484: GroupIdが設定されている場合はそのグループ化を優先
            var hasGroupId = sortedTrips.Any(t => t.GroupId.HasValue);
            if (hasGroupId)
            {
                return GenerateRailwaySummaryWithGroupId(sortedTrips);
            }

            // GroupIdが設定されていない場合は従来の自動判定
            return GenerateRailwaySummaryAutomatic(sortedTrips);
        }

        /// <summary>
        /// GroupIdに基づいて鉄道利用の摘要を生成（Issue #484）
        /// </summary>
        private string GenerateRailwaySummaryWithGroupId(List<LedgerDetail> sortedTrips)
        {
            var result = new List<string>();

            // GroupIdでグループ化（NULLは個別のグループとして扱う）
            // まず、GroupIdがある経路とない経路を分離
            // Issue #1735: 運賃が発生した片側欠落明細も摘要から落とさない（欠落側はプレースホルダで補完）
            var groupedTrips = sortedTrips
                .Where(t => t.GroupId.HasValue && IsSummarizableTrip(t))
                .GroupBy(t => t.GroupId!.Value)
                .OrderBy(g => g.Min(t => t.UseDate ?? DateTime.MaxValue));

            var ungroupedTrips = sortedTrips
                .Where(t => !t.GroupId.HasValue && IsSummarizableTrip(t))
                .ToList();

            // グループ化された経路を処理
            foreach (var group in groupedTrips)
            {
                var groupTrips = SortChronologically(group.ToList());
                if (groupTrips.Count == 1)
                {
                    var route = ToRoute(groupTrips[0]);
                    result.Add($"{route.Entry}～{route.Exit}");
                }
                else
                {
                    // Issue #548: グループ内でも往復・乗継を自動判定
                    // 単純にfirst/lastを使うと往復（A→B, B→A）で「A～A」になるバグがあった
                    var groupSummary = GenerateRailwaySummaryAutomatic(groupTrips);
                    if (!string.IsNullOrEmpty(groupSummary))
                    {
                        result.Add(CollapseExplicitGroupSummary(groupTrips, groupSummary));
                    }
                }
            }

            // グループ化されていない経路は自動判定
            if (ungroupedTrips.Count > 0)
            {
                var autoSummary = GenerateRailwaySummaryAutomatic(ungroupedTrips);
                if (!string.IsNullOrEmpty(autoSummary))
                {
                    result.Add(autoSummary);
                }
            }

            return string.Join(RouteSeparator, result);
        }

        /// <summary>
        /// 明示的なグループの摘要を1区間へ畳む（Issue #1816）
        /// </summary>
        /// <param name="groupTrips">時系列に並べ替え済みの、同一グループの明細</param>
        /// <param name="automaticSummary">グループ内で自動判定した結果の摘要</param>
        /// <returns>区間が複数残っている場合は「始発駅～終着駅」、それ以外は <paramref name="automaticSummary"/></returns>
        /// <remarks>
        /// <para>
        /// GroupId は「利用者がこの明細群を1つの利用として指定した」ことを表す（Issue #484 / #633、
        /// 履歴詳細画面の「すべて統合」は Issue #1816 で全項目に同一 GroupId を付与するようになった）。
        /// ところがグループ内の生成は自動判定に委ねているため（Issue #548: 往復を「A～A」にしないため）、
        /// 乗り継ぎでも往復でもない非連続区間は「A駅～B駅、C駅～D駅」と分かれたままだった。
        /// これでは「1つのグループに統合しました」という案内と摘要が食い違う。
        /// </para>
        /// <para>
        /// 自動判定の結果に区間の区切り（<see cref="RouteSeparator"/>）が残っている場合だけ、
        /// 始発駅～終着駅へ畳む。<b>往復（「A駅～B駅 往復」）と乗継統合（単一区間）はそのまま維持する</b> —
        /// これらは自動判定が既に1区間へまとめており、畳むと「往復」の情報が失われるため。
        /// </para>
        /// <para>
        /// 畳まない条件は2つある（いずれも Issue #1816 のコードレビューで判明）。
        /// <list type="number">
        /// <item><description>
        /// 自動判定の結果に往復（<c>SummaryText.RoundTripSuffix</c>）が含まれる場合。「、」は
        /// 「往復＋別区間」（A～B 往復、C～D）でも現れるため、区切りの有無だけで畳むと
        /// 往復の情報が失われ、さらに「A～D」という**実際には乗っていない区間**が生成される
        /// </description></item>
        /// <item><description>
        /// 始発駅と終着駅が同一（乗り継ぎ駅としての同一視を含む）の場合。畳むと「A駅～A駅」となり、
        /// Issue #548 が自動判定パスを導入して避けたはずの無意味な摘要が、
        /// 6年保存の台帳・物品出納簿へそのまま記録される
        /// </description></item>
        /// </list>
        /// どちらも「畳まない」＝従来どおり自動判定の結果を使う側へ倒す。
        /// </para>
        /// </remarks>
        private string CollapseExplicitGroupSummary(List<LedgerDetail> groupTrips, string automaticSummary)
        {
            if (!automaticSummary.Contains(RouteSeparator))
            {
                return automaticSummary;
            }

            // 往復が含まれる場合は畳まない（往復の情報が失われ、未乗車の区間を作るため）
            // 接尾辞が空に設定されている場合は Contains が常に true になるため除外する
            var roundTripSuffix = _options.SummaryText.RoundTripSuffix;
            if (!string.IsNullOrEmpty(roundTripSuffix) && automaticSummary.Contains(roundTripSuffix))
            {
                return automaticSummary;
            }

            var routes = groupTrips.Where(IsSummarizableTrip).Select(ToRoute).ToList();
            if (routes.Count == 0)
            {
                return automaticSummary;
            }

            var start = routes[0].Entry;
            var end = routes[routes.Count - 1].Exit;

            // 端点の駅名が解決できていない場合は畳まない（Issue #1816 のコードレビュー）。
            // 「博多～?、薬院～大橋」を畳むと「?～大橋」になり、解決できていた駅名まで捨てて
            // 情報量が減った摘要が 6 年保存の台帳へ入る。畳まなければ「?」は片側だけに留まる
            if (start == UnknownStationPlaceholder || end == UnknownStationPlaceholder)
            {
                return automaticSummary;
            }

            // 始点＝終点は「A駅～A駅」になるため畳まない（Issue #548 の循環移動と同じ扱い）
            if (AreTransferStations(start, end))
            {
                return automaticSummary;
            }

            return $"{start}～{end}";
        }

        /// <summary>
        /// 摘要中で複数区間を区切る文字（Issue #1816）
        /// </summary>
        private const string RouteSeparator = "、";

        /// <summary>
        /// 自動判定で鉄道利用の摘要を生成（従来のロジック）
        /// </summary>
        private string GenerateRailwaySummaryAutomatic(List<LedgerDetail> sortedTrips)
        {
            // Issue #1735: 片側だけ駅名が解決できた明細（StationCode.csv 未収録の新駅等）を
            // 摘要から黙って落とさず、欠落側をプレースホルダで埋めて経路に採用する。
            // 両側とも駅名が無い明細は従来どおり除外する（その結果摘要が空になるケースは
            // LendingService 側の代替文言ガードが受け止める）
            var routes = sortedTrips
                .Where(IsSummarizableTrip)
                .Select(ToRoute)
                .ToList();

            return BuildRouteSummary(routes);
        }

        /// <summary>
        /// 明細を摘要の経路として採用できるか（Issue #1735）
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item><description>両側とも駅名あり → 採用（従来どおり。同一駅乗降の 0 円移動も含む）</description></item>
        /// <item><description>両側とも駅名なし → 除外（従来どおり。経路として表現できない）</description></item>
        /// <item><description>片側のみ駅名あり → 運賃が発生した完了移動のみ採用。金額 0 の明細は
        /// 「入場記録のみ」（未完了移動）とみなし従来どおり除外する。摘要は払出金額の説明であり、
        /// 払出のない未完了記録を載せない仕様（SummaryGeneratorComprehensiveTests TC019）を維持する。
        /// 金額 null は情報不足のため、区間の黙示的欠落を防ぐ側（採用）に倒す</description></item>
        /// </list>
        /// </remarks>
        private static bool IsSummarizableTrip(LedgerDetail trip)
        {
            var hasEntry = !string.IsNullOrEmpty(trip.EntryStation);
            var hasExit = !string.IsNullOrEmpty(trip.ExitStation);

            if (hasEntry && hasExit)
            {
                return true;
            }
            if (!hasEntry && !hasExit)
            {
                return false;
            }

            // 片側欠落: 運賃が発生していれば採用（int? の lifted 比較により Amount=null も採用側）
            return trip.Amount != 0;
        }

        /// <summary>
        /// 明細を経路タプルへ変換する。駅名を解決できなかった側は
        /// <see cref="UnknownStationPlaceholder"/> で埋める（Issue #1735）
        /// </summary>
        private static (string Entry, string Exit) ToRoute(LedgerDetail trip) => (
            Entry: string.IsNullOrEmpty(trip.EntryStation) ? UnknownStationPlaceholder : trip.EntryStation!,
            Exit: string.IsNullOrEmpty(trip.ExitStation) ? UnknownStationPlaceholder : trip.ExitStation!);

        /// <summary>
        /// 往復を検出
        /// </summary>
        /// <param name="routes">経路リスト（時系列順：古い順であること）</param>
        /// <returns>往復経路のリスト。Startは出発点（往路の乗車駅）、Endは折り返し点（往路の降車駅）</returns>
        /// <remarks>
        /// <para>
        /// 入力リストは必ず時系列順（古い順）であること。
        /// 往復検出時は最初にマッチした経路（routes[i]）の方向を採用するため、
        /// 順序が逆だと「帰りの経路」が先に来てしまい、摘要の駅順が逆転する。
        /// </para>
        /// <para>
        /// 例：薬院→博多→薬院の移動
        /// - 正しい順序: [(薬院,博多), (博多,薬院)] → "薬院～博多 往復"
        /// - 逆順の場合: [(博多,薬院), (薬院,博多)] → "博多～薬院 往復" (不正)
        /// </para>
        /// </remarks>
        private List<RoundTrip> DetectRoundTrips(List<(string Entry, string Exit)> routes)
        {
            var roundTrips = new List<RoundTrip>();
            var usedIndices = new HashSet<int>();

            for (int i = 0; i < routes.Count; i++)
            {
                if (usedIndices.Contains(i))
                {
                    continue;
                }

                // 逆方向の経路を探す
                for (int j = i + 1; j < routes.Count; j++)
                {
                    if (usedIndices.Contains(j))
                    {
                        continue;
                    }

                    // A→B と B→A のパターン
                    // Issue #1905: 端点の突合は同一視グループを考慮する
                    // （道路を挟んで向かい合うバス停や、事業者違いで名前が異なる駅を
                    // 往復の折り返し点として認識するため）
                    if (AreTransferStations(routes[i].Entry, routes[j].Exit)
                        && AreTransferStations(routes[i].Exit, routes[j].Entry))
                    {
                        // Issue #1905: 復路の乗降地名も持ち回る。同一視グループにより
                        // 往路の端点とは名前が異なり得るため、摘要へ併記して
                        // 実際に乗降した場所を台帳から失わないようにする
                        roundTrips.Add(new RoundTrip(
                            routes[i].Entry,
                            routes[i].Exit,
                            routes[j].Entry,
                            routes[j].Exit,
                            i,
                            j));
                        usedIndices.Add(i);
                        usedIndices.Add(j);
                        break;
                    }
                }
            }

            return roundTrips;
        }

        /// <summary>
        /// 往復で使われなかった経路を取得
        /// </summary>
        /// <remarks>
        /// 各往復は forward 方向（A→B）と reverse 方向（B→A）の経路を 1 つずつ消費する。
        /// 同方向の往復が N 件ある場合、forward は N 回、reverse も N 回まで消費可能。
        /// この消費可能枠を超えた経路だけが余りとして残る。
        ///
        /// 旧実装は <c>(Entry, Exit)</c> の方向ペアごとに <c>usedCount</c> を取り、
        /// 「2 回目以降は余り」と判定していたため、N 往復ある同方向のうち forward 1 件と
        /// reverse 1 件だけが消費され、残り <c>2(N-1)</c> 件が余りに残る不具合があった
        /// （Issue #1579）。
        /// </remarks>
        private List<(int Index, string Entry, string Exit)> GetRemainingRoutes(
            List<(string Entry, string Exit)> allRoutes,
            List<RoundTrip> roundTrips)
        {
            // 往復の正方向ペアごとに件数を集計（例: (天神,博多) の往復が 2 件 → forwardQuotas[(天神,博多)] = 2）
            //
            // Issue #1905: キーは CanonicalStation で正規化する。DetectRoundTrips が
            // 同一視グループで往復を検出するようになったため、ここだけ完全一致のままだと
            // 復路（例: 下原中央→天神中央郵便局前）が往路（天神日銀前→下原中央）の
            // 逆方向キーと一致せず「余り」に残り、「A～B 往復、B～C」と重複表示になる。
            // 突合に使う名前だけを正規化し、余りとして返す経路は元の名前のまま保つ。
            var forwardQuotas = new Dictionary<(string, string), int>();
            foreach (var rt in roundTrips)
            {
                var key = (CanonicalStation(rt.Start), CanonicalStation(rt.End));
                forwardQuotas[key] = forwardQuotas.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            var consumedForward = new Dictionary<(string, string), int>();
            var consumedReverse = new Dictionary<(string, string), int>();

            var remaining = new List<(int Index, string Entry, string Exit)>();
            for (int index = 0; index < allRoutes.Count; index++)
            {
                var route = allRoutes[index];
                var canonicalEntry = CanonicalStation(route.Entry);
                var canonicalExit = CanonicalStation(route.Exit);
                var forwardKey = (canonicalEntry, canonicalExit);
                var reverseKey = (canonicalExit, canonicalEntry);

                // forward 方向で消費できるか
                if (forwardQuotas.TryGetValue(forwardKey, out var fwdQuota))
                {
                    var alreadyConsumed = consumedForward.TryGetValue(forwardKey, out var c) ? c : 0;
                    if (alreadyConsumed < fwdQuota)
                    {
                        consumedForward[forwardKey] = alreadyConsumed + 1;
                        continue;
                    }
                }

                // reverse 方向で消費できるか
                if (forwardQuotas.TryGetValue(reverseKey, out var revQuota))
                {
                    var alreadyConsumed = consumedReverse.TryGetValue(reverseKey, out var c) ? c : 0;
                    if (alreadyConsumed < revQuota)
                    {
                        consumedReverse[reverseKey] = alreadyConsumed + 1;
                        continue;
                    }
                }

                // どちらの方向枠も埋まっている、または往復に該当しない経路 → 余り
                remaining.Add((index, route.Entry, route.Exit));
            }

            return remaining;
        }

        /// <summary>
        /// 連続する経路を統合（乗継判定）
        /// 注：起点と終点が同じになる循環移動の場合は統合せず、個別の経路を表示
        /// </summary>
        /// <remarks>
        /// Issue #1580: <c>AreTransferStations</c> の隣接判定だけでは「乗継（順方向に進む）」と
        /// 「往復（戻ってくる）」を区別できないため、A→B→A→B 型のチェーンを 1 経路に
        /// 潰してしまうバグがあった。本実装ではチェーン内の既訪問駅集合を保持し、
        /// 次経路の終点が既訪問なら原則として方向反転とみなして乗継統合を打ち切る。
        ///
        /// 例外: 「次経路の終点 == チェーンの始点」かつ「チェーン長 ≥ 3」となる場合は
        /// 「閉じた循環（A→B→C→A 型の単一周回移動）」とみなしてチェーンを継続させ、
        /// 末尾の <see cref="AddConsolidatedChain"/> の循環検出に個別表示を委ねる
        /// （Issue #878 で確立された奇数長循環 = 個別表示の設計を維持）。
        ///
        /// 一方 A→B→A（チェーン長 2 の反転）は break して個別化し、後段の
        /// <see cref="DetectRoundTrips"/> に往復ペアとして拾わせる。
        ///
        /// 既訪問判定は <see cref="AreTransferStations"/> による同一視を考慮する
        /// （例: 天神 と 西鉄福岡(天神) は同一駅とみなす）。
        ///
        /// Issue #1902: さらに、現在のチェーンが「直前に確定したチェーンの完全な逆走」
        /// （＝往復の復路）になっている場合は、次経路への乗継延長を行わない。
        /// 復路を次の移動と統合すると往復ペアの片割れが消費され、後段の
        /// <see cref="DetectRoundTrips"/> が往復を検出できなくなるため
        /// （例: A→B、B→A、C→D、D→C で復路 B→A が乗換駅グループ経由で
        /// C→D と統合され「B～D」という実際には乗っていない区間になっていた）。
        ///
        /// Issue #1905: かつては逆走判定が <see cref="AreTransferStations"/> による同一視を含む一方で
        /// <see cref="DetectRoundTrips"/> の往復ペア照合は駅名の完全一致だったため、
        /// 復路の端点が同一視グループ内の別名（例: 天神→博多 の復路が 博多→西鉄福岡(天神)）だと
        /// 延長の打ち切りだけが働いて摘要は「往復」表記にならなかった。
        /// 現在は往復ペア照合も同一視を考慮するため、この非対称は解消されている。
        /// </remarks>
        /// <param name="routes">経路の(Entry, Exit)タプルリスト（時系列順）</param>
        /// <param name="suppressedIndices">
        /// Issue #1916: これらの添字の経路では乗継延長を行わずチェーンを閉じる（null で抑止なし）。
        /// <see cref="BuildRouteSummary"/> が「延長を抑止した候補」を作るために使う。
        /// 添字は<b>最上位の経路リスト</b>のもので、再帰呼び出し（循環チェーンの分割）へは
        /// <paramref name="indexOffset"/> を通じて引き継がれる — 引き継がないと、抑止点が
        /// 分割された半分の内側に落ちたときその半分が抑止点で再統合され、
        /// 「抑止した候補」が既定解と同一物になって探索したつもりの候補が消える。
        /// </param>
        /// <param name="indexOffset">
        /// Issue #1916: <paramref name="routes"/> の先頭が最上位リストの何番目かを表す。
        /// 再帰呼び出しで抑止点の添字を対応付けるために使う。
        /// </param>
        private List<ConsolidatedRoute> ConsolidateRoutes(
            List<(string Entry, string Exit)> routes,
            ISet<int> suppressedIndices = null,
            int indexOffset = 0)
        {
            if (routes.Count == 0)
            {
                return new List<ConsolidatedRoute>();
            }

            var result = new List<ConsolidatedRoute>();
            var chainStartIndex = 0;
            var currentStart = routes[0].Entry;
            var currentEnd = routes[0].Exit;
            var visitedInChain = new List<string> { currentStart, currentEnd };

            // Issue #1902: 直前に確定したチェーンの端点（逆走判定用）
            string previousChainStart = null;
            string previousChainEnd = null;

            for (int i = 1; i < routes.Count; i++)
            {
                var isTransfer = AreTransferStations(currentEnd, routes[i].Entry);
                var nextExit = routes[i].Exit;
                var nextExitVisited = visitedInChain.Any(v => AreTransferStations(v, nextExit));
                var nextExitEqualsStart = AreTransferStations(currentStart, nextExit);
                var chainLengthAfter = i - chainStartIndex + 1;
                var isClosingCircular = nextExitEqualsStart && chainLengthAfter >= 3;

                // Issue #1902: 現在のチェーンが直前チェーンの完全な逆走（往復の復路）なら
                // ここでチェーンを閉じ、往復ペアを DetectRoundTrips に委ねる
                var isReturnLegOfPreviousChain = previousChainStart != null
                    && AreTransferStations(currentStart, previousChainEnd)
                    && AreTransferStations(currentEnd, previousChainStart);

                // Issue #1916: 候補生成のためにこの位置の延長を抑止する
                var isSuppressed = suppressedIndices != null
                    && suppressedIndices.Contains(indexOffset + i);

                if (isTransfer && !isReturnLegOfPreviousChain && !isSuppressed
                    && (!nextExitVisited || isClosingCircular))
                {
                    currentEnd = nextExit;
                    if (!nextExitVisited)
                    {
                        visitedInChain.Add(currentEnd);
                    }
                }
                else
                {
                    AddConsolidatedChain(
                        result, routes, chainStartIndex, i - 1, currentStart, currentEnd,
                        suppressedIndices, indexOffset);

                    // 逆走判定は「直前に result へ確定した経路」を基準にする
                    // （循環分割で複数経路が追加された場合は末尾の経路が直前の移動）
                    var lastEmitted = result[result.Count - 1];
                    previousChainStart = lastEmitted.Start;
                    previousChainEnd = lastEmitted.End;

                    chainStartIndex = i;
                    currentStart = routes[i].Entry;
                    currentEnd = routes[i].Exit;
                    visitedInChain = new List<string> { currentStart, currentEnd };
                }
            }

            // 最後のチェーンを追加
            AddConsolidatedChain(
                result, routes, chainStartIndex, routes.Count - 1, currentStart, currentEnd,
                suppressedIndices, indexOffset);

            return result;
        }

        /// <summary>
        /// 統合されたチェーンを結果に追加
        /// 起点と終点が同じ（循環）の場合は個別の経路を追加
        /// </summary>
        private void AddConsolidatedChain(
            List<ConsolidatedRoute> result,
            List<(string Entry, string Exit)> routes,
            int chainStart,
            int chainEnd,
            string consolidatedStart,
            string consolidatedEnd,
            ISet<int> suppressedIndices = null,
            int indexOffset = 0)
        {
            // 起点と終点が同じ場合（循環移動）
            // Issue #878: 乗り継ぎ駅も考慮して循環判定
            if (AreTransferStations(consolidatedStart, consolidatedEnd) && chainEnd > chainStart)
            {
                var chainLength = chainEnd - chainStart + 1;

                // Issue #878: 偶数長の循環チェーンは往復の可能性が高い
                // 中間点で分割して各半分を再統合し、往復判定に渡す
                if (chainLength % 2 == 0 && chainLength >= 4)
                {
                    int mid = chainStart + chainLength / 2 - 1;

                    var firstHalf = new List<(string Entry, string Exit)>();
                    for (int i = chainStart; i <= mid; i++)
                    {
                        firstHalf.Add(routes[i]);
                    }

                    var secondHalf = new List<(string Entry, string Exit)>();
                    for (int i = mid + 1; i <= chainEnd; i++)
                    {
                        secondHalf.Add(routes[i]);
                    }

                    // Issue #1916: 抑止点の添字は最上位リスト基準なので、分割後の
                    // 部分リストへはそれぞれの先頭位置をオフセットとして渡す
                    result.AddRange(ConsolidateRoutes(
                        firstHalf, suppressedIndices, indexOffset + chainStart));
                    result.AddRange(ConsolidateRoutes(
                        secondHalf, suppressedIndices, indexOffset + mid + 1));
                }
                else
                {
                    // 奇数長または2経路の循環は個別の経路として追加
                    for (int i = chainStart; i <= chainEnd; i++)
                    {
                        result.Add(new ConsolidatedRoute(routes[i].Entry, routes[i].Exit, 1));
                    }
                }
            }
            else
            {
                result.Add(new ConsolidatedRoute(
                    consolidatedStart, consolidatedEnd, chainEnd - chainStart + 1));
            }
        }

        /// <summary>
        /// 乗継統合後の 1 経路（Issue #1916）
        /// </summary>
        /// <remarks>
        /// <see cref="LegCount"/> は、この 1 経路が束ねている<b>元の明細（区間）</b>の件数。
        /// 統合候補を比べるときのカバレッジは統合後の本数ではなくこの件数で数える
        /// （<see cref="BuildRouteSummary"/> の remarks 参照）。
        /// </remarks>
        private readonly struct ConsolidatedRoute
        {
            public ConsolidatedRoute(string start, string end, int legCount)
            {
                Start = start;
                End = end;
                LegCount = legCount;
            }

            /// <summary>統合後の乗車地</summary>
            public string Start { get; }

            /// <summary>統合後の降車地</summary>
            public string End { get; }

            /// <summary>束ねている元の明細（区間）の件数</summary>
            public int LegCount { get; }
        }

        /// <summary>
        /// バス利用の摘要を生成
        /// </summary>
        private string GenerateBusSummary(List<LedgerDetail> trips)
        {
            var sortedTrips = SortChronologically(trips);

            // GroupIdが設定されている場合はグループ化を優先（鉄道と同様）
            var hasGroupId = sortedTrips.Any(t => t.GroupId.HasValue);
            if (hasGroupId)
            {
                return GenerateBusSummaryWithGroupId(sortedTrips);
            }

            return GenerateBusSummaryAutomatic(sortedTrips);
        }

        /// <summary>
        /// GroupIdに基づいてバス利用の摘要を生成
        /// </summary>
        private string GenerateBusSummaryWithGroupId(List<LedgerDetail> sortedTrips)
        {
            var result = new List<string>();

            // GroupIdでグループ化（NULLは個別のグループとして扱う）
            var groupedTrips = sortedTrips
                .Where(t => t.GroupId.HasValue)
                .GroupBy(t => t.GroupId!.Value)
                .OrderBy(g => g.Min(t => t.UseDate ?? DateTime.MaxValue));

            var ungroupedTrips = sortedTrips
                .Where(t => !t.GroupId.HasValue)
                .ToList();

            // グループ化された経路を処理
            foreach (var group in groupedTrips)
            {
                var groupTrips = SortChronologically(group.ToList());
                var groupSummary = GenerateBusSummaryAutomatic(groupTrips);
                if (!string.IsNullOrEmpty(groupSummary))
                {
                    result.Add(groupSummary);
                }
            }

            // グループ化されていない経路は自動判定
            if (ungroupedTrips.Count > 0)
            {
                var autoSummary = GenerateBusSummaryAutomatic(ungroupedTrips);
                if (!string.IsNullOrEmpty(autoSummary))
                {
                    result.Add(autoSummary);
                }
            }

            return string.Join(RouteSeparator, result);
        }

        /// <summary>
        /// 自動判定でバス利用の摘要を生成
        /// </summary>
        private string GenerateBusSummaryAutomatic(List<LedgerDetail> sortedTrips)
        {
            // バス停名が入力されているものを時系列順（古い→新しい）で取得
            var allBusStops = sortedTrips
                .Where(t => !string.IsNullOrEmpty(t.BusStops))
                .Select(t => t.BusStops!)
                .ToList();

            if (allBusStops.Count == 0)
            {
                // 未入力の場合はプレースホルダ
                return BusPlaceholder;
            }

            // Issue #985: 「A～B」形式のバス停名から乗り継ぎ統合・往復検出を行う
            var parsedRoutes = allBusStops
                .Select(ParseBusRoute)
                .Where(r => r.HasValue)
                .Select(r => r!.Value)
                .ToList();

            // 解析できなかったバス停名（「A～B」形式でないもの）
            var unparsed = allBusStops
                .Where(bs => !ParseBusRoute(bs).HasValue)
                .Distinct()
                .ToList();

            if (parsedRoutes.Count >= 2)
            {
                // 共通パイプラインで統合・往復検出・整形
                var routeSummary = BuildRouteSummary(parsedRoutes);

                if (unparsed.Count > 0)
                {
                    return string.Join(RouteSeparator, new[] { routeSummary }.Concat(unparsed));
                }
                return routeSummary;
            }

            // 経路が1件以下の場合: 重複除去して連結
            return string.Join(RouteSeparator, allBusStops.Distinct());
        }

        /// <summary>
        /// バス停名を「A～B」形式として解析（Issue #985）
        /// </summary>
        /// <returns>解析成功時は(Entry, Exit)のタプル、失敗時はnull</returns>
        private static (string Entry, string Exit)? ParseBusRoute(string busStops)
        {
            var parts = busStops.Split('～');
            if (parts.Length == 2 &&
                !string.IsNullOrWhiteSpace(parts[0]) &&
                !string.IsNullOrWhiteSpace(parts[1]))
            {
                return (parts[0], parts[1]);
            }
            return null;
        }

        /// <summary>
        /// 貸出中を示す摘要を生成
        /// </summary>
        public static string GetLendingSummary()
        {
            return _options.SummaryText.LendingSummary;
        }

        /// <summary>
        /// チャージの摘要を生成（市長事務部局用デフォルト）
        /// </summary>
        public static string GetChargeSummary()
        {
            return GetChargeSummary(DepartmentType.MayorOffice);
        }

        /// <summary>
        /// チャージの摘要を部署種別に応じて生成
        /// </summary>
        /// <param name="departmentType">部署種別</param>
        /// <returns>市長事務部局:「役務費によりチャージ」、企業会計部局:「旅費によりチャージ」</returns>
        public static string GetChargeSummary(DepartmentType departmentType)
        {
            return departmentType == DepartmentType.EnterpriseAccount
                ? _options.SummaryText.ChargeSummaryEnterprise
                : _options.SummaryText.ChargeSummaryMayorOffice;
        }

        /// <summary>
        /// ポイント還元の摘要を生成
        /// </summary>
        public static string GetPointRedemptionSummary()
        {
            return _options.SummaryText.PointRedemption;
        }

        /// <summary>
        /// 払い戻しの摘要を生成
        /// </summary>
        public static string GetRefundSummary()
        {
            return _options.SummaryText.RefundSummary;
        }

        /// <summary>
        /// 区間を特定できない利用の代替摘要を生成（Issue #1735）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 利用明細から摘要を生成できなかった（<see cref="Generate"/> が空文字を返した）場合に、
        /// 摘要が空欄の台帳行を保存しないための代替文言。LendingService の Ledger 生成経路が使う。
        /// 片側欠落は <see cref="UnknownStationPlaceholder"/> による補完で摘要に採用されるため、
        /// 本文言が使われるのは乗車駅・降車駅の両方が欠落した鉄道明細のみ。
        /// </para>
        /// <para>交通系固有メソッド（駅名からの摘要組み立ての安全網。domain-boundaries.md 参照）。</para>
        /// </remarks>
        public static string GetUnknownUsageSummary()
        {
            return _options.SummaryText.UnknownUsageSummary;
        }

        /// <summary>
        /// 残高不足時の備考テキストを生成
        /// </summary>
        /// <remarks>
        /// Issue #380対応: 残高不足で不足分を現金でチャージした場合の備考テキスト。
        /// 例: 運賃210円に対し残高200円の場合、不足額10円を現金で支払い。
        /// </remarks>
        /// <param name="totalFare">支払総額（運賃）</param>
        /// <param name="shortfall">不足額（現金支払額）</param>
        /// <returns>備考テキスト</returns>
        public static string GetInsufficientBalanceNote(int totalFare, int shortfall)
        {
            return string.Format(_options.SummaryText.InsufficientBalanceNoteFormat, totalFare, shortfall);
        }

        /// <summary>
        /// 前年度繰越の摘要を生成
        /// </summary>
        public static string GetCarryoverFromPreviousYearSummary()
        {
            return _options.SummaryText.CarryoverFromPreviousYear;
        }

        /// <summary>
        /// 前月繰越の摘要を生成
        /// </summary>
        /// <param name="previousMonth">前月の月番号（1-12）</param>
        public static string GetCarryoverFromPreviousMonthSummary(int previousMonth)
        {
            return string.Format(_options.SummaryText.CarryoverFromMonthFormat, previousMonth);
        }

        /// <summary>
        /// 次年度繰越の摘要を生成
        /// </summary>
        public static string GetCarryoverToNextYearSummary()
        {
            return _options.SummaryText.CarryoverToNextYear;
        }

        /// <summary>
        /// 年度途中導入時の繰越摘要を生成（Issue #510）
        /// </summary>
        /// <param name="carryoverMonth">繰越元の月（1-12）</param>
        /// <returns>「○月から繰越」形式の摘要文字列</returns>
        /// <remarks>
        /// 年度途中から本アプリを導入する場合に使用。
        /// 例: 5月まで紙の出納簿を使用し、6月からアプリを使う場合は「5月から繰越」を生成。
        /// </remarks>
        public static string GetMidYearCarryoverSummary(int carryoverMonth)
        {
            return string.Format(_options.SummaryText.MidYearCarryoverFormat, carryoverMonth);
        }

        /// <summary>
        /// 年度途中導入の繰越レコード日付を計算（Issue #599）
        /// </summary>
        /// <param name="carryoverMonth">繰越元の月（1-12）</param>
        /// <param name="registrationDate">登録日</param>
        /// <returns>繰越月の翌月1日</returns>
        /// <remarks>
        /// 繰越レコードの日付は「繰越月の翌月1日」とする。
        /// 例: 2月9日に「1月から繰越」→ 2月1日、1月15日に「12月から繰越」→ 1月1日。
        /// 繰越月は「登録月以前に最後に現れた同月」とみなす。
        /// 例: 2月15日に「11月から繰越」→ 前年11月が繰越月なので前年12月1日。
        /// 例: 2月20日に「2月から繰越」→ 当年2月が繰越月なので当年3月1日（Issue #1812）。
        ///
        /// Issue #1812: 旧実装は先に「翌月」を求めてから年を判定していたため、
        /// 12月→1月の折り返し後の値で大小比較することになり、
        /// 繰越月＝登録月（翌月が必ず登録月より後になる）で1年前へ落ちていた。
        /// 繰越月そのものの年を先に確定し、AddMonths(1) に桁上がりを任せる。
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">carryoverMonthが1〜12の範囲外の場合</exception>
        public static DateTime GetMidYearCarryoverDate(int carryoverMonth, DateTime registrationDate)
        {
            if (carryoverMonth < 1 || carryoverMonth > 12)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(carryoverMonth),
                    carryoverMonth,
                    "繰越月は1〜12の範囲で指定してください。");
            }

            // 繰越月が属する年を先に確定する（登録月以前に最後に現れた同月）
            var carryoverYear = carryoverMonth <= registrationDate.Month
                ? registrationDate.Year
                : registrationDate.Year - 1;

            return new DateTime(carryoverYear, carryoverMonth, 1).AddMonths(1);
        }

        /// <summary>
        /// 繰越月の選択に対して実際に生成される繰越レコード日付の説明文を生成（Issue #1812）
        /// </summary>
        /// <param name="carryoverMonth">繰越元の月（1-12）</param>
        /// <param name="registrationDate">登録日</param>
        /// <returns>カード登録モードダイアログに表示する説明文（前年扱いの場合は注意書きを含む）</returns>
        /// <remarks>
        /// 【汎用】物品出納簿の繰越様式に属し、交通系固有の知識を含まない（Issue #1695 の境界分類）。
        ///
        /// 繰越月が登録月より後の場合は「前年の同月」として解決されるが、
        /// これは正当な運用（2月登録で「11月から繰越」）と誤選択（2月登録で「5月から繰越」）の
        /// 両方を含むため、コンボから除外せず解決結果を画面に提示して職員に判断させる。
        /// </remarks>
        public static string GetMidYearCarryoverDateDescription(int carryoverMonth, DateTime registrationDate)
        {
            var recordDate = GetMidYearCarryoverDate(carryoverMonth, registrationDate);
            var description =
                $"繰越レコードの日付: {recordDate:yyyy年M月d日}（{WarekiConverter.ToWareki(recordDate)}）";

            if (carryoverMonth > registrationDate.Month)
            {
                // この分岐は GetMidYearCarryoverDate が前年へ解決する条件そのものなので、
                // 繰越月が属する年は必ず登録日の前年になる
                var carryoverYear = registrationDate.Year - 1;
                description +=
                    Environment.NewLine +
                    $"※ 選択した{carryoverMonth}月は登録日（{registrationDate:yyyy年M月d日}）より後の月のため、" +
                    $"前年（{carryoverYear}年）の{carryoverMonth}月として扱われます。" +
                    $"当年の月を指定する場合は、{registrationDate.Month}月以前の月を選択してください。";
            }

            return description;
        }

        /// <summary>
        /// 摘要が年度途中導入の繰越かどうかを判定（Issue #510）
        /// </summary>
        /// <param name="summary">摘要文字列</param>
        /// <returns>「○月から繰越」形式の場合true</returns>
        public static bool IsMidYearCarryoverSummary(string? summary)
        {
            if (string.IsNullOrEmpty(summary))
            {
                return false;
            }

            try
            {
                return Regex.IsMatch(summary, _options.SummaryText.MidYearCarryoverPattern);
            }
            catch (ArgumentException)
            {
                // 不正な正規表現の場合はデフォルトパターンにフォールバック
                // （リテラルを直書きせず SummaryTextOptions の既定値を単一の真実源とする。
                //   GetMidYearCarryoverLikePattern のフォールバックと同じ流儀）
                return Regex.IsMatch(summary, new SummaryTextOptions().MidYearCarryoverPattern);
            }
        }

        /// <summary>
        /// 繰越摘要を SQL の LIKE で近似判定するためのパターンを導出（Issue #1749）
        /// </summary>
        /// <returns>LIKE パターン。エスケープ文字はバックスラッシュ（SQL 側で <c>ESCAPE '\'</c> を指定すること）</returns>
        /// <remarks>
        /// <para>
        /// 判定の正は <see cref="IsMidYearCarryoverSummary"/>（正規表現 <c>MidYearCarryoverPattern</c>）だが、
        /// SQLite の SQL では正規表現が使えないため、生成書式 <c>MidYearCarryoverFormat</c> の
        /// 月プレースホルダー <c>{0}</c> を <c>%</c> に置き換えた LIKE パターンで近似する。
        /// 既定書式では従来 SQL にハードコードされていた <c>'%月から繰越'</c> と一致する。
        /// 近似のため「13月から繰越」のような範囲外の月や「備考 4月から繰越」のような
        /// 接頭辞付きにも一致する（先頭 <c>%</c> は月数字だけでなく任意の接頭辞を許す）。生成側
        /// （<see cref="GetMidYearCarryoverSummary"/>）は 1〜12 月しか保存しないため
        /// 実データでは乖離しない（従来のハードコードと同じ近似度）。CSV インポート等で
        /// この形の摘要を持ち込むと、SQL（一致）と C# 正規表現（不一致）で判定が分かれる点に注意。
        /// </para>
        /// <para>
        /// 書式リテラル部の LIKE メタ文字（<c>%</c> <c>_</c> <c>\</c>）はバックスラッシュでエスケープする。
        /// 不正な書式（<c>string.Format</c> が <see cref="FormatException"/> で失敗する、
        /// または書式が null で <see cref="ArgumentNullException"/> になる）は既定書式へ
        /// フォールバックする（<see cref="IsMidYearCarryoverSummary"/> の不正正規表現
        /// フォールバックと同じ方針。本メソッドは全 ledger クエリの構築で呼ばれるため、
        /// 設定不備で照会系が全滅しないことを優先する）。
        /// </para>
        /// <para>
        /// 汎用/固有の別: 汎用（物品出納簿の様式）。<see cref="IsMidYearCarryoverSummary"/> と同群。
        /// </para>
        /// </remarks>
        public static string GetMidYearCarryoverLikePattern()
        {
            // 私用領域の文字を月プレースホルダーの一時マーカーに使う
            // （書式リテラル部のエスケープ処理と {0} の % 置換を混同させないため）
            const string placeholder = "\uE000";

            string formatted;
            try
            {
                formatted = string.Format(_options.SummaryText.MidYearCarryoverFormat, placeholder);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentNullException)
            {
                // 不正な書式（FormatException）／null 書式（ArgumentNullException）は
                // 既定書式へフォールバック（既定値は SummaryTextOptions と同期）。
                // FormatException だけを catch すると、設定バインドで null が入った場合に
                // ArgumentNullException が漏れて全 ledger クエリが失敗する（Issue #1749 レビュー指摘）
                formatted = string.Format(new SummaryTextOptions().MidYearCarryoverFormat, placeholder);
            }

            return formatted
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_")
                .Replace(placeholder, "%");
        }

        /// <summary>
        /// 月計の摘要を生成
        /// </summary>
        public static string GetMonthlySummary(int month)
        {
            return string.Format(_options.SummaryText.MonthlySummaryFormat, month);
        }

        /// <summary>
        /// 累計の摘要を生成
        /// </summary>
        public static string GetCumulativeSummary()
        {
            return _options.SummaryText.CumulativeSummary;
        }
    }
}
