using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1307: <see cref="SummaryGeneratorCollection"/> の設定および
/// 対象テストクラスへの <c>[Collection]</c> 属性付与を検証する回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ICCardManager.Services.SummaryGenerator"/> の静的フィールド <c>_options</c> /
/// 設定の世代（<c>_context</c>）を変更するテスト（<c>Configure</c> / <c>ResetToDefaults</c> 呼び出し）と、
/// その影響を受けるテストは、必ず <see cref="SummaryGeneratorCollection.Name"/> Collection に
/// 属している必要がある。本テストは将来 Collection 属性の付与漏れを静的に検出する。
/// </para>
/// <para>
/// ランタイム上の並列実行抑止そのもの（xUnit Scheduler の挙動）は単体テストでは検証困難なため、
/// 属性の付与・プロパティ値・命名の整合性を検証することで代替する。
/// </para>
/// <para>
/// Issue #2101: 対象のテストクラスを手で列挙していた頃は、静的状態を書き換える 7 クラス
/// （<c>SummaryGeneratorBusTextTests</c> ほか）が一覧から漏れていた。属性はたまたま付いていたが、
/// 一覧に無い以上は外されても検出できなかった。対象は<b>ソースツリーから導出する</b> —
/// 「静的状態を書き換える API」を <c>SummaryGenerator</c> の本体から求め、それを（直接、または
/// 書き換える本番の型を生成して間接に）呼ぶテストクラスすべてを検査する
/// （<c>.claude/rules/development-conventions.md</c> #1786「ガードを書くときは経路を列挙する」）。
/// あわせて、属性だけでなく <b>Dispose で <c>ResetToDefaults</c> を呼んで既定値へ戻していること</b>を
/// ソース上で確かめる（IDisposable の実装だけでは「戻している」ことにならない）。
/// </para>
/// </remarks>
public class SummaryGeneratorCollectionConfigurationTests
{
    /// <summary>既定値へ戻す API。Dispose での復元はこの呼び出しで判定する。</summary>
    private const string ResetMethodName = "ResetToDefaults";

