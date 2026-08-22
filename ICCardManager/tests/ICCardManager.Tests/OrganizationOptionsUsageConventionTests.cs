using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1820: <see cref="OrganizationOptions"/> に定義された設定項目が、
/// <b>すべて本番コードから実際に読まれている</b>ことを静的に検査する。
/// </summary>
/// <remarks>
/// <para>
/// 本 Issue の起点は「<c>TemplateMapping</c> の 18 項目中 5 項目しか使われていない」状態だった。
/// 管理者マニュアル §7.4 が設定可能と案内している一方、本番コードは同名のローカル <c>const</c> と
/// 列リテラルを使っており、設定しても反映されない（＝広告と実装の乖離）。
/// </para>
/// <para>
/// 個別テストは項目の追加に追随できないため、<b>リフレクションで全項目を導出</b>して
/// 検査対象を自動的に増やす（Issue #1786 の「ガードは経路の網羅で設計する」、
/// Issue #1818 の「個別テストと規約テストを対で置く」と同じ形）。
/// </para>
/// <para>
/// 検査は<b>コメントを除去してから</b>行う。除去しないと「この項目は未使用」と説明した
/// コメント自体が「使用されている」と誤判定される（極性の反転）。
/// </para>
/// <para>
/// なお本テストは「読まれているか」だけを見る。読まれた値が正しく<b>効いている</b>ことは、
/// 消費側の個別テスト（<c>ReportFileNameConfigurationConsumerTests</c> /
/// <c>BusTextConfigurationConsumerTests</c> 等）が既定と異なる値を設定して表明する。
/// </para>
/// </remarks>
public class OrganizationOptionsUsageConventionTests
{
    private static readonly string ProductionRoot = Path.Combine(
        FindRepoRoot(), "ICCardManager", "src", "ICCardManager");

    /// <summary>
    /// 既定値の宣言そのものなので検査対象から除外するファイル
    /// </summary>
    private const string OptionsDeclarationFileName = "OrganizationOptions.cs";

    /// <summary>
    /// 検査対象の設定項目（親プロパティ名 or 宣言クラス名, プロパティ名）を導出する
    /// </summary>
    public static IEnumerable<object[]> AllOptionProperties()
    {
        foreach (var sectionProperty in typeof(OrganizationOptions).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            // OrganizationOptions 直下のセクション（SummaryText 等）自身
            yield return new object[] { nameof(OrganizationOptions), sectionProperty.Name };

            foreach (var itemProperty in sectionProperty.PropertyType.GetProperties(
                         BindingFlags.Public | BindingFlags.Instance))
            {
                yield return new object[] { sectionProperty.Name, itemProperty.Name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllOptionProperties))]
    public void すべての組織設定項目が本番コードから読まれている(string ownerName, string propertyName)
    {
        var sources = LoadProductionSources();
        sources.Should().NotBeEmpty("本番ソースの走査が空振りしていないこと");

        var isRead = sources.Values.Any(code => IsPropertyRead(code, ownerName, propertyName));

        isRead.Should().BeTrue(
            $"組織設定 {ownerName}.{propertyName} が本番コードから一度も読まれていません。" +
            "設定できるのに反映されない項目（dead config）は、設定するほど壊れる状態になります。" +
            "実装して読むか、項目そのものを削除してください（あわせて管理者マニュアル §7.4 の表も同期すること）。");
    }

    #region 空振り検出・検査ロジックの固定（Issue #1786）

    [Fact]
    public void 検査対象の設定項目が十分な数だけ導出されている()
    {
        // 「導出が空になったのでテストが全部通る」状態を検出する。
        // 下限は現状（36項目）より小さい値にして、正当な項目削除で赤にならないようにする。
        AllOptionProperties().Should().HaveCountGreaterThan(20);
    }

    [Fact]
    public void 除外対象の宣言ファイルが実在する()
    {
        File.Exists(Path.Combine(ProductionRoot, "Services", OptionsDeclarationFileName))
            .Should().BeTrue("除外対象のパスが変わると検査が意味を失うため");
    }

    [Theory]
    // 実際に本番コードに現れる読み取りの形
    [InlineData("_options.SummaryText.BusLabel;", "SummaryText", "BusLabel", true)]
    [InlineData("_options.SummaryText?.BusLabel;", "SummaryText", "BusLabel", true)]
    [InlineData("_orgOptions.ReportLayout?.FileNameFormat,", "ReportLayout", "FileNameFormat", true)]
    [InlineData("new ReportLayoutOptions().FileNameFormat;", "ReportLayout", "FileNameFormat", true)]
    [InlineData("options.TemplateMapping.UnitColumn)", "TemplateMapping", "UnitColumn", true)]
    // 別のセクションのプロパティは一致させない
    [InlineData("_options.SummaryText.BusLabel;", "ReportLayout", "BusLabel", false)]
    // 同名の無関係な識別子（XAML の TextBlock 名等）を拾わない
    [InlineData("toast.TitleText.Text = title;", "ReportLayout", "TitleText", false)]
    // 前方一致で拾わない
    [InlineData("_options.SummaryText.BusLabelSuffix;", "SummaryText", "BusLabel", false)]
    // コメント中の言及は読み取りではない
    [InlineData("// 組織設定 SummaryText.BusLabel 由来", "SummaryText", "BusLabel", false)]
    [InlineData("/// <c>ReportLayout.FileNameFormat</c>", "ReportLayout", "FileNameFormat", false)]
    public void 検査ロジックが既知のサンプル入力で期待どおり動く(
        string source, string ownerName, string propertyName, bool expected)
    {
        IsPropertyRead(TestSourceInspection.RemoveCommentsPreservingLines(source), ownerName, propertyName)
            .Should().Be(expected);
    }

    #endregion

    #region 検査ロジック

    /// <summary>
    /// <paramref name="ownerName"/>.<paramref name="propertyName"/> の読み取りが
    /// コメント除去済みソースに含まれるかを判定する
    /// </summary>
    /// <remarks>
    /// 所有者（セクション名 / 宣言クラス名）ごと照合することで、同名の無関係な識別子
    /// （XAML から生成される <c>TitleText</c> 等）を拾わない。null 条件演算子
    /// （<c>?.</c>）と <c>new XxxOptions()</c> 形式の直接読み取りも許容する。
    /// </remarks>
    private static bool IsPropertyRead(string codeOnlySource, string ownerName, string propertyName)
    {
        var escapedProperty = Regex.Escape(propertyName);

        // OrganizationOptions 直下のセクション（SummaryText 等）は "<何か>.SummaryText" の形。
        // 所有者を名指しできないため、メンバーアクセスであることだけを要求する。
        var pattern = ownerName == nameof(OrganizationOptions)
            ? $@"\.\s*{escapedProperty}\b"
            : $@"(?:{Regex.Escape(ownerName)}|{Regex.Escape(ownerName)}Options\s*\(\s*\))\s*\??\s*\.\s*{escapedProperty}\b";

        return Regex.IsMatch(codeOnlySource, pattern);
    }

    private static Dictionary<string, string> LoadProductionSources()
    {
        var sources = new Dictionary<string, string>();

        foreach (var path in Directory.EnumerateFiles(ProductionRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                Path.GetFileName(path) == OptionsDeclarationFileName)
            {
                continue;
            }

            sources[path] = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(path));
        }

        return sources;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
        {
            throw new InvalidOperationException(
                $"リポジトリルート (.git を含むディレクトリ) が見つかりませんでした。基準: {AppContext.BaseDirectory}");
        }
        return dir.FullName;
    }

    #endregion
}
