using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1919: 摘要生成の各段階が「引数で受け取った世代」だけを見ることの静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 挙動テスト（<see cref="SummaryGeneratorGenerationSnapshotTests"/>）は既存の段階しか
/// 通らないため、段階が増えたときの追随漏れ（新しい段階が静的状態を直接読む）を
/// 検出できない（error-messages.md #1764「経路ごとの個別テストで守り切れないと分かったら
/// ソーステキストの静的検査へ移す」）。
/// </para>
/// <para>
/// 検査は<b>対</b>で表明する。「禁止された形（生成の内部から静的状態を読む）の不在」だけを
/// 見ると、世代を引数で渡す形を丸ごと撤去した実装でも緑になるため、
/// 「正しい形（各段階が世代を引数で受け取っている）の存在」も併せて固定する。
/// 検査対象はファイル名の列挙ではなく<b>コードの形</b>（世代を引数に取るメソッド）から
/// 導出するので、段階が増えても自動的に検査へ入る（development-conventions.md #1786）。
/// </para>
/// </remarks>
public class SummaryGenerationSnapshotConventionTests
{
    /// <summary>世代を引数で受け取っているメソッドの目印</summary>
    private const string ContextParameter = "SummaryGenerationContext context";

    /// <summary>
    /// 生成の内部から読んではならない静的状態（現在の世代／そこから導出する文言）
    /// </summary>
    private static readonly string[] ForbiddenTokenPatterns =
    {
        @"(?<![A-Za-z0-9_])_context(?![A-Za-z0-9_])",
        @"(?<![A-Za-z0-9_])CurrentOptions(?![A-Za-z0-9_])",
        @"(?<![A-Za-z0-9_])BusLabel(?![A-Za-z0-9_])",
        @"(?<![A-Za-z0-9_])BusPlaceholder(?![A-Za-z0-9_])",
        // Issue #1975: 部署種別も運用中に差し替わる（設定画面 F5）。段階が直接読むと
        // 同じ生成の中で「役務費によりチャージ」と「旅費によりチャージ」が混ざる
        @"(?<![A-Za-z0-9_])_departmentType(?![A-Za-z0-9_])",
    };

    /// <summary>
    /// 世代を引数で受け取るメソッドの本体が、現在の静的状態を読まないこと。
    /// </summary>
    [Fact]
    public void 生成の各段階が静的状態を直接読まないこと()
    {
        var methods = ExtractContextTakingMethods(ReadSummaryGeneratorSource());

        var violations = methods
            .Where(m => ForbiddenTokenPatterns.Any(p => Regex.IsMatch(m.Body, p)))
            .Select(m => m.Name)
            .ToList();

        violations.Should().BeEmpty(
            "摘要生成の各段階は引数で受け取った世代（context）だけを見ること。" +
            "静的状態を読むと 1 回の生成の途中で世代が入れ替わり、" +
            "往復の突合（DetectRoundTrips ↔ GetRemainingRoutes）が壊れる（Issue #1919）");
    }

    /// <summary>
    /// 対の検査: 同一視を参照する段階が、実際に世代を引数で受け取っていること。
    /// </summary>
    [Fact]
    public void 同一視を参照する段階が世代を引数で受け取っていること()
    {
        var methods = ExtractContextTakingMethods(ReadSummaryGeneratorSource());
        var names = methods.Select(m => m.Name).ToList();

        // 突合が成立する前提となる 3 段階は必ず同じ世代を受け取る
        names.Should().Contain("ConsolidateRoutes");
        names.Should().Contain("DetectRoundTrips");
        names.Should().Contain("GetRemainingRoutes");

        // 入口から 3 段階までを繋ぐ経路も世代を持ち回っている
        names.Should().Contain("GenerateUsageSummary");
        names.Should().Contain("BuildRouteSummary");
        names.Should().Contain("EvaluateCandidate");

        // 検査が縮んで空振りしていないこと（現状 17 メソッド）
        methods.Should().HaveCountGreaterOrEqualTo(15);
    }