    /// <summary>
    /// <see cref="SummaryGeneratorCollection"/> 自身が <c>CollectionDefinition</c> 属性を持ち、
    /// <c>DisableParallelization = true</c> が設定されていること。
    /// </summary>
    /// <remarks>
    /// xUnit の <c>CollectionDefinitionAttribute</c> / <c>CollectionAttribute</c> は
    /// <c>Name</c> プロパティを公開していないため、<see cref="CustomAttributeData"/> 経由で
    /// コンストラクタ引数として渡された名前を取得する。
    /// </remarks>
    [Fact]
    public void Collection定義が並列実行を無効化していること()
    {
        // Arrange
        var collectionType = typeof(SummaryGeneratorCollection);
        var attributeData = CustomAttributeData.GetCustomAttributes(collectionType)
            .FirstOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute));

        // Assert
        attributeData.Should().NotBeNull(
            "SummaryGeneratorCollection は CollectionDefinition 属性を持つ必要がある");
        var name = attributeData!.ConstructorArguments[0].Value as string;
        name.Should().Be(SummaryGeneratorCollection.Name);
        var disableParallelization = attributeData.NamedArguments
            .FirstOrDefault(a => a.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization))
            .TypedValue.Value;
        disableParallelization.Should().Be(true,
            "DisableParallelization=true でないと静的状態が並列実行で汚染される");
    }

    /// <summary>
    /// 静的状態を書き換えるテストクラスが <see cref="SummaryGeneratorCollection"/> に属していること。
    /// </summary>
    [Fact]
    public void 静的状態を書き換えるテストクラスがすべてCollectionに属していること()
    {
        var violations = new List<string>();

        foreach (var testClass in DeriveMutatingTestClasses())
        {
            var attributeData = CustomAttributeData.GetCustomAttributes(testClass)
                .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute));
            var name = attributeData?.ConstructorArguments[0].Value as string;

            if (name != SummaryGeneratorCollection.Name)
            {
                violations.Add($"{testClass.Name}（Collection: {name ?? "なし"}）");
            }
        }

        violations.Should().BeEmpty(
            "SummaryGenerator の静的状態を書き換えるテストクラスは " +
            "[Collection(SummaryGeneratorCollection.Name)] を持つ必要がある（並列実行で他のテストの設定を上書きする）");
    }

    /// <summary>
    /// 静的状態を書き換えるテストクラスが、Dispose で既定値へ戻していること。
    /// </summary>
    /// <remarks>
    /// IDisposable を実装しているだけでは「戻している」ことにならない。Dispose の本体が
    /// <c>SummaryGenerator.ResetToDefaults()</c> を呼んでいることをソース上で確かめる。
    /// 戻さないと、同じ Collection の後続テストが書き換え後の設定のまま走る。
    /// </remarks>
    [Fact]
    public void 静的状態を書き換えるテストクラスはDisposeで既定値へ戻していること()
    {
        var violations = new List<string>();

        foreach (var testClass in DeriveMutatingTestClasses())
        {
            if (!typeof(IDisposable).IsAssignableFrom(testClass))
            {
                violations.Add($"{testClass.Name}（IDisposable を実装していない）");
                continue;
            }

            // partial クラスや同名の入れ子クラスがあり得るため、宣言を含むすべてのファイルを見る
            var restores = LoadTestSources()
                .Where(s => s.Classes.Contains(testClass.Name))
                .Any(s => RestoresDefaultsOnDispose(ExtractClassBody(s.Code, testClass.Name)));
            if (!restores)
            {
                violations.Add($"{testClass.Name}（Dispose が SummaryGenerator.{ResetMethodName}() を呼んでいない）");
            }
        }

        violations.Should().BeEmpty(
            $"静的状態を書き換えるテストクラスは Dispose で SummaryGenerator.{ResetMethodName}() を呼び、" +
            "後続のテストへ書き換えた設定を持ち越さないこと");
    }

    /// <summary>
    /// 導出の空振り防止: 書き換える API・型・テストクラスが既知のものを拾えていること。
    /// </summary>
    /// <remarks>
    /// 導出が 0 件へ縮んでも上の 2 件は緑になる（検査対象が無いため）。既知の書き換えクラスは
    /// 下限として表明する（一覧に無いクラスを検査から外す意味ではない — 検査対象は導出した全件）。
    /// </remarks>
    [Fact]
    public void 静的状態を書き換える手段とテストクラスの導出が既知のものを拾うこと()
    {
        var mutators = DeriveProductionMutators();

        mutators.Methods.Should().Contain(new[] { "Configure", "ApplyTransferStationGroups", ResetMethodName },
            "静的な世代（_context）へ代入する API");
        mutators.Methods.Should().NotContain("GetTransferStationGroups", "読むだけの API は書き換えではない（対の表明）");
        mutators.ConstructorArities.Should().Contain(2, "設定を受け取るコンストラクタは Configure を呼ぶ");
        mutators.ConstructorArities.Should().NotContain(1, "部署種別だけのコンストラクタは静的状態に触れない（対の表明）");
        mutators.Types.Should().Contain("TransferStationGroupService",
            "SaveGroupsAsync が ApplyTransferStationGroups で静的状態を書き換える");

        var derived = DeriveMutatingTestClasses().Select(t => t.Name).ToList();
        derived.Should().Contain(new[]
        {
            "SummaryGeneratorTests",
            "SummaryGeneratorEdgeCaseTests",
            "SummaryGeneratorComprehensiveTests",
            "OrganizationOptionsTests",
            "MidYearCarryoverConsistencyTests",
            "LedgerTests",
            "LedgerRepositoryMidYearCarryoverPatternTests",
            "LedgerOrderHelperMidYearCarryoverPatternTests",
            "SummaryGeneratorMidYearCarryoverLikePatternTests",
            "TransferStationGroupServiceTests",
            "SummaryGeneratorGenerationSnapshotTests",
            "SummaryGeneratorDepartmentTypeHotSwapTests",
            // Issue #2101: 手で列挙していた頃に漏れていた 7 クラス
            "SummaryGeneratorBusTextTests",
            "SummaryGeneratorBusStopExtractionTests",
            "SummaryGenerationContextTests",
            "BusTextConfigurationConsumerTests",
            "LendingServiceSummaryGuardTests",
            "LedgerRepositoryPurchaseDateTests",
            "LedgerDetailSaveOrderingIntegrationTests",
        });
    }

    /// <summary>
    /// 書き換えの判定（<see cref="MutatesSummaryGeneratorState"/>）をサンプル入力で固定する。
    /// </summary>
    [Theory]
    [InlineData("SummaryGenerator.Configure(options);", true)]
    [InlineData("SummaryGenerator.ApplyTransferStationGroups(new[] { new[] { \"A\", \"B\" } });", true)]
    [InlineData("SummaryGenerator.ResetToDefaults();", true)]
    // 設定を受け取るコンストラクタ（Configure を呼ぶ）
    [InlineData("_ = new SummaryGenerator(DepartmentType.MayorOffice, options);", true)]
    [InlineData("SummaryGenerator g = new(DepartmentType.MayorOffice, options);", true)]
    // 書き換える本番の型を生成する（target-typed new を含む）
    [InlineData("var service = new TransferStationGroupService(repo, options, logger);", true)]
    [InlineData("private TransferStationGroupService CreateService() => new(repo, options, logger);", true)]
    // 読むだけ・静的状態に触れないコンストラクタ・コメントや文字列の中は対象外（対の表明）
    [InlineData("var groups = SummaryGenerator.GetTransferStationGroups();", false)]
    [InlineData("var g = new SummaryGenerator(DepartmentType.MayorOffice);", false)]
    [InlineData("var g = new SummaryGenerator();", false)]
    [InlineData("private readonly Mock<ITransferStationGroupService> _service = new();", false)]
    [InlineData("var min = TransferStationGroupService.MinimumNamesPerGroup;", false)]
    [InlineData("// SummaryGenerator.Configure(options) を呼ぶテストは Collection に入れる", false)]
    [InlineData("[InlineData(\"SummaryGenerator.Configure(options);\", true)]", false)]
    public void 静的状態の書き換えの判定がサンプル入力で固定されていること(string code, bool expected)
    {
        MutatesSummaryGeneratorState(TestSourceInspection.ToCodeOnly(code), DeriveProductionMutators())
            .Should().Be(expected);
    }

    /// <summary>
    /// Dispose での復元の判定（<see cref="RestoresDefaultsOnDispose"/>）をサンプル入力で固定する。
    /// </summary>
    [Theory]
    [InlineData("public void Dispose() { SummaryGenerator.ResetToDefaults(); }", true)]
    [InlineData("public void Dispose() { _db.Dispose(); SummaryGenerator.ResetToDefaults(); GC.SuppressFinalize(this); }", true)]
    [InlineData("public void Dispose() => SummaryGenerator.ResetToDefaults();", true)]
    // コンストラクタでだけ戻している・Dispose が別の後始末だけ・コメントだけ（対の表明）
    [InlineData("public C() { SummaryGenerator.ResetToDefaults(); } public void Dispose() { _db.Dispose(); }", false)]
    [InlineData("public void Dispose() { GC.SuppressFinalize(this); }", false)]
    [InlineData("public void Dispose() { /* SummaryGenerator.ResetToDefaults(); */ }", false)]
    public void Disposeでの復元の判定がサンプル入力で固定されていること(string code, bool expected)
    {
        RestoresDefaultsOnDispose(TestSourceInspection.ToCodeOnly(code)).Should().Be(expected);
    }

    /// <summary>
    /// 静的な可変状態の抽出（<see cref="ExtractMutableStaticMembers"/>）をサンプル入力で固定する
    /// （Issue #2101 のコードレビューで検出）。
    /// </summary>
    /// <remarks>
    /// フィールドだけを数えると、世代を静的自動プロパティ（<c>static X Y { get; set; }</c>）へ移した日に
    /// 可変状態が 0 件へ縮み、書き換える API も 1 つも導出されなくなる。
    /// </remarks>
    [Theory]
    [InlineData("private static SummaryGenerationContext _context = SummaryGenerationContext.Default;", "_context", true)]
    [InlineData("private static SummaryGenerationContext _context;", "_context", true)]
    [InlineData("public static SummaryGenerationContext Current { get; set; }", "Current", true)]
    [InlineData("internal static SummaryGenerationContext Current { get; private set; } = SummaryGenerationContext.Default;", "Current", true)]
    // 対の表明: readonly・const・getter だけの自動プロパティ・式形式のプロパティは可変状態ではない
    [InlineData("private static readonly SummaryRules _defaults = new SummaryRules();", "_defaults", false)]
    [InlineData("public const string LendingText = \"x\";", "LendingText", false)]
    [InlineData("public static SummaryGenerationContext Current { get; }", "Current", false)]
    [InlineData("public static SummaryRules Rules => _context.Rules;", "Rules", false)]
    public void 静的な可変状態の抽出がサンプル入力で固定されていること(string code, string member, bool expected)
    {
        ExtractMutableStaticMembers(TestSourceInspection.ToCodeOnly(code)).Contains(member).Should().Be(expected);
    }

    /// <summary>
    /// 静的自動プロパティへの代入と、書き換える型を生成する型（多段の伝播）を導出できること
    /// （Issue #2101 のコードレビューで検出）。
    /// </summary>
    /// <remarks>
    /// 書き換える型の導出が 1 段だと、その型を内部で生成する本番の型（<c>Outer</c>）を生成する
    /// テストクラスが検査から漏れる。導出は不動点まで繰り返す。
    /// </remarks>
    [Fact]
    public void 静的自動プロパティと多段の生成を書き換え手段として導出すること()
    {
        var sources = new[]
        {
            "public class SummaryGenerator { public static Ctx Current { get; set; } " +
            "public static void Use(Ctx c) { Current = c; } public static Ctx Read() => Current; " +
            "public SummaryGenerator(int a, Ctx c) { Use(c); } public SummaryGenerator(int a) { } }",
            "public class Holder { public void Save() { SummaryGenerator.Use(x); } }",
            "public class Outer { private readonly Holder _holder = new Holder(); }",
            "public class Unrelated { public Ctx M() => SummaryGenerator.Read(); }",
        }.Select(TestSourceInspection.ToCodeOnly).ToList();

        var mutators = DeriveMutators(sources);

        mutators.StaticMembers.Should().Contain("Current");
        mutators.Methods.Should().Contain("Use").And.NotContain("Read");
        mutators.ConstructorArities.Should().Contain(2).And.NotContain(1);
        mutators.Types.Should().Contain(new[] { "Holder", "Outer" }).And.NotContain("Unrelated");

        MutatesSummaryGeneratorState("SummaryGenerator.Current = new Ctx();", mutators).Should().BeTrue(
            "公開された静的自動プロパティへの代入はテストから直接行える書き換えである");
        MutatesSummaryGeneratorState("var o = new Outer();", mutators).Should().BeTrue(
            "書き換える型を生成する型の生成も書き換えにつながる");
        MutatesSummaryGeneratorState("var c = SummaryGenerator.Current;", mutators).Should().BeFalse(
            "読むだけのアクセスは書き換えではない（対の表明）");
        MutatesSummaryGeneratorState("var u = new Unrelated();", mutators).Should().BeFalse(
            "読むだけの型の生成は書き換えではない（対の表明）");
    }

    #region 導出

    /// <summary>
    /// 本番コードから導出した「SummaryGenerator の静的状態を書き換える手段」。
    /// </summary>
    internal sealed class ProductionMutators
    {
        public ProductionMutators(
            IReadOnlyCollection<string> methods,
            IReadOnlyCollection<int> constructorArities,
            IReadOnlyCollection<string> types,
            IReadOnlyCollection<string> staticMembers)
        {
            Methods = methods;
            ConstructorArities = constructorArities;
            Types = types;
            StaticMembers = staticMembers;
        }

        /// <summary>SummaryGenerator の静的な可変状態（フィールド・setter を持つ静的自動プロパティ）の名前。</summary>
        public IReadOnlyCollection<string> StaticMembers { get; }

        /// <summary>静的な可変フィールドへ代入する SummaryGenerator の static メソッド名。</summary>
        public IReadOnlyCollection<string> Methods { get; }

        /// <summary>書き換えを行う SummaryGenerator のコンストラクタの引数の数。</summary>
        public IReadOnlyCollection<int> ConstructorArities { get; }

        /// <summary>書き換え手段を呼ぶ本番の型（SummaryGenerator 自身を除く）。</summary>
        public IReadOnlyCollection<string> Types { get; }
    }

    private static ProductionMutators? _productionMutators;

    /// <summary>
    /// 本番コードから書き換え手段を導出する。
    /// </summary>
    /// <remarks>
    /// 「書き換え」は SummaryGenerator の <b>static かつ readonly / const でないフィールド、または setter を持つ
    /// 静的自動プロパティ</b>への代入で定義する（現在は <c>_context</c> のみ。<see cref="ExtractMutableStaticMembers"/>）。API 名を手で列挙すると、書き換える API が増えた日に検査から漏れる。
    /// </remarks>
    internal static ProductionMutators DeriveProductionMutators()
    {
        if (_productionMutators != null)
        {
            return _productionMutators;
        }

        var sources = Directory
            .EnumerateFiles(TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(IsSourceFile)
            .Select(f => TestSourceInspection.ToCodeOnly(File.ReadAllText(f)))
            .ToList();

        return _productionMutators = DeriveMutators(sources);
    }

    /// <summary>
    /// ソース群（コメント・リテラル除去済み）から書き換え手段を導出する。
    /// </summary>
    /// <remarks>
    /// 本番コードの導出とサンプル入力の固定は、必ずこの 1 本の導出を通す（Issue #2101）。
    /// </remarks>
    internal static ProductionMutators DeriveMutators(IReadOnlyList<string> sources)
    {
        var generatorSources = sources.Where(c => Regex.IsMatch(c, @"\bclass\s+SummaryGenerator\b")).ToList();
        generatorSources.Should().NotBeEmpty("SummaryGenerator の定義が見つからないと導出が空振りする");

        var staticFields = generatorSources
            .SelectMany(ExtractMutableStaticMembers)
            .ToHashSet(StringComparer.Ordinal);
        staticFields.Should().NotBeEmpty("SummaryGenerator の静的な可変フィールドが見つからないと導出が空振りする");

        var assignsStaticField = new Regex(
            $@"(?<![.\w])(?:{string.Join("|", staticFields.Select(Regex.Escape))})\s*=(?![=>])");

        var methods = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var code in generatorSources)
        {
            foreach (Match m in Regex.Matches(code, @"\bstatic\s+[^=;(){}]*?\b(\w+)\s*\("))
            {
                var body = ExtractMemberBody(code, m.Index + m.Length - 1);
                if (body != null && assignsStaticField.IsMatch(body))
                {
                    methods.Add(m.Groups[1].Value);
                }
            }
        }

        var callsMutator = new Regex(
            $@"(?<![.\w])(?:SummaryGenerator\s*\.\s*)?(?:{string.Join("|", methods.Select(Regex.Escape))})\s*\(");

        var arities = new SortedSet<int>();
        foreach (var code in generatorSources)
        {
            foreach (Match m in Regex.Matches(code, @"\b(?:public|internal|protected|private)\s+SummaryGenerator\s*\("))
            {
                var openParen = m.Index + m.Length - 1;
                var body = ExtractMemberBody(code, openParen);
                if (body != null && (callsMutator.IsMatch(body) || assignsStaticField.IsMatch(body)))
                {
                    arities.Add(CountParameters(code, openParen));
                }
            }
        }

        var types = new SortedSet<string>(StringComparer.Ordinal);
        var callsQualifiedMutator = new Regex(
            $@"(?<![.\w])SummaryGenerator\s*\.\s*(?:{string.Join("|", methods.Select(Regex.Escape))})\s*\(");
        foreach (var code in sources.Except(generatorSources))
        {
            var constructsMutating = InvocationArities(code, @"\bnew\s+SummaryGenerator\b").Any(arities.Contains);
            if (callsQualifiedMutator.IsMatch(code) || constructsMutating)
            {
                foreach (Match c in Regex.Matches(code, @"\bclass\s+(\w+)"))
                {
                    types.Add(c.Groups[1].Value);
                }
            }
        }

        // 書き換える型を生成する本番の型も、生成したテストにとっては書き換えにつながる。
        // 1 段で止めると「書き換える型を内部で生成する型」を生成するテストが検査から漏れるため、
        // 不動点まで繰り返す（Issue #2101 のコードレビューで検出）。
        var nonGeneratorSources = sources.Except(generatorSources).ToList();
        while (true)
        {
            var candidates = nonGeneratorSources
                .Where(code => types.Any(type => ConstructsType(code, type)))
                .SelectMany(code => Regex.Matches(code, @"\bclass\s+(\w+)").Cast<Match>())
                .Select(c => c.Groups[1].Value)
                .ToList();
            if (candidates.Where(types.Add).ToList().Count == 0)
            {
                break;
            }
        }

        return new ProductionMutators(methods, arities, types, staticFields);
    }

    /// <summary>
    /// ソース（コメント・リテラル除去済み）が <paramref name="type"/> を生成しているか（通常の new・target-typed new）。
    /// </summary>
    private static bool ConstructsType(string codeOnly, string type)
    {
        var t = Regex.Escape(type);
        return Regex.IsMatch(codeOnly,
            $@"\bnew\s+{t}\s*\(|(?<![.\w<]){t}\s+\w+\s*(?:\([^()]*\))?\s*(?:=>|=)\s*new\s*\(");
    }

    /// <summary>
    /// SummaryGenerator のソース（コメント・リテラル除去済み）から、静的な可変状態のメンバー名を列挙する。
    /// </summary>
    /// <remarks>
    /// <para>
    /// フィールド（<c>static X _y;</c> / <c>static X _y = …;</c>）に加え、setter（<c>set</c> / <c>init</c>。
    /// アクセス修飾子付きを含む）を持つ静的自動プロパティ（<c>static X Y { get; set; }</c>）を数える。
    /// フィールドだけを数えると、世代を静的自動プロパティへ移した日に可変状態が 0 件へ縮み、
    /// 書き換える API も導出されなくなる（Issue #2101 のコードレビューで検出）。
    /// </para>
    /// <para>
    /// 式形式のプロパティ（<c>static X Y =&gt; …;</c>）は読むだけなので除く。旧実装は <c>=&gt;</c> の <c>=</c> を
    /// フィールド初期化子と取り違えていた。
    /// </para>
    /// </remarks>
    internal static IReadOnlyCollection<string> ExtractMutableStaticMembers(string generatorCodeOnly)
    {
        const string declaration = @"\bstatic\s+(?!readonly\b|const\b)[^=;(){}]*?\b(\w+)\s*";
        var fields = Regex.Matches(generatorCodeOnly, declaration + @"(?:=(?!>)|;)").Cast<Match>();
        var autoProperties = Regex.Matches(
                generatorCodeOnly,
                declaration + @"\{\s*(?:\w+\s+)*get\s*;\s*(?:\w+\s+)*(?:set|init)\s*;\s*\}")
            .Cast<Match>();

        return fields.Concat(autoProperties).Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    /// <summary>
    /// テストのソース（コメント・リテラル除去済み）が SummaryGenerator の静的状態を書き換えるかを判定する。
    /// </summary>
    internal static bool MutatesSummaryGeneratorState(string codeOnly, ProductionMutators mutators)
    {
        if (mutators.Methods.Count > 0 && Regex.IsMatch(codeOnly,
                $@"(?<![.\w])SummaryGenerator\s*\.\s*(?:{string.Join("|", mutators.Methods.Select(Regex.Escape))})\s*\("))
        {
            return true;
        }

        // 公開された静的な可変状態（静的自動プロパティ等）への直接の代入（Issue #2101 のコードレビューで検出）
        if (mutators.StaticMembers.Count > 0 && Regex.IsMatch(codeOnly,
                $@"(?<![.\w])SummaryGenerator\s*\.\s*(?:{string.Join("|", mutators.StaticMembers.Select(Regex.Escape))})\s*=(?![=>])"))
        {
            return true;
        }

        // 設定を受け取るコンストラクタ（通常の new・target-typed new・派生クラスの base 呼び出し）
        var constructions = InvocationArities(codeOnly, @"\bnew\s+SummaryGenerator\b")
            .Concat(InvocationArities(codeOnly, @"\bSummaryGenerator\s+\w+\s*=\s*new\b"));
        if (Regex.IsMatch(codeOnly, @"\bclass\s+\w+\s*:\s*SummaryGenerator\b"))
        {
            constructions = constructions.Concat(InvocationArities(codeOnly, @":\s*base\b"));
        }

        if (constructions.Any(mutators.ConstructorArities.Contains))
        {
            return true;
        }

        // 書き換える本番の型の生成
        return mutators.Types.Any(type => ConstructsType(codeOnly, type));
    }

    /// <summary>
    /// クラス本体（コメント・リテラル除去済み）の Dispose が <c>SummaryGenerator.ResetToDefaults()</c> を呼ぶかを判定する。
    /// </summary>
    internal static bool RestoresDefaultsOnDispose(string classCodeOnly)
    {
        foreach (Match m in Regex.Matches(classCodeOnly, @"\bvoid\s+Dispose\s*\("))
        {
            var body = ExtractMemberBody(classCodeOnly, m.Index + m.Length - 1);
            if (body != null && Regex.IsMatch(body, $@"\bSummaryGenerator\s*\.\s*{ResetMethodName}\s*\(\s*\)"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// テストのソースツリー全体から、静的状態を書き換えるテストクラス（[Fact] / [Theory] を持つトップレベルの型）を導出する。
    /// </summary>
    private static IReadOnlyList<Type> DeriveMutatingTestClasses()
    {
        var mutators = DeriveProductionMutators();
        var assemblyTypes = typeof(SummaryGeneratorCollectionConfigurationTests).Assembly.GetTypes();

        return LoadTestSources()
            .Where(s => MutatesSummaryGeneratorState(s.Code, mutators))
            .SelectMany(s => s.Classes)
            .SelectMany(name => assemblyTypes.Where(t => t.Name == name && t.DeclaringType == null))
            .Where(IsTestClass)
            .Distinct()
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsTestClass(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any());

    private static IReadOnlyList<(string Code, IReadOnlyList<string> Classes)>? _testSources;

    private static IReadOnlyList<(string Code, IReadOnlyList<string> Classes)> LoadTestSources()
    {
        if (_testSources != null)
        {
            return _testSources;
        }

        var root = Path.Combine(TestPaths.GetSolutionRoot(), "tests", "ICCardManager.Tests");
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(IsSourceFile)
            .Select(f => TestSourceInspection.ToCodeOnly(File.ReadAllText(f)))
            .Select(code => (code, (IReadOnlyList<string>)Regex.Matches(code, @"\bclass\s+(\w+)")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList()))
            .ToList();

        sources.Should().HaveCountGreaterThan(100, "テストのソースツリーが走査できること（空振り防止）");
        return _testSources = sources;
    }

    private static bool IsSourceFile(string path)
        => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    /// <summary>
    /// <c>class Name</c> の本体（<c>{ }</c> 含む）を取り出す。
    /// </summary>
    private static string ExtractClassBody(string codeOnly, string className)
    {
        var match = Regex.Match(codeOnly, $@"\bclass\s+{Regex.Escape(className)}\b");
        match.Success.Should().BeTrue($"class {className} がソース中に見つかること");
        return TestSourceInspection.ExtractMethodBody(codeOnly.Substring(match.Index), match.Value);
    }

    /// <summary>
    /// 引数リストの開き括弧の位置から、メンバーの本体（ブロック本体、または式形式の <c>=&gt; …;</c>）を返す。
    /// 本体を持たない宣言（抽象・extern・呼び出し式）なら <c>null</c>。
    /// </summary>
    private static string? ExtractMemberBody(string codeOnly, int openParen)
    {
        var closeParen = FindClosingParen(codeOnly, openParen);
        if (closeParen < 0)
        {
            return null;
        }

        var i = closeParen + 1;

        // コンストラクタ初期化子（: this(...) / : base(...)）と where 制約は読み飛ばす
        var initializer = Regex.Match(codeOnly.Substring(i), @"\A\s*:\s*(?:this|base)\s*\(");
        if (initializer.Success)
        {
            var close = FindClosingParen(codeOnly, i + initializer.Length - 1);
            if (close < 0)
            {
                return null;
            }

            i = close + 1;
        }

        while (i < codeOnly.Length && char.IsWhiteSpace(codeOnly[i]))
        {
            i++;
        }

        if (i < codeOnly.Length && codeOnly[i] == '{')
        {
            return TestSourceInspection.ExtractMethodBody(codeOnly.Substring(i), "{");
        }

        if (i + 1 < codeOnly.Length && codeOnly[i] == '=' && codeOnly[i + 1] == '>')
        {
            var end = codeOnly.IndexOf(';', i);
            return end < 0 ? null : codeOnly.Substring(i, end - i + 1);
        }

        return null;
    }

    private static int FindClosingParen(string code, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < code.Length; i++)
        {
            if (code[i] == '(')
            {
                depth++;
            }
            else if (code[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int CountParameters(string code, int openParen)
    {
        var closeParen = FindClosingParen(code, openParen);
        var parameters = code.Substring(openParen + 1, closeParen - openParen - 1);
        return string.IsNullOrWhiteSpace(parameters) ? 0 : parameters.Split(',').Length;
    }

    private static IEnumerable<int> InvocationArities(string codeOnly, string invocationPattern)
        => TestSourceInspection.ExtractInvocationArguments(codeOnly, new Regex(invocationPattern))
            .Select(i => i.Arguments.Count);

    #endregion
}
