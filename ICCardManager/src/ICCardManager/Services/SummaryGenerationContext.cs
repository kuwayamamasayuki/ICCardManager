using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ICCardManager.Models;

namespace ICCardManager.Services
{
    /// <summary>
    /// 1 回の摘要生成が参照する設定の世代（スナップショット、Issue #1919）
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SummaryGenerator"/> は Singleton で静的状態を持ち、同一視グループは
    /// システム管理画面から運用中に差し替えられる（Issue #1905）。1 回の摘要生成は
    /// <c>ConsolidateRoutes</c>（乗継統合）→ <c>DetectRoundTrips</c>（往復検出）→
    /// <c>GetRemainingRoutes</c>（余りの算出）と複数の段階で同一視を参照し、
    /// <b>後ろの 2 つは同じ同一視関係を見ていることが正しさの前提</b>になっている
    /// （<c>GetRemainingRoutes</c> は往復の消費枠を <see cref="CanonicalStation"/> で
    /// 正規化した辞書キーで数えるため、<c>DetectRoundTrips</c> が拾ったペアと突合できないと
    /// 復路が「余り」に残り「A～B 往復、B～C」と重複表示になる）。
    /// </para>
    /// <para>
    /// そのため本クラスは<b>不変</b>とし、設定と、そこから導出した同一視グループを
    /// 1 つの参照へまとめる。差し替えは参照 1 回の代入（.NET でアトミック）で行うので、
    /// 読み手は「新旧どちらか一方」を必ず見る（設定は新しいがグループは古い、という
    /// 中間状態が構造的に存在しない）。生成の各段階は
    /// <see cref="SummaryGenerator"/> が入口で捕捉した<b>同一のインスタンス</b>を
    /// 引数で受け取るため、生成の途中で世代が変わることもない。
    /// </para>
    /// </remarks>
    internal sealed class SummaryGenerationContext
    {
        /// <summary>
        /// 摘要テキストの文字列プロパティ（null を既定値で補う対象。Issue #2035）
        /// </summary>
        private static readonly PropertyInfo[] SummaryTextStringProperties = typeof(SummaryTextOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite)
            .ToArray();

        private static readonly SummaryTextOptions DefaultSummaryText = new();

        /// <summary>
        /// 併合済み・空白除外済みの同一視グループ（判定と観測が共有する唯一の値。Issue #2035）
        /// </summary>
        private readonly IReadOnlyList<IReadOnlyList<string>> _transferStationGroups;

        /// <summary>
        /// 名前 → 同一視グループの代表名（Issue #2035）
        /// </summary>
        /// <remarks>
        /// <see cref="AreTransferStations"/> と <see cref="CanonicalStation"/> はどちらも
        /// #1916 の候補探索（経路数に対して急速に増える）の内側で呼ばれる。旧実装は呼ばれるたびに
        /// グループ全体を走査し、<see cref="CanonicalStation"/> は毎回グループを並べ替えていたため、
        /// 登録グループ数に比例して遅くなっていた（実測: 20 区間の日 30 日分で、グループ 0 件 19.6ms・
        /// 50 件 231.6ms・300 件 1,190.8ms）。世代を組み立てるときに 1 度だけ作り、判定は参照 1 回にする。
        /// 両者が同じ辞書を引くので、<c>CanonicalStation(a) == CanonicalStation(b)</c> と
        /// <c>AreTransferStations(a, b)</c> の等価性（<c>GetRemainingRoutes</c> の突合が依存する）も構造で保たれる。
        /// </remarks>
        private readonly Dictionary<string, string> _canonicalStations;

        private SummaryGenerationContext(
            OrganizationOptions? source,
            OrganizationOptions options,
            IReadOnlyList<IReadOnlyList<string>> transferStationGroups,
            Dictionary<string, string> canonicalStations,
            DepartmentType departmentType)
        {
            Source = source;
            Options = options;
            _transferStationGroups = transferStationGroups;
            _canonicalStations = canonicalStations;
            DepartmentType = departmentType;
        }

        /// <summary>
        /// この世代の組織固有設定（null を既定値で補った後の値）
        /// </summary>
        /// <remarks>
        /// Issue #2035: <see cref="OrganizationOptions.SummaryText"/> / <see cref="OrganizationOptions.SummaryRules"/>・
        /// 同一視グループのリスト・摘要テキストの各文字列は <b>null にならない</b>。設定から来た null は
        /// <see cref="Create"/> が 1 か所で既定値へ補うため、生成の各段階は null を守らずに参照してよい。
        /// 空文字は「明示的な設定」として保持する（往復の接尾辞を空にする設定を
        /// <c>CollapseExplicitGroupSummary</c> が想定しているため）。
        /// </remarks>
        public OrganizationOptions Options { get; }