    /// <summary>
    /// 運用中に差し替わる部署種別（Issue #1975）を読んでよい場所を列挙して固定する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上の「生成の各段階が静的状態を直接読まないこと」は<b>世代を引数に取るメソッドだけ</b>を
    /// 走査するため、世代を捕捉する側である <c>Generate</c> / <c>GenerateByDate</c> は対象外になる。
    /// ところが #1975 の欠陥が実際に住んでいたのはその 2 つで、
    /// <c>Generate</c> の「チャージのみ」分岐が <c>_departmentType</c> を直接読んでいた。
    /// <b>ガードが見ていない場所に欠陥がある</b>状態だったので、フィールドの参照箇所そのものを数える。
    /// </para>
    /// <para>
    /// 読んでよいのは「値を受け取る」コンストラクタ、「差し替える」<c>ApplyDepartmentType</c>、
    /// 「世代へ畳み込む」<c>CaptureContext</c> の 3 か所（宣言を含めて 4 回）だけ。
    /// </para>
    /// </remarks>
    [Fact]
    public void 部署種別を読む箇所が捕捉の入口に限られていること()
    {
        var source = TestSourceInspection.ToCodeOnly(ReadSummaryGeneratorSource());

        var references = Regex.Matches(
            source, @"(?<![A-Za-z0-9_])_departmentType(?![A-Za-z0-9_])").Count;

        // 宣言 1 ＋ コンストラクタでの代入 1 ＋ ApplyDepartmentType での代入 1 ＋ CaptureContext での読み取り 1
        references.Should().Be(4,
            "部署種別は生成の入口（CaptureContext）で世代へ畳み込み、以降は捕捉済みの値だけを見ること。" +
            "生成の途中で読むと、1 回の生成の中で「役務費によりチャージ」と「旅費によりチャージ」が" +
            "混ざる（Issue #1975 / #1919）");

        // 対の表明: 差し替えの手段が実在すること（フィールドごと消した実装で緑にしない）
        source.Should().Contain("ApplyDepartmentType",
            "運用中に差し替える手段（Issue #1975）が残っていること");
        source.Should().Contain("WithDepartmentType",
            "捕捉時に世代へ畳み込む手段（Issue #1975）が残っていること");
    }

    /// <summary>
    /// 生成の入口が世代を 1 回だけ捕捉していること。
    /// </summary>
    /// <remarks>
    /// 捕捉が段階ごとに散ると、世代を引数で持ち回っていても
    /// 「入口ごとに違う世代」を混ぜられる余地が残る。
    /// </remarks>
    [Fact]
    public void 生成の入口だけが世代を捕捉していること()
    {
        var source = TestSourceInspection.ToCodeOnly(ReadSummaryGeneratorSource());

        var captureCalls = Regex.Matches(source, @"(?<![A-Za-z0-9_])CaptureContext\(\)").Count;

        // 定義（=> _context）1 か所 ＋ 入口 2 か所（Generate / GenerateByDate）
        captureCalls.Should().Be(3,
            "世代の捕捉は生成の入口（Generate / GenerateByDate）に限ること（Issue #1919）");
    }

    /// <summary>
    /// 抽出ロジック自体を既知のサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データ（本番ソース）が変わっても検査が空振りしないことを保証する
    /// （development-conventions.md #1786「空振り検出を各対象が非空であることで書かない」）。
    /// </remarks>
    [Fact]
    public void 抽出ロジックがサンプル入力で期待どおり動くこと()
    {
        const string sample = @"
class C
{
    private string WithBody(List<(string A, string B)> routes, SummaryGenerationContext context)
    {
        return Helper(routes, context);
    }

    private static string ExpressionBodied(string s, SummaryGenerationContext context)
        => context.Options.SummaryText.BusLabel;

    private string WithoutContext(List<string> items)
    {
        return _context.Options.SummaryText.BusLabel;
    }
}";

        var methods = ExtractContextTakingMethods(sample);

        // 波括弧本体を持つメソッドだけを対象にし、式形式の小さなヘルパーは対象外
        methods.Select(m => m.Name).Should().Equal("WithBody");
        methods[0].Body.Should().Contain("Helper(routes, context)");
        methods[0].Body.Should().NotContain("WithoutContext");
    }

