using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// SQLite の接続文字列を組み立てる箇所を 1 つに保つ静的検査（Issue #1924）
/// </summary>
/// <remarks>
/// <para>
/// <b>SQLite はバックスラッシュの UNC パス（<c>\\server\share\x.db</c>）を開けない</b>。
/// <c>Data Source</c> にそのまま渡すと <c>SQLiteException: unable to open database file</c>
/// になる。<c>DbContext.BuildConnectionString</c> はフォワードスラッシュへ変換してこれを回避するが、
/// <c>BackupService.CopyDatabaseTo</c> が <c>$"Data Source={destinationPath}"</c> で
/// 接続文字列を自前に組み立てていたため、<b>共有フォルダーを保存先にするとバックアップだけが
/// 必ず失敗していた</b>（DB 本体は <c>DbContext</c> 経由なので開ける）。
/// </para>
/// <para>
/// 個別テストでは守れない。UNC への実接続は共有が必要で CI から実行できず、
/// 「接続文字列をどこで組み立てたか」は経路が増えるたびに追随漏れが起きる
/// （<c>.claude/rules/error-messages.md</c> #1764「経路ごとの個別テストで守り切れないと分かったら
/// ソーステキストの静的検査へ移す」）。
/// </para>
/// </remarks>
public class SqliteConnectionStringConventionTests
{
    /// <summary>
    /// 接続文字列の組み立てを許可する唯一の場所。
    /// </summary>
    private const string AllowedFile = "DbContext.cs";