        /// <summary>
        /// この世代を組み立てた元の設定インスタンス（<see cref="Create"/> に渡されたもの、Issue #2035）
        /// </summary>
        /// <remarks>
        /// DI 用コンストラクタ（<see cref="SummaryGenerator(DepartmentType, OrganizationOptions)"/>）が
        /// <b>同じ設定インスタンスで世代を作り直さない</b>ための照合にだけ使う。作り直すと、
        /// 実行中に反映した同一視グループ（<see cref="SummaryGenerator.ApplyTransferStationGroups"/>）が
        /// 起動時の値へ戻る。生成には使わない（正規化済みの <see cref="Options"/> を使う）。
        /// 差し替え（<see cref="WithTransferStationGroups"/> / <see cref="WithDepartmentType"/>）は元の値を引き継ぐ。
        /// </remarks>
        public OrganizationOptions? Source { get; }

        /// <summary>
        /// この世代の部署種別（チャージ摘要の切替に使用、Issue #1975）
        /// </summary>
        /// <remarks>
        /// 部署種別は設定画面（F5）から運用中に変更でき、保存成功時に
        /// <see cref="SummaryGenerator.ApplyDepartmentType"/> が DI シングルトンへ反映する。
        /// 値が意味を持つのは <see cref="WithDepartmentType"/> を通した世代だけで、
        /// <see cref="Create"/> が置く既定値は生成の入口で必ず上書きされる（<see cref="Create"/> の remarks 参照）。
        /// 1 回の摘要生成は複数の地点でチャージ摘要を作る（<c>GenerateByDate</c> は
        /// 日付ごと・チャージ境界ごとに作る）ため、途中で差し替わると<b>同じ生成の中で
        /// 「役務費によりチャージ」と「旅費によりチャージ」が混ざる</b>。
        /// 世代へ畳み込んで入口で 1 回だけ捕捉することで、この中間状態を構造的に無くす
        /// （Issue #1919 の同一視グループと同じ扱い）。
        /// </remarks>
        public DepartmentType DepartmentType { get; }

        /// <summary>
        /// 設定から世代を組み立てる
        /// </summary>
        /// <remarks>
        /// <see cref="DepartmentType"/> は<b>置き場所だけを確保した既定値</b>で、意味を持つのは
        /// <see cref="WithDepartmentType"/> を通した後だけ。部署種別は
        /// <see cref="SummaryGenerator"/> の<b>インスタンス</b>が持ち（明細 CSV 取込・
        /// バス停名入力は設定を読み直して別インスタンスを組み立てるため、Issue #1955）、
        /// 生成の入口（<c>CaptureContext</c>）が世代へ畳み込む。ここで受け取る形にすると
        /// 「静的な世代が持つ部署種別」という第 2 の情報源ができ、どちらが正かが失われる。
        /// </remarks>
        public static SummaryGenerationContext Create(OrganizationOptions? options)
        {
            var effective = Normalize(options);
            var groups = BuildTransferStationGroups(effective);
            return new SummaryGenerationContext(
                options, effective, groups, BuildCanonicalStations(groups), DepartmentType.MayorOffice);
        }

        /// <summary>
        /// 部署種別だけを差し替えた新しい世代を返す（Issue #1975）
        /// </summary>
        /// <remarks>
        /// 同一視グループの再構築（<see cref="BuildTransferStationGroups"/>）を伴わないため、
        /// 生成の入口（<see cref="SummaryGenerator.CaptureContext"/>）から毎回呼んでも
        /// 走査コストは掛からない。<see cref="Options"/> はそのまま共有する
        /// （不変オブジェクトとして扱うため、共有しても中間状態は生じない）。
        /// 汎用/固有の別: 汎用（部署種別によるチャージ摘要の切替は物品出納簿の様式）。
        /// </remarks>
        public SummaryGenerationContext WithDepartmentType(DepartmentType departmentType)
            => departmentType == DepartmentType
                ? this
                : new SummaryGenerationContext(
                    Source, Options, _transferStationGroups, _canonicalStations, departmentType);