    /// <summary>
    /// Issue #2035: 生成の入口と各段階が、静的状態を読むメンバーを<b>呼び出し経由でも</b>使わないこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上の「生成の各段階が静的状態を直接読まないこと」は、世代を引数に取るメソッドの本体に
    /// <c>_context</c> などの<b>字句</b>が現れるかしか見ていない。そのため次の 2 つを検出できなかった。
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// 世代を捕捉する入口（<c>GenerateByDate</c>）自身が静的状態を読む形。旧実装はポイント還元の行で
    /// <c>GetPointRedemptionSummary()</c>（内部で <c>CurrentOptions</c> を読む）を呼んでおり、
    /// 同じ生成のチャージの行は捕捉した世代から引いていた。
    /// </description></item>
    /// <item><description>
    /// 字句としては現れず、静的状態を読むメンバーを<b>呼び出す</b>形（上の 1 もこの形）。
    /// </description></item>
    /// </list>
    /// <para>
    /// そこで「静的状態を読むメンバー」を字句から種として求め、呼び出し関係で推移的に広げてから、
    /// 生成パイプライン（世代を引数に取るメンバー ＋ 世代を捕捉する入口）との交わりが空であることを表明する。
    /// 同名のオーバーロード（<c>FormatBusSummary(string)</c> は静的状態を読み、
    /// <c>FormatBusSummary(string, context)</c> は読まない）は引数の数で区別する。
    /// 補間文字列の補間式（<c>$"{GetPointRedemptionSummary()}"</c>）も走査の対象に含める。
    /// </para>
    /// </remarks>
    [Fact]
    public void 生成パイプラインが静的状態を読むメンバーを呼び出し経由でも使わないこと()
    {
        var analysis = AnalyzeStaticStateReaders(ReadSummaryGeneratorSource(), "SummaryGenerator");

        analysis.Violations.Should().BeEmpty(
            "摘要生成は入口で捕捉した世代（context）だけを見ること。静的状態を読むメンバーを呼ぶと、" +
            "1 回の生成の中で読む先が分かれ、組織設定・同一視グループの差し替えが生成の途中に混ざる" +
            $"（Issue #1919 / #2035）。経路: {string.Join(" / ", analysis.Violations.Select(v => v.Path))}");
    }

    /// <summary>
    /// 対の検査: 上の検査が実際に「静的状態を読むメンバー」と「生成パイプライン」を拾えていること。
    /// </summary>
    /// <remarks>
    /// 抽出が縮んで 0 件になると、上の検査は<b>永久に緑</b>になる（空振り）。
    /// 既知のメンバーが両方の集合に入っていることを固定する。
    /// </remarks>
    [Fact]
    public void 静的状態を読むメンバーと生成パイプラインを抽出できていること()
    {
        var analysis = AnalyzeStaticStateReaders(ReadSummaryGeneratorSource(), "SummaryGenerator");

        // 静的状態を読むメンバー（直接／推移的）
        analysis.Readers.Should().Contain(new[]
        {
            "CurrentOptions", "BusLabel", "BusPlaceholder", "FormatBusSummary/1",
            "GetPointRedemptionSummary/0", "GetChargeSummary/1", "ExtractBusStopBlocks/1"
        });
        analysis.Readers.Should().NotContain(new[] { "FormatBusSummary/2", "CaptureContext/0" },
            "世代を引数に取るオーバーロードと、捕捉の入口そのものは静的状態の読み手に数えない");

        // 生成パイプライン（入口 ＋ 世代を引数に取るメンバー）
        analysis.Pipeline.Should().Contain(new[]
        {
            "Generate/1", "GenerateByDate/1", "GenerateUsageSummary/2",
            "ConsolidateRoutes/4", "DetectRoundTrips/2", "GetRemainingRoutes/3"
        });
    }

