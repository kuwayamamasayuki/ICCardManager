using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2035: 摘要生成の世代（<see cref="SummaryGenerationContext"/>）の保守性に関する回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 対象は「今は実害が無いが、次に変更する人が欠陥を持ち込みやすい」箇所で、次の 3 つを固定する。
/// </para>
/// <list type="number">
/// <item><description>
/// 同一視グループの差し替えが、グループ以外の設定を<b>1 つ残らず</b>引き継ぐこと。
/// 手作業のコピーは、設定クラスへプロパティを足した日に F6 の保存でそのプロパティが既定値へ戻る
/// （#1726「触っていない値が既定値へ戻る」と同じ形）。個別のプロパティを並べたテストは
/// プロパティの増減に追随できないため、リフレクションで走査する（<c>LedgerClonerCoverageTests</c> と同じ作法）。
/// </description></item>
/// <item><description>
/// 観測用の <see cref="SummaryGenerationContext.GetTransferStationGroups"/> が、判定に実際に使っている
/// 「併合済み・空白除外済み」のグループを返すこと。設定の生のリストを返すと、併合が壊れても
/// このメソッドで確かめるテストは緑のままになる。
/// </description></item>
/// <item><description>
/// null を含む設定でも世代を組み立てられること（設定から来た値は、生成の各段階へ届く前に 1 か所で補う）。
/// </description></item>
/// </list>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class SummaryGenerationContextTests : IDisposable
{
    public SummaryGenerationContextTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    #region 同一視グループの差し替えが他の設定を引き継ぐこと

    /// <summary>
    /// グループの差し替えが、<see cref="OrganizationOptions"/> と <see cref="SummaryRulesOptions"/> の
    /// グループ以外の書き込み可能な全プロパティを引き継ぐこと。
    /// </summary>
    /// <remarks>
    /// 各プロパティを既定と異なる値へ設定してから差し替え、差し替え後の世代が同じ値を持つことを表明する。
    /// 走査対象はリフレクションで導出するので、プロパティを足した人が本テストの追記を忘れても自動的に対象へ入る。
    /// 走査できない型が現れたら例外にして、黙って素通りしない（Issue #1944 の fail-open 回避）。
    /// </remarks>
    [Fact]
    public void WithTransferStationGroups_グループ以外の全プロパティを引き継ぐこと()
    {
        // Arrange: すべてのプロパティを既定と異なる値にする
        var options = new OrganizationOptions();
        var expectedTopLevel = AssignDistinctValues(options, excluded: nameof(OrganizationOptions.SummaryRules));
        var expectedRules = AssignDistinctValues(
            options.SummaryRules, excluded: nameof(SummaryRulesOptions.TransferStationGroups));
        var context = SummaryGenerationContext.Create(options);

        // Act
        var replaced = context.WithTransferStationGroups(new[] { new[] { "薬院", "大橋" } });

        // Assert
        AssertPreserved(replaced.Options, expectedTopLevel);
        AssertPreserved(replaced.Options.SummaryRules, expectedRules);
        expectedTopLevel.Should().NotBeEmpty("走査が空振りしていないこと");
        expectedRules.Should().NotBeEmpty("走査が空振りしていないこと");
    }

    /// <summary>
    /// 対のテスト: 差し替えたグループそのものは置き換わり、元の世代と設定インスタンスは変わらないこと。
    /// </summary>
    /// <remarks>
    /// これが無いと、差し替えを無視して元の設定をそのまま返す実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public void WithTransferStationGroups_グループだけが置き換わり元の世代は変わらないこと()
    {
        // Arrange
        var options = new OrganizationOptions();
        var context = SummaryGenerationContext.Create(options);

        // Act
        var replaced = context.WithTransferStationGroups(new[] { new[] { "薬院", "大橋" } });

        // Assert
        replaced.GetTransferStationGroups().Should().ContainSingle()
            .Which.Should().Equal("薬院", "大橋");
        replaced.Options.Should().NotBeSameAs(options);
        replaced.Options.SummaryRules.Should().NotBeSameAs(options.SummaryRules);

        context.GetTransferStationGroups().Should().BeEquivalentTo(
            new OrganizationOptions().SummaryRules.TransferStationGroups);
        options.SummaryRules.TransferStationGroups.Should().BeEquivalentTo(
            new OrganizationOptions().SummaryRules.TransferStationGroups);
    }

    #endregion

    #region 観測用メソッドが判定と同じグループを返すこと

    /// <summary>
    /// 名前を共有するグループは、判定と同じ 1 つの同値類として観測されること。
    /// </summary>
    [Fact]
    public void GetTransferStationGroups_重なるグループは併合した同値類を返すこと()
    {
        // Arrange: [A,B] と [B,C] は A ≡ B ≡ C の 1 つの同値類になる（Issue #1905）
        var context = CreateContext(
            new[] { "天神日銀前", "天神中央郵便局前" },
            new[] { "天神中央郵便局前", "天神北" });

        // Act
        var groups = context.GetTransferStationGroups();

        // Assert: 名前の並びは設定に最初に現れた順
        groups.Should().ContainSingle()
            .Which.Should().Equal("天神日銀前", "天神中央郵便局前", "天神北");
        context.AreTransferStations("天神日銀前", "天神北").Should().BeTrue("判定も同じ同値類で動いていること");
    }

    /// <summary>
    /// 対のテスト: 互いに素なグループは分かれたまま、空白の名前と空のグループは除いて観測されること。
    /// </summary>
    /// <remarks>
    /// これが無いと、すべてのグループを 1 つへ潰す実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public void GetTransferStationGroups_互いに素なグループは分けたまま空白を除いて返すこと()
    {
        // Arrange
        var context = CreateContext(
            new[] { "天神", " ", "西鉄福岡(天神)" },
            new[] { "", "  " },
            new[] { "千早", "西鉄千早" });

        // Act
        var groups = context.GetTransferStationGroups();

        // Assert
        groups.Should().HaveCount(2);
        groups[0].Should().Equal("天神", "西鉄福岡(天神)");
        groups[1].Should().Equal("千早", "西鉄千早");
    }

    #endregion

    #region 同一視判定と正規化

    /// <summary>
    /// 同一視判定（<see cref="SummaryGenerationContext.AreTransferStations"/>）と
    /// 正規化（<see cref="SummaryGenerationContext.CanonicalStation"/>）が、名前の全組み合わせで一致すること。
    /// </summary>
    /// <remarks>
    /// <c>GetRemainingRoutes</c> は往復の消費枠を正規化した名前で数え、<c>DetectRoundTrips</c> は判定で拾う。
    /// 両者が食い違うと復路が「余り」に残り重複表示になる（Issue #1905）。Issue #2035 で両者を
    /// 1 つの辞書へ寄せたため、グループに属さない名前・null を含めて全組み合わせを走査して固定する。
    /// </remarks>
    [Fact]
    public void 同一視判定と正規化が名前の全組み合わせで一致すること()
    {
        // Arrange
        var context = CreateContext(
            new[] { "天神日銀前", "天神中央郵便局前" },
            new[] { "天神中央郵便局前", "天神北" },
            new[] { "千早", "西鉄千早" },
            new[] { "博多" });
        var names = new[] { "天神日銀前", "天神中央郵便局前", "天神北", "千早", "西鉄千早", "博多", "薬院", null };
        var expectedClasses = new[]
        {
            new[] { "天神日銀前", "天神中央郵便局前", "天神北" },
            new[] { "千早", "西鉄千早" },
        };

        foreach (var a in names)
        {
            foreach (var b in names)
            {
                var expected = a == b || expectedClasses.Any(c => c.Contains(a) && c.Contains(b));

                // Act & Assert
                context.AreTransferStations(a, b).Should().Be(expected, $"AreTransferStations({a ?? "null"}, {b ?? "null"})");
                (context.CanonicalStation(a) == context.CanonicalStation(b)).Should().Be(
                    expected, $"CanonicalStation({a ?? "null"}) と CanonicalStation({b ?? "null"}) の一致");
            }
        }
    }

    /// <summary>
    /// 代表名はグループ内で序数比較が最小の名前で、グループに属さない名前はそのまま返ること。
    /// </summary>
    [Fact]
    public void CanonicalStation_代表名は序数比較で最小の名前を返すこと()
    {
        // Arrange
        var context = CreateContext(new[] { "西鉄福岡(天神)", "天神" });
        var expected = string.CompareOrdinal("西鉄福岡(天神)", "天神") < 0 ? "西鉄福岡(天神)" : "天神";

        // Act & Assert
        context.CanonicalStation("西鉄福岡(天神)").Should().Be(expected);
        context.CanonicalStation("天神").Should().Be(expected);
        context.CanonicalStation("薬院").Should().Be("薬院");
        context.CanonicalStation(null).Should().BeNull();
    }

    #endregion

    #region null を含む設定

    public static IEnumerable<object[]> OptionsContainingNull() => new[]
    {
        new object[] { "SummaryText が null", (Action<OrganizationOptions>)(o => o.SummaryText = null) },
        new object[] { "SummaryRules が null", (Action<OrganizationOptions>)(o => o.SummaryRules = null) },
        new object[] { "TransferStationGroups が null", (Action<OrganizationOptions>)(o => o.SummaryRules.TransferStationGroups = null) },
        new object[] { "グループの要素が null", (Action<OrganizationOptions>)(o => o.SummaryRules.TransferStationGroups.Add(null)) },
        new object[] { "名前が null", (Action<OrganizationOptions>)(o => o.SummaryRules.TransferStationGroups.Add(new List<string> { null, "薬院" })) },
    };

    /// <summary>
    /// null を含む設定でも世代を組み立てられ、摘要を生成できること。
    /// </summary>
    /// <remarks>
    /// 旧実装は <c>BuildTransferStationGroups</c> が <c>SummaryRules</c> と各グループを null チェックせずに参照し、
    /// 生成の各段階は <c>SummaryText.RailwayLabel</c> 等を直接参照していたため、
    /// DI の解決中（<c>SummaryGenerator</c> の生成）に例外になり得た。
    /// </remarks>
    [Theory]
    [MemberData(nameof(OptionsContainingNull))]
    public void Create_nullを含む設定でも摘要を生成できること(string caseName, Action<OrganizationOptions> corrupt)
    {
        // Arrange
        var options = new OrganizationOptions();
        corrupt(options);

        // Act
        SummaryGenerator.Configure(options);
        var summary = new SummaryGenerator().Generate(CreateRailwayRoundTripDetails());

        // Assert
        summary.Should().Be("鉄道（天神～博多 往復）", caseName);
    }

    /// <summary>
    /// 同一視グループのリストが null の設定は「未設定」とみなし、既定のグループで補うこと。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 上の Theory は摘要（天神～博多 往復）しか見ておらず、この経路では既定グループでも空でも同じ結果になるため
    /// 判断を固定していない（コードレビューで検出）。グループの中身で表明する。
    /// </para>
    /// <para>
    /// <c>ApplyTransferStationGroups(null)</c> が空のグループになるのとは意味が異なる。あちらは管理者が
    /// 「グループを保存する」操作の引数であり null は「何も登録しない」、こちらは設定ファイルの項目で
    /// null は「書かれていない」（セクションが無ければ既定値、という <see cref="OrganizationOptions"/> の方針に合わせる）。
    /// </para>
    /// </remarks>
    [Fact]
    public void Create_グループのリストがnullなら既定のグループで補い差し替えのnullは空にすること()
    {
        // Arrange
        var options = new OrganizationOptions();
        options.SummaryRules.TransferStationGroups = null;

        // Act
        var context = SummaryGenerationContext.Create(options);
        var applied = context.WithTransferStationGroups(null);

        // Assert
        context.GetTransferStationGroups().Should().BeEquivalentTo(
            new SummaryRulesOptions().TransferStationGroups, o => o.WithStrictOrdering());
        applied.GetTransferStationGroups().Should().BeEmpty();
        options.SummaryRules.TransferStationGroups.Should().BeNull("渡された設定インスタンスは書き換えないこと");
    }

    /// <summary>
    /// 摘要テキストの null は既定値で補うこと（プロパティはリフレクションで走査する）。
    /// </summary>
    [Fact]
    public void Create_摘要テキストのnullは既定値で補うこと()
    {
        // Arrange
        var options = new OrganizationOptions();
        var stringProperties = StringProperties(typeof(SummaryTextOptions));
        foreach (var property in stringProperties)
        {
            property.SetValue(options.SummaryText, null);
        }

        // Act
        var context = SummaryGenerationContext.Create(options);

        // Assert
        var defaults = new SummaryTextOptions();
        stringProperties.Should().NotBeEmpty("走査が空振りしていないこと");
        foreach (var property in stringProperties)
        {
            property.GetValue(context.Options.SummaryText).Should().Be(
                property.GetValue(defaults), $"{property.Name} の null は既定値で補うこと");
        }
    }

    /// <summary>
    /// 対のテスト: 空文字や独自の値は「明示的な設定」として保持し、渡された設定インスタンスは書き換えないこと。
    /// </summary>
    /// <remarks>
    /// null だけを補う。空文字まで既定値へ倒すと、往復の接尾辞を空にする設定
    /// （<c>CollapseExplicitGroupSummary</c> が空の接尾辞を想定して分岐している）が効かなくなる。
    /// 補うために設定インスタンスを書き換えると、DI シングルトンの設定を参照する他のサービスへ波及する。
    /// </remarks>
    [Fact]
    public void Create_空文字と独自の値は保持し渡された設定を書き換えないこと()
    {
        // Arrange
        var options = new OrganizationOptions();
        options.SummaryText.RoundTripSuffix = string.Empty;
        options.SummaryText.RailwayLabel = "電車";
        options.SummaryText.PointRedemption = null;
        var originalText = options.SummaryText;

        // Act
        var context = SummaryGenerationContext.Create(options);

        // Assert
        context.Options.SummaryText.RoundTripSuffix.Should().BeEmpty();
        context.Options.SummaryText.RailwayLabel.Should().Be("電車");
        context.Options.SummaryText.PointRedemption.Should().Be(new SummaryTextOptions().PointRedemption);

        options.SummaryText.Should().BeSameAs(originalText);
        options.SummaryText.PointRedemption.Should().BeNull("渡された設定インスタンスは書き換えないこと");
    }

    #endregion

    #region ヘルパー

    private static SummaryGenerationContext CreateContext(params string[][] groups)
    {
        var options = new OrganizationOptions();
        options.SummaryRules.TransferStationGroups = groups.Select(g => g.ToList()).ToList();
        return SummaryGenerationContext.Create(options);
    }

    /// <summary>ICカード履歴（新しい順）: 天神→博多、博多→天神</summary>
    private static List<LedgerDetail> CreateRailwayRoundTripDetails() => new()
    {
        new LedgerDetail
        {
            UseDate = new DateTime(2024, 5, 1), EntryStation = "博多", ExitStation = "天神",
            Amount = 260, Balance = 740, SequenceNumber = 1
        },
        new LedgerDetail
        {
            UseDate = new DateTime(2024, 5, 1), EntryStation = "天神", ExitStation = "博多",
            Amount = 260, Balance = 1000, SequenceNumber = 2
        },
    };

    private static IReadOnlyList<PropertyInfo> StringProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.PropertyType == typeof(string))
            .ToList();

    /// <summary>
    /// 書き込み可能な全プロパティへ既定と異なる値を設定し、設定した値を返す。
    /// </summary>
    private static IReadOnlyDictionary<PropertyInfo, object> AssignDistinctValues(object target, string excluded)
    {
        var assigned = new Dictionary<PropertyInfo, object>();
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite || property.Name == excluded)
            {
                continue;
            }

            var current = property.GetValue(target);
            object value = property.PropertyType switch
            {
                var t when t == typeof(bool) => !(bool)current,
                var t when t == typeof(int) => (int)current + 7,
                var t when t == typeof(string) => current + "_Issue2035",
                var t when t.IsClass && t.GetConstructor(Type.EmptyTypes) != null => Activator.CreateInstance(t),
                _ => throw new NotSupportedException(
                    $"{target.GetType().Name}.{property.Name}（{property.PropertyType.Name}）に既定と異なる値を作れない。" +
                    "本テストのヘルパーへ型を追加すること（黙って走査から外さない）。")
            };

            property.SetValue(target, value);
            assigned[property] = value;
        }

        return assigned;
    }

    private static void AssertPreserved(object actual, IReadOnlyDictionary<PropertyInfo, object> expected)
    {
        foreach (var pair in expected)
        {
            var value = pair.Key.GetValue(actual);
            if (pair.Key.PropertyType.IsClass && pair.Key.PropertyType != typeof(string))
            {
                value.Should().BeSameAs(pair.Value, $"{pair.Key.DeclaringType!.Name}.{pair.Key.Name} を引き継ぐこと");
            }
            else
            {
                value.Should().Be(pair.Value, $"{pair.Key.DeclaringType!.Name}.{pair.Key.Name} を引き継ぐこと");
            }
        }
    }

    #endregion
}