        /// <summary>
        /// 同一視グループだけを差し替えた新しい世代を返す（Issue #1905 / #1919）
        /// </summary>
        /// <remarks>
        /// 現行の設定インスタンスを<b>その場で書き換えない</b>。書き換えると、
        /// 既に生成を始めていて古い世代を捕捉済みの呼び出しが、参照経由で新しい
        /// グループを見てしまう（世代を分けた意味が無くなる）。摘要テキスト・
        /// 生成ルールの ON/OFF は現行の値をそのまま引き継ぐ
        /// （<c>development-conventions.md</c>「UPDATE の SET 句は、その経路で
        /// 本当に編集する列に限る」と同じ判断）。
        ///
        /// Issue #2035: 引き継ぎは<b>複製</b>（<see cref="OrganizationOptions.ShallowCopy"/>）で行い、
        /// プロパティを 1 つずつ書き写さない。書き写す形は、設定クラスへプロパティを足した日に
        /// F6 で同一視グループを保存した時点でそのプロパティが黙って既定値へ戻る（#1726 と同じ形）。
        /// 漏れは <c>SummaryGenerationContextTests</c> がリフレクションで検出する。
        /// </remarks>
        public SummaryGenerationContext WithTransferStationGroups(
            IEnumerable<IEnumerable<string>> groups)
        {
            var newRules = Options.SummaryRules.ShallowCopy();
            newRules.TransferStationGroups = (groups ?? Enumerable.Empty<IEnumerable<string>>())
                .Where(g => g != null)
                .Select(g => g.ToList())
                .ToList();

            var newOptions = Options.ShallowCopy();
            newOptions.SummaryRules = newRules;

            var newGroups = BuildTransferStationGroups(newOptions);
            return new SummaryGenerationContext(
                Source, newOptions, newGroups, BuildCanonicalStations(newGroups), DepartmentType);
        }

        /// <summary>
        /// この世代の同一視グループのコピーを返す（観測用、Issue #1905）
        /// </summary>
        /// <remarks>
        /// Issue #2035: 判定（<see cref="AreTransferStations"/> / <see cref="CanonicalStation"/>）が実際に使う
        /// <b>併合済み・空白除外済み</b>のグループを返す。設定の生のリストを返すと、[A,B] と [B,C] の登録で
        /// 2 グループが観測されるのに判定は 1 つの同値類 {A,B,C} で動き、このメソッドで結果を確かめる
        /// テストは併合が壊れても緑のままになる。各グループの名前は設定に最初に現れた順、
        /// グループの並びは各グループの先頭の名前が現れた順。
        /// </remarks>
        public List<List<string>> GetTransferStationGroups()
            => _transferStationGroups
                .Select(g => g.ToList())
                .ToList();

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
        public bool AreTransferStations(string? station1, string? station2)
        {
            // 完全一致
            if (station1 == station2)
            {
                return true;
            }

            // 同一グループ内か（代表名が一致するか）。Dictionary は null キーを引けないため先に除く
            return station1 != null
                && station2 != null
                && _canonicalStations.TryGetValue(station1, out var canonical1)
                && _canonicalStations.TryGetValue(station2, out var canonical2)
                && canonical1 == canonical2;
        }

        /// <summary>
        /// 駅名・バス停名を同一視グループの代表名へ正規化する（Issue #1905）
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="AreTransferStations"/> は「2 つが同一か」しか答えられないため、
        /// 名前を辞書のキーにしている処理（<c>GetRemainingRoutes</c> の往復消費枠）では使えない。
        /// 正規化を挟むと <c>CanonicalStation(a) == CanonicalStation(b)</c> が
        /// <c>AreTransferStations(a, b)</c> と等価になり、辞書のキーとして扱えるようになる。
        /// </para>
        /// <para>
        /// 代表名はグループ内で順序が安定するよう序数比較で最小のものを選ぶ。
        /// 代表名は突合にのみ使い、<b>摘要へ出力する名前には使わない</b>
        /// （利用者が実際に乗降した停留所の名前をそのまま表示するため）。
        /// </para>
        /// </remarks>
        public string? CanonicalStation(string? station)
            => station != null && _canonicalStations.TryGetValue(station, out var canonical)
                ? canonical
                : station;

        /// <summary>
        /// TransferStationGroups を List&lt;List&lt;string&gt;&gt; から List&lt;HashSet&lt;string&gt;&gt; に変換
        /// </summary>
        /// <remarks>
        /// Issue #1905: 名前を共有するグループどうしは 1 つに併合し、同一視を
        /// 真の同値関係（反射・対称・<b>推移</b>律を満たす）にする。
        ///
        /// 併合しないと [A, B] と [B, C] が登録されたとき A ≡ B、B ≡ C なのに
        /// A ≢ C という非推移的な判定になり、<see cref="CanonicalStation"/> による
        /// 正規化（<c>GetRemainingRoutes</c> のキー突合が依存する）が成立しない。
        /// 既定のグループ（天神/西鉄福岡(天神)、千早/西鉄千早）は互いに素なので挙動は変わらない。
        ///
        /// 併合が要るのは、#1905 で管理者が画面からグループを登録できるようになり、
        /// 「天神日銀前と天神中央郵便局前」「天神中央郵便局前と天神北」のように
        /// 重なるグループが実際に作られ得るため。
        /// </remarks>
        private static IReadOnlyList<IReadOnlyList<string>> BuildTransferStationGroups(OrganizationOptions options)
        {
            // 観測（GetTransferStationGroups）で並びが揺れないよう、名前が最初に現れた位置を覚えておく
            var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
            var merged = new List<HashSet<string>>();

            foreach (var group in options.SummaryRules.TransferStationGroups)
            {
                // Issue #2035: 設定のグループ・名前の null は空白と同じく除外する
                if (group == null)
                {
                    continue;
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in group)
                {
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!firstSeen.ContainsKey(name))
                    {
                        firstSeen.Add(name, firstSeen.Count);
                    }

                    names.Add(name);
                }

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

            return merged
                .Select(g => (IReadOnlyList<string>)g.OrderBy(n => firstSeen[n]).ToList())
                .OrderBy(g => firstSeen[g[0]])
                .ToList();
        }