    /// <summary>
    /// 推移的な抽出ロジックを既知のサンプル入力で固定する。
    /// </summary>
    [Fact]
    public void 推移的な抽出ロジックがサンプル入力で期待どおり動くこと()
    {
        const string sample = @"
namespace N
{
    public class SummaryGenerator
    {
        private static SummaryGenerationContext _context = SummaryGenerationContext.Create(null);
        private static OrganizationOptions CurrentOptions => _context.Options;
        private static readonly char Open = '（';

        public static string Label() => CurrentOptions.SummaryText.RailwayLabel;
        public static string Format(string s) => Format(s, _context);
        private static string Format(string s, SummaryGenerationContext context) => s + Open;
        private static string Indirect() { return Label(); }

        internal virtual SummaryGenerationContext CaptureContext() => _context;

        public string GoodEntry(List<(string A, string B)> items)
        {
            var context = CaptureContext();
            return Format(""x{}"", context);
        }

        public string BadEntry(Dictionary<string, int> items)
        {
            var context = CaptureContext();
            return Indirect();
        }

        private string BadStage(string s, SummaryGenerationContext context)
        {
            return $""{Label()}（{s}）"";
        }

        private string GoodStage(string s, SummaryGenerationContext context)
        {
            // Label() はコメントなので対象外
            return context.Options.SummaryText.RailwayLabel + ""Label()"";
        }

        public static string Amb(string s) => CurrentOptions.SummaryText.RailwayLabel + s;
        private static string Amb(string s, SummaryGenerationContext? context = null) => s;

        private string AmbiguousStage(SummaryGenerationContext? context)
        {
            return Amb(""x"");
        }

        private string RecaptureStage(string s, SummaryGenerationContext context)
        {
            var again = CaptureContext();
            return s;
        }

        private string StageCallingEntry(SummaryGenerationContext context)
        {
            return GoodEntry(null);
        }

        private sealed class Inner
        {
            public string Run(SummaryGenerationContext context) => string.Empty;
        }
    }
}";

        var analysis = AnalyzeStaticStateReaders(sample, "SummaryGenerator");

        analysis.Readers.Should().BeEquivalentTo(new[]
        {
            "_context", "CurrentOptions", "Label/0", "Format/1", "Indirect/0", "Amb/1"
        });
        // 世代を引数に取るオーバーロード（Format/2・Amb/2）もパイプラインの一段として扱う。
        // null 許容の注釈（SummaryGenerationContext?）も世代の仮引数に数える
        analysis.Pipeline.Should().BeEquivalentTo(new[]
        {
            "Format/2", "GoodEntry/1", "BadEntry/1", "BadStage/2", "GoodStage/2",
            "Amb/2", "AmbiguousStage/1", "RecaptureStage/2", "StageCallingEntry/1"
        });
        analysis.Violations.Select(v => v.Member).Should().BeEquivalentTo(new[]
        {
            "BadEntry/1", "BadStage/2", "AmbiguousStage/1", "RecaptureStage/2", "StageCallingEntry/1", "Inner"
        });
        analysis.Violations.Single(v => v.Member == "RecaptureStage/2").Path
            .Should().Be("RecaptureStage/2 → CaptureContext/0（世代の再捕捉）");
        analysis.Violations.Single(v => v.Member == "StageCallingEntry/1").Path
            .Should().Be("StageCallingEntry/1 → GoodEntry/1（世代の再捕捉）");
        analysis.Violations.Single(v => v.Member == "BadEntry/1").Path
            .Should().Be("BadEntry/1 → Indirect/0 → Label/0 → CurrentOptions → _context");
    }

    private static string ReadSummaryGeneratorSource()
        => File.ReadAllText(Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Services", "SummaryGenerator.cs"));

    #region 静的状態の読み手の推移的な抽出（Issue #2035）

    /// <summary>世代を受け取る仮引数（null 許容の注釈 <c>SummaryGenerationContext?</c> も含める）</summary>
    private const string ContextParameterPattern = @"\bSummaryGenerationContext\??\s+[A-Za-z_]";

    /// <summary>静的状態そのもの（読み手の種）</summary>
    private static readonly string[] StateMemberNames = { "_context" };

    /// <summary>
    /// 静的状態を読むが、読み手に数えないメンバー（世代を捕捉する入口そのもの）
    /// </summary>
    private const string CaptureMethodName = "CaptureContext";

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "private", "public", "internal", "protected", "static", "virtual", "override", "readonly",
        "async", "sealed", "abstract", "new", "unsafe", "extern", "partial", "const"
    };

    private sealed class ClassMember
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>メソッドなら仮引数の数。プロパティ・フィールド・入れ子の型は null</summary>
        public int? ParameterCount { get; init; }

        public int RequiredParameterCount { get; init; }

        public string Parameters { get; init; } = string.Empty;

        public string Body { get; init; } = string.Empty;

