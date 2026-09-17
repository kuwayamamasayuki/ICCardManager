using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #2051: 物品出納簿テンプレートの帳票幅（L 列 = 12）をリテラルで書かないことを固定する規約テスト。
/// </summary>
/// <remarks>
/// <para>
/// 帳票幅は #1956 で <c>ReportService.TemplateLastColumn</c> ただ 1 つに寄せたとコメントに書かれていたが、
/// 実際には <c>ExcelStyleFormatter</c>（罫線・結合）と <c>ReportService</c>（見出し・備考欄のコピー、
/// 列幅のコピー、合計行の書式）にリテラルの <c>12</c> が 16 か所残っていた。定義を 1 つにしたという主張は、
/// 使う側がその定義を参照していて初めて成立する（<c>service-conventions.md</c> #1924）。
/// </para>
/// <para>
/// 走査対象は <c>TemplateLastColumn</c> を参照する本番ファイルから<b>導出</b>する（ファイル名で列挙すると
/// 帳票幅を扱うファイルが増えたときに静かに漏れる。#1786）。対の表明として、導出した集合に既知の
/// 2 ファイルが含まれることと、検出ロジックが既知の入力を正しく分類することを置く — 前者が無いと
/// 導出が 0 件に縮んでも緑になり、後者が無いと検出が空振りしても緑になる。
/// </para>
/// <para>
/// <b>検出しない形</b>: <c>TemplateLastColumn</c> を一度も参照しないファイルに書かれたリテラル、
/// 列番号を別名のローカル変数へ退避してから渡す形、<c>"A1:L4"</c> のようなアドレス文字列。
/// 行番号の <c>12</c>（<c>Range(12, 1, 12, TemplateLastColumn)</c>）は帳票幅ではないので検出しない。
/// </para>
/// </remarks>
public class ReportTemplateColumnLiteralConventionTests
{
    private const string TemplateWidthLiteral = "12";

    /// <summary>ClosedXML の列番号を取る呼び出し（メソッド名までを照合する）。</summary>
    private static readonly Regex ColumnTakingInvocation = new Regex(
        @"\.\s*(?<name>Range|Cells?|Columns?|Add)\b", RegexOptions.Compiled);

