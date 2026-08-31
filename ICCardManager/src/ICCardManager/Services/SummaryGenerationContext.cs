using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly List<HashSet<string>> _transferStationGroups;

        private SummaryGenerationContext(
            OrganizationOptions options,
            List<HashSet<string>> transferStationGroups,
            DepartmentType departmentType)
        {
            Options = options;
            _transferStationGroups = transferStationGroups;
            DepartmentType = departmentType;
        }

        /// <summary>この世代の組織固有設定</summary>
        public OrganizationOptions Options { get; }

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
        public static SummaryGenerationContext Create(OrganizationOptions options)
        {
            var effective = options ?? new OrganizationOptions();
            return new SummaryGenerationContext(
                effective, BuildTransferStationGroups(effective), DepartmentType.MayorOffice);
        }

        /// <summary>
        /// 部署種別だけを差し替えた新しい世代を返す（Issue #1975）
        /// </summary>
        /// <remarks>
        /// 同一視グループの再構築（<see cref="BuildTransferStationGroups"/>）を伴わないため、
        /// 生成の入口（<see cref="SummaryGenerator.CaptureContext"/>）から毎回呼んでも
        /// 走査コストは掛からない。<see cref="Options"/> はそのまま共有する
        /// （不変オブジェクトとして扱うため、共有しても中間状態は生じない）。
        /// </remarks>
        public SummaryGenerationContext WithDepartmentType(DepartmentType departmentType)
            => departmentType == DepartmentType
                ? this
                : new SummaryGenerationContext(Options, _transferStationGroups, departmentType);

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
        /// </remarks>
        public SummaryGenerationContext WithTransferStationGroups(
            IEnumerable<IEnumerable<string>> groups)
        {
            var newOptions = new OrganizationOptions
            {
                SummaryText = Options.SummaryText,
                AreaPriority = Options.AreaPriority,
                ReportLayout = Options.ReportLayout,
                TemplateMapping = Options.TemplateMapping,
                SummaryRules = new SummaryRulesOptions
                {
                    EnableRoundTripDetection = Options.SummaryRules.EnableRoundTripDetection,
                    EnableTransferConsolidation = Options.SummaryRules.EnableTransferConsolidation,
                    TransferStationGroups = (groups ?? Enumerable.Empty<IEnumerable<string>>())
                        .Where(g => g != null)
                        .Select(g => g.ToList())
                        .ToList()
                }
            };

            return Create(newOptions);
        }

        /// <summary>
        /// この世代の同一視グループのコピーを返す（観測用、Issue #1905）
        /// </summary>
        public List<List<string>> GetTransferStationGroups()
            => Options.SummaryRules.TransferStationGroups
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
        public bool AreTransferStations(string station1, string station2)
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
        public string CanonicalStation(string station)
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
    }
}