        public string Key => ParameterCount.HasValue ? $"{Name}/{ParameterCount}" : Name;
    }

    private sealed class StaticStateAnalysis
    {
        public IReadOnlyCollection<string> Readers { get; init; } = Array.Empty<string>();

        public IReadOnlyCollection<string> Pipeline { get; init; } = Array.Empty<string>();

        public IReadOnlyList<(string Member, string Path)> Violations { get; init; }
            = Array.Empty<(string, string)>();
    }

    /// <summary>
    /// 静的状態を読むメンバーを推移的に求め、生成パイプラインのうちそれを参照するものを返す。
    /// </summary>
    private static StaticStateAnalysis AnalyzeStaticStateReaders(string source, string className)
    {
        // 補間式の中身は残す（$"{GetPointRedemptionSummary()}" を見逃さない）
        var code = TestSourceInspection.ToCodeOnlyPreservingLines(source, preserveInterpolationHoles: true);
        var members = SplitClassMembers(code, className);

        var pipeline = members
            .Where(m => m.ParameterCount.HasValue
                && (Regex.IsMatch(m.Parameters, ContextParameterPattern)
                    || (m.Name != CaptureMethodName
                        && Regex.IsMatch(m.Body, $@"(?<![A-Za-z0-9_.]){CaptureMethodName}\s*\(\s*\)"))))
            .ToList();
        var pipelineKeys = new HashSet<string>(pipeline.Select(m => m.Key), StringComparer.Ordinal);

        // 読み手 → 経由した読み手（経路の復元用。種は null）
        var readers = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var member in members.Where(m => StateMemberNames.Contains(m.Name)))
        {
            readers[member.Key] = null;
        }

        var candidates = members
            .Where(m => !pipelineKeys.Contains(m.Key) && m.Name != CaptureMethodName)
            .ToList();

        bool changed;
        do
        {
            changed = false;
            foreach (var member in candidates.Where(m => !readers.ContainsKey(m.Key)))
            {
                var via = FindFirstReaderReference(member, members, readers.Keys);
                if (via != null)
                {
                    readers[member.Key] = via;
                    changed = true;
                }
            }
        }
        while (changed);

        var violations = new List<(string, string)>();
        foreach (var member in pipeline)
        {
            var via = FindFirstReaderReference(member, members, readers.Keys);
            if (via == null)
            {
                continue;
            }

            var path = new List<string> { member.Key };
            for (var current = via; current != null; current = readers[current])
            {
                path.Add(current);
            }

            violations.Add((member.Key, string.Join(" → ", path)));
        }

        // 世代の再捕捉: 段階が CaptureContext() や入口（Generate 等）を呼ぶと、1 回の生成が 2 つの世代を見る
        var entries = pipeline
            .Where(m => Regex.IsMatch(m.Body, $@"(?<![A-Za-z0-9_.]){CaptureMethodName}\s*\(\s*\)"))
            .ToList();
        foreach (var member in pipeline)
        {
            if (Regex.IsMatch(member.Parameters, ContextParameterPattern)
                && Regex.IsMatch(member.Body, $@"(?<![A-Za-z0-9_.]){CaptureMethodName}\s*\(\s*\)"))
            {
                violations.Add((member.Key, $"{member.Key} → {CaptureMethodName}/0（世代の再捕捉）"));
            }

            foreach (var entry in entries.Where(e => e.Key != member.Key))
            {
                var pattern = new Regex(
                    $@"(?:(?<![A-Za-z0-9_.])|(?<=\bSummaryGenerator\.)|(?<=\bthis\.)){Regex.Escape(entry.Name)}");
                if (TestSourceInspection.ExtractInvocationArguments(member.Body, pattern)
                    .Any(c => MatchesArity(entry, c.Arguments.Count)))
                {
                    violations.Add((member.Key, $"{member.Key} → {entry.Key}（世代の再捕捉）"));
                }
            }
        }

        // 入れ子の型の中のメソッドはメンバー分割の対象外。世代を引数に取るメソッドが現れたら検査を広げる合図にする
        foreach (var nested in members.Where(m => !m.ParameterCount.HasValue
            && Regex.IsMatch(m.Body, ContextParameterPattern)))
        {
            violations.Add((nested.Key, $"{nested.Key}（入れ子の型に世代を引数に取るメソッドがある。検査の対象外なので分割を広げること）"));
        }

        return new StaticStateAnalysis
        {
            Readers = readers.Keys.ToList(),
            Pipeline = pipelineKeys.ToList(),
            Violations = violations
        };
    }

    /// <summary>
    /// メンバー本体が参照している読み手のうち、本体で最初に現れるものを返す。
    /// </summary>
    private static string? FindFirstReaderReference(
        ClassMember member, IReadOnlyList<ClassMember> members, IEnumerable<string> readerKeys)
    {
        var readerSet = new HashSet<string>(readerKeys, StringComparer.Ordinal);
        (int Index, string Key)? first = null;

        foreach (var group in members.Where(m => readerSet.Contains(m.Key)).GroupBy(m => m.Name))
        {
            // 自クラスのメンバーとして参照される形だけを数える（context.Options.X のような他の型のメンバーは除く）
            var namePattern = $@"(?:(?<![A-Za-z0-9_.])|(?<=\bSummaryGenerator\.)|(?<=\bthis\.)){Regex.Escape(group.Key)}";

            foreach (var reader in group)
            {
                IEnumerable<int> hits;
                if (reader.ParameterCount.HasValue)
                {
                    // 呼び出し: 引数の数が合うオーバーロードに限る。引数の数が複数のオーバーロードに合う
                    // （既定値付き・params）呼び出しは、どれが選ばれるか区別できないので読み手の側へ倒す（fail-open にしない）
                    var calls = TestSourceInspection
                        .ExtractInvocationArguments(member.Body, new Regex(namePattern))
                        .Where(c => MatchesArity(reader, c.Arguments.Count))
                        .Select(c => c.Index);

                    // メソッドグループとしての参照（.Where(Name)）は、どのオーバーロードか区別できないので数える
                    var groups = Regex.Matches(member.Body, namePattern + @"(?![A-Za-z0-9_]|\s*\()")
                        .Cast<Match>().Select(m => m.Index);
                    hits = calls.Concat(groups);
                }
                else
                {
                    hits = Regex.Matches(member.Body, namePattern + @"(?![A-Za-z0-9_])")
                        .Cast<Match>().Select(m => m.Index);
                }

                foreach (var index in hits)
                {
                    if (first == null || index < first.Value.Index)
                    {
                        first = (index, reader.Key);
                    }
                }
            }
        }

        return first?.Key;
    }

    private static bool MatchesArity(ClassMember method, int argumentCount)
        => argumentCount >= method.RequiredParameterCount && argumentCount <= method.ParameterCount;

    /// <summary>
    /// クラス本体を直下のメンバー（メソッド・プロパティ・フィールド・入れ子の型）へ分割する。
    /// </summary>
    /// <remarks>
    /// 入力はコメント・文字列リテラルを除去済みであること（波括弧の対応を狂わせないため）。
    /// </remarks>
    private static IReadOnlyList<ClassMember> SplitClassMembers(string code, string className)
    {
        var classMatch = Regex.Match(code, $@"\bclass\s+{Regex.Escape(className)}\b");
        if (!classMatch.Success)
        {
            throw new InvalidOperationException($"class {className} が見つからない。抽出ロジックを確認すること。");
        }

        var bodyStart = code.IndexOf('{', classMatch.Index);
        var bodyEnd = ExtractBracedBlock(code, bodyStart).Length + bodyStart - 1;

        var members = new List<ClassMember>();
        var chunkStart = bodyStart + 1;
        var braceDepth = 0;
        var parenDepth = 0;
        var sawInitializer = false;

        for (var i = chunkStart; i < bodyEnd; i++)
        {
            var c = code[i];
            if (c == '(')
            {
                parenDepth++;
            }
            else if (c == ')')
            {
                parenDepth--;
            }
            else if (c == '=' && braceDepth == 0 && parenDepth == 0)
            {
                // 初期化子・式形式の本体（= / =>）の後の波括弧は、メンバーの終わりではない
                sawInitializer = true;
            }
            else if (c == '{')
            {
                braceDepth++;
            }
            else if (c == '}')
            {
                braceDepth--;
                if (braceDepth == 0 && parenDepth == 0 && !sawInitializer)
                {
                    AddMember(members, code.Substring(chunkStart, i - chunkStart + 1));
                    chunkStart = i + 1;
                }
            }
            else if (c == ';' && braceDepth == 0 && parenDepth == 0)
            {
                AddMember(members, code.Substring(chunkStart, i - chunkStart + 1));
                chunkStart = i + 1;
                sawInitializer = false;
            }
        }

        return members;
    }

    private static void AddMember(List<ClassMember> members, string chunk)
    {
        var text = Regex.Replace(chunk, @"^\s*(\[[^\]]*\]\s*)*", string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var angleDepth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '=' && angleDepth == 0)
            {
                // プロパティ（=>）・フィールド（=）
                AddNamed(members, text.Substring(0, i), text.Substring(i + (next == '>' ? 2 : 1)));
                return;
            }

            if (c == '<')
            {
                angleDepth++;
            }
            else if (c == '>')
            {
                angleDepth--;
            }
            else if ((c == '{' || c == ';') && angleDepth == 0)
            {
                // ブロック形式のプロパティ・入れ子の型・初期化子の無いフィールド
                AddNamed(members, text.Substring(0, i), text.Substring(i));
                return;
            }
            else if (c == '(' && angleDepth == 0)
            {
                var close = FindMatchingParenthesis(text, i);
                var name = LastIdentifier(text.Substring(0, i));
                if (name == null || Modifiers.Contains(name))
                {
                    // タプル型の戻り値（(string A, int B) Foo(...)）。型として読み飛ばす
                    i = close;
                    continue;
                }

                var parameters = text.Substring(i + 1, close - i - 1);
                var segments = SplitTopLevel(parameters);
                members.Add(new ClassMember
                {
                    Name = name,
                    ParameterCount = segments.Count,
                    RequiredParameterCount = segments.Count(s => !Regex.IsMatch(s, @"(?<![=!<>])=(?!=)")),
                    Parameters = parameters,
                    Body = text.Substring(close + 1)
                });
                return;
            }
        }
    }

    private static void AddNamed(List<ClassMember> members, string header, string body)
    {
        var name = LastIdentifier(header);
        if (name != null && !Modifiers.Contains(name))
        {
            members.Add(new ClassMember { Name = name, Body = body });
        }
    }

    private static string? LastIdentifier(string header)
    {
        // ジェネリックの型引数（Foo<T>）は名前に含めない
        var trimmed = Regex.Replace(header, @"<[^<>()]*>\s*$", string.Empty);
        var match = Regex.Match(trimmed, @"([A-Za-z_][A-Za-z0-9_]*)\s*$");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int FindMatchingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("丸括弧が対応していない。抽出ロジックを確認すること。");
    }

    private static IReadOnlyList<string> SplitTopLevel(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return Array.Empty<string>();
        }

        var segments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var c = parameters[i];
            if (c == '(' || c == '<' || c == '[')
            {
                depth++;
            }
            else if (c == ')' || c == '>' || c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                segments.Add(parameters.Substring(start, i - start));
                start = i + 1;
            }
        }

        segments.Add(parameters.Substring(start));
        return segments;
    }

    #endregion

    /// <summary>
    /// 世代を引数で受け取り、波括弧の本体を持つメソッドを抽出する。
    /// </summary>
    private static IReadOnlyList<(string Name, string Body)> ExtractContextTakingMethods(string source)
    {
        var code = TestSourceInspection.ToCodeOnly(source);
        var results = new List<(string Name, string Body)>();

        var index = 0;
        while (true)
        {
            var found = code.IndexOf(ContextParameter, index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }
            index = found + ContextParameter.Length;

            var brace = code.IndexOf('{', index);
            if (brace < 0)
            {
                break;
            }

            // 本体が波括弧でないもの（式形式・宣言のみ）は対象外。
            // 引数リストの終わりから最初の波括弧までに ";" があれば本体ではない
            if (code.IndexOf(';', index) >= 0 && code.IndexOf(';', index) < brace)
            {
                continue;
            }

            var name = ExtractMethodName(code, found);
            results.Add((name, ExtractBracedBlock(code, brace)));
        }

        return results;
    }

    /// <summary>
    /// 引数の出現位置から遡ってメソッド名を取り出す。
    /// </summary>
    private static string ExtractMethodName(string code, int parameterIndex)
    {
        var head = code.Substring(0, parameterIndex);
        var matches = Regex.Matches(head, @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(");
        return matches.Count == 0
            ? "(unknown)"
            : matches[matches.Count - 1].Groups["name"].Value;
    }

    private static string ExtractBracedBlock(string code, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return code.Substring(openBrace, i - openBrace + 1);
                }
            }
        }

        throw new InvalidOperationException("波括弧が対応していない。抽出ロジックを確認すること。");
    }
}