    /// <summary>
    /// <c>Data Source</c> を含む文字列リテラル・補間文字列を検出する。
    /// </summary>
    /// <remarks>
    /// 大文字小文字を無視するのは <c>SQLiteConnectionStringBuilder.ToString()</c> が
    /// <c>data source=</c> を出力するため、その綴りで書かれた自前組み立ても拾うため。
    /// </remarks>
    private static readonly Regex DataSourceLiteralPattern =
        new(@"data\s+source\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <c>SQLiteConnectionStringBuilder</c> の使用を検出する（型名の出現そのもの）。
    /// </summary>
    /// <remarks>
    /// <c>new SQLiteConnectionStringBuilder</c> という生成式だけを見ると、target-typed new
    /// （<c>SQLiteConnectionStringBuilder b = new() { DataSource = p };</c>）・完全修飾名
    /// （<c>new System.Data.SQLite.SQLiteConnectionStringBuilder</c>）・<c>using</c> 別名が素通りする（Issue #2101）。
    /// DbContext 以外に型名が現れる正当な理由は無いため、生成式ではなく型名で照合する。
    /// 照合はコメントと文字列リテラルを除いたコードに対して行う（型名を説明する文言を誤検出しないため）。
    /// </remarks>
    private static readonly Regex BuilderPattern =
        new(@"(?<![A-Za-z0-9_])SQLiteConnectionStringBuilder(?![A-Za-z0-9_])", RegexOptions.Compiled);

    /// <summary>
    /// 1 ファイル分の生のソースが、接続文字列を自前に組み立てているかを判定する。
    /// </summary>
    /// <remarks>
    /// 実データの検査とサンプル入力の固定は、必ずこの 1 本の判定を通す（前処理ごと共有する）。
    /// <c>Data Source</c> はリテラルの中身が検査対象なのでコメントだけを除いたテキストで、
    /// 型名はリテラルも除いたテキストで照合する。
    /// </remarks>
    internal static bool AssemblesConnectionString(string rawSource)
        => DataSourceLiteralPattern.IsMatch(TestSourceInspection.RemoveCommentsPreservingLines(rawSource))
           || BuilderPattern.IsMatch(TestSourceInspection.ToCodeOnly(rawSource));

    /// <summary>
    /// Issue #1924: 接続文字列の組み立ては <c>DbContext</c> だけが行うこと。
    /// </summary>
    /// <remarks>
    /// 検査は<b>コメントを除去し、文字列リテラルは残した</b>ソースに対して行う
    /// （<c>ToCodeOnly</c> ではリテラルごと消えて検査対象が無くなる。逆にコメントを残すと、
    /// 本規約の理由を説明したコメント自体が違反として検出される＝極性の反転）。
    /// </remarks>
    [Fact]
    public void 接続文字列の組み立てはDbContextだけが行うこと()
    {
        var violations = new List<string>();

        foreach (var file in EnumerateProductionSources())
        {
            if (string.Equals(Path.GetFileName(file), AllowedFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (AssemblesConnectionString(File.ReadAllText(file)))
            {
                violations.Add(RelativePath(file));
            }
        }

        violations.Should().BeEmpty(
            "SQLite はバックスラッシュの UNC パスを開けないため、接続文字列は "
            + "DbContext.BuildConnectionString に一本化する（Issue #1924）。"
            + "自前に組み立てると、共有フォルダーを指定した環境でだけ "
            + "SQLiteException: unable to open database file になる");
    }

    /// <summary>
    /// Issue #1924 の対: 正規の組み立て手段が実在すること。
    /// </summary>
    /// <remarks>
    /// 「違反の不在」だけを検査すると、<c>DbContext</c> 側の変換ごと消した実装でも緑になる
    /// （`.claude/rules/development-conventions.md`「禁止された形の不在と正しい形の存在を対で表明する」）。
    /// </remarks>
    [Fact]
    public void DbContextがUNCをフォワードスラッシュへ変換していること()
    {
        var dbContextPath = Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Data", "DbContext.cs");

        File.Exists(dbContextPath).Should().BeTrue("検査対象が見つからないと空振りする");

        var code = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(dbContextPath));

        code.Should().Contain("internal static string BuildConnectionString",
            "接続文字列の組み立ては単一のメソッドとして公開する");
        code.Should().Contain("Replace('\\\\', '/')",
            "UNC パスをフォワードスラッシュへ変換しないと SQLite が開けない");
    }

    /// <summary>
    /// Issue #1924: 検査ロジック自体をサンプル入力で固定する。
    /// </summary>
    /// <remarks>
    /// 実データ側が空になっても空振りしないようにする（#1786）。
    /// </remarks>
    [Theory]
    [InlineData("new SQLiteConnection($\"Data Source={path}\");", true)]
    [InlineData("new SQLiteConnection(\"data source=\" + path);", true)]
    [InlineData("var b = new SQLiteConnectionStringBuilder { DataSource = p };", true)]
    [InlineData("new SQLiteConnection(DbContext.BuildConnectionString(path));", false)]
    [InlineData("// Data Source= を直接組み立てないこと", false)]
    // Issue #2101: 生成式に型名が現れない・綴りが異なる形
    [InlineData("SQLiteConnectionStringBuilder b = new() { DataSource = p };", true)]
    [InlineData("var b = new System.Data.SQLite.SQLiteConnectionStringBuilder { DataSource = p };", true)]
    [InlineData("private readonly SQLiteConnectionStringBuilder _builder = new();", true)]
    [InlineData("using Csb = System.Data.SQLite.SQLiteConnectionStringBuilder;", true)]
    // 型名を説明するコメント・文字列、名前が前方一致するだけの別の識別子は対象外（対の表明）
    [InlineData("/// <c>SQLiteConnectionStringBuilder</c> は DbContext だけが使う", false)]
    [InlineData("_logger.LogDebug(\"SQLiteConnectionStringBuilder を使わない\");", false)]
    [InlineData("var x = MySQLiteConnectionStringBuilderFactory.Create();", false)]
    public void 検出パターンがサンプル入力を正しく判定すること(string snippet, bool expectedViolation)
    {
        AssemblesConnectionString(snippet).Should().Be(expectedViolation);
    }

    private static IEnumerable<string> EnumerateProductionSources()
    {
        return Directory
            .EnumerateFiles(TestPaths.GetProductionSourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
    }

    private static string RelativePath(string fullPath)
    {
        var root = TestPaths.GetProductionSourceRoot();
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)
            : fullPath;
    }
}