        /// <summary>
        /// 名前 → 代表名の辞書を組み立てる（Issue #2035）
        /// </summary>
        /// <remarks>
        /// 代表名はグループ内で序数比較が最小の名前（<see cref="CanonicalStation"/> の remarks 参照）。
        /// </remarks>
        private static Dictionary<string, string> BuildCanonicalStations(
            IReadOnlyList<IReadOnlyList<string>> groups)
        {
            var canonicalStations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                // .NET Framework 4.8 には Enumerable.Min(IComparer) のオーバーロードが無い
                var canonical = group.OrderBy(n => n, StringComparer.Ordinal).First();
                foreach (var name in group)
                {
                    canonicalStations[name] = canonical;
                }
            }

            return canonicalStations;
        }

        /// <summary>
        /// 設定の null を既定値で補った値を返す（Issue #2035）
        /// </summary>
        /// <remarks>
        /// <para>
        /// 旧実装は <see cref="BuildTransferStationGroups"/> が <c>SummaryRules</c> と各グループを
        /// null チェックせずに参照し（<see cref="WithTransferStationGroups"/> は null のグループを除外していた）、
        /// 生成の各段階は <c>SummaryText.RailwayLabel</c> 等を直接参照していた（<c>ResolveBusLabel</c> だけが
        /// null を守っていた）。null の扱いを参照する側ごとに書くと、次に参照を足す人が守り忘れる。
        /// 世代の入口で 1 回だけ補い、以降は null を表現できない状態にする（#1883）。
        /// </para>
        /// <para>
        /// null は「未設定＝既定値」として補い、<b>空文字は明示的な設定として保持する</b>。
        /// 補うために<b>渡された設定インスタンスを書き換えない</b>（DI シングルトンの設定は
        /// <c>TransferStationGroupService</c> も参照する）。null が無ければ元のインスタンスをそのまま使う。
        /// </para>
        /// <para>
        /// なお appsettings.json の <c>null</c> は構成バインダーが空文字・空のグループへ変換するため、
        /// 設定ファイル経由でここへ null が届くことは無い（実測）。null はコードから組み立てた設定でだけ起きる。
        /// </para>
        /// </remarks>
        private static OrganizationOptions Normalize(OrganizationOptions? options)
        {
            var summaryText = NormalizeSummaryText(options?.SummaryText);
            var summaryRules = NormalizeSummaryRules(options?.SummaryRules);

            if (options != null
                && ReferenceEquals(summaryText, options.SummaryText)
                && ReferenceEquals(summaryRules, options.SummaryRules))
            {
                return options;
            }

            var normalized = options?.ShallowCopy() ?? new OrganizationOptions();
            normalized.SummaryText = summaryText;
            normalized.SummaryRules = summaryRules;
            return normalized;
        }

        private static SummaryTextOptions NormalizeSummaryText(SummaryTextOptions? summaryText)
        {
            if (summaryText == null)
            {
                return new SummaryTextOptions();
            }

            var nullProperties = SummaryTextStringProperties.Where(p => p.GetValue(summaryText) == null).ToList();
            if (nullProperties.Count == 0)
            {
                return summaryText;
            }

            var normalized = summaryText.ShallowCopy();
            foreach (var property in nullProperties)
            {
                property.SetValue(normalized, property.GetValue(DefaultSummaryText));
            }

            return normalized;
        }

        private static SummaryRulesOptions NormalizeSummaryRules(SummaryRulesOptions? summaryRules)
        {
            if (summaryRules == null)
            {
                return new SummaryRulesOptions();
            }

            if (summaryRules.TransferStationGroups != null)
            {
                return summaryRules;
            }

            var normalized = summaryRules.ShallowCopy();
            normalized.TransferStationGroups = new SummaryRulesOptions().TransferStationGroups;
            return normalized;
        }
    }
}