    /// <summary>列番号のループ境界（<c>col &lt;= 12</c> / <c>column &lt; 13</c>）。</summary>
    private static readonly Regex ColumnLoopBound = new Regex(
        @"\b(?:col|column)(?:Index)?\s*(?:<=\s*12|<\s*13)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// コードのみのソースから、帳票幅をリテラルで書いた箇所を返す。
    /// </summary>
    internal static IReadOnlyList<string> FindTemplateWidthLiterals(string codeOnlySource)
    {
        var findings = new List<string>();

        foreach (var (index, arguments) in TestSourceInspection.ExtractInvocationArguments(codeOnlySource, ColumnTakingInvocation))
        {
            var name = ColumnTakingInvocation.Match(codeOnlySource, index).Groups["name"].Value;
            var columnPositions = (name, arguments.Count) switch
            {
                // Range(firstRow, firstColumn, lastRow, lastColumn) / PrintAreas.Add(同)
                ("Range", 4) or ("Add", 4) => new[] { 1, 3 },
                // Cell(row, column) / Row(r).Cell(column)
                ("Cell", 2) => new[] { 1 },
                ("Cell", 1) => new[] { 0 },
                // Column(column) / Columns(firstColumn, lastColumn)
                ("Column", 1) => new[] { 0 },
                ("Columns", 2) => new[] { 0, 1 },
                // Enumerable.Range(start, count) 等は列番号ではない
                _ => Array.Empty<int>(),
            };

            if (columnPositions.Any(i => arguments[i].Trim() == TemplateWidthLiteral))
            {
                findings.Add(Describe(codeOnlySource, index));
            }
        }

        foreach (Match match in ColumnLoopBound.Matches(codeOnlySource))
        {
            findings.Add(Describe(codeOnlySource, match.Index));
        }

        return findings;
    }

    private static string Describe(string source, int index)
    {
        var line = source.Take(index).Count(c => c == '\n') + 1;
        var lineStart = source.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var lineEnd = source.IndexOf('\n', index);
        var text = source.Substring(lineStart, (lineEnd < 0 ? source.Length : lineEnd) - lineStart).Trim();
        return $"{line}: {text}";
    }

    [Theory]
    [InlineData("var r = worksheet.Range(row, 1, row, 12);", true)]
    [InlineData("worksheet.Range(firstRow, 12, lastRow, TemplateLastColumn).Style.Border.RightBorder = x;", true)]
    [InlineData("worksheet.Cell(row, 12).Style.Border.RightBorder = x;", true)]
    [InlineData("target.Column(12).Width = 3;", true)]
    [InlineData("worksheet.PageSetup.PrintAreas.Add(1, 1, lastRow, 12);", true)]
    [InlineData("worksheet.Row(row).Cell(12).Value = x;", true)]
    [InlineData("worksheet.Columns(1, 12).AdjustToContents();", true)]
    [InlineData("for (int col = 1; col <= 12; col++)", true)]
    [InlineData("for (int column = 1; column < 13; column++)", true)]
    // 複数行にまたがる呼び出し
    [InlineData("worksheet.Range(\n    row, 1,\n    row, 12)", true)]
    // 正しい形・対象外の形
    [InlineData("var r = worksheet.Range(row, 1, row, TemplateLastColumn);", false)]
    [InlineData("worksheet.Cell(row, ReportService.TemplateLastColumn)", false)]
    [InlineData("for (int col = 1; col <= TemplateLastColumn; col++)", false)]
    [InlineData("var r = worksheet.Range(12, 1, 12, TemplateLastColumn);", false)]
    [InlineData("worksheet.Cell(12, 2).Value = x;", false)]
    [InlineData("worksheet.Row(12).Height = 30;", false)]
    [InlineData("worksheet.Row(12).Cell(TemplateLastColumn).Value = x;", false)]
    [InlineData("const int RowsPerPage = 12;", false)]
    [InlineData("if (length < 32) return 12;", false)]
    [InlineData("new ObservableCollection<int>(Enumerable.Range(1, 12))", false)]
    [InlineData("if (month >= 1 && month <= 12)", false)]
    [InlineData("for (int count = 1; count <= 12; count++)", false)]
    public void 帳票幅のリテラル検出が既知の入力を正しく分類すること(string code, bool expected)
    {
        FindTemplateWidthLiterals(TestSourceInspection.ToCodeOnlyPreservingLines(code))
            .Any().Should().Be(expected);
    }

    [Fact]
    public void 帳票幅を扱う本番コードが列番号12をリテラルで書いていないこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateTemplateWidthConsumers())
        {
            var code = TestSourceInspection.ToCodeOnlyPreservingLines(File.ReadAllText(file));
            violations.AddRange(FindTemplateWidthLiterals(code).Select(f => $"{Path.GetFileName(file)}:{f}"));
        }

        violations.Should().BeEmpty(
            "物品出納簿テンプレートの帳票幅は ReportService.TemplateLastColumn ただ 1 つで表す。" +
            "同じ幅を別々のリテラルで持つと、片方だけが変わる日が来る（Issue #1956 / #2051）");
    }

    [Fact]
    public void 帳票幅を参照するファイルの導出が既知のファイルを含むこと()
    {
        var names = EnumerateTemplateWidthConsumers().Select(Path.GetFileName).ToList();

        names.Should().Contain(new[] { "ReportService.cs", "ExcelStyleFormatter.cs" },
            "導出した走査対象が縮んでいると、リテラルの検査が空振りする（#1786）");
    }

    private static IEnumerable<string> EnumerateTemplateWidthConsumers()
        => Directory.EnumerateFiles(TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => TestSourceInspection.ToCodeOnly(File.ReadAllText(f)).Contains("TemplateLastColumn"));
}
