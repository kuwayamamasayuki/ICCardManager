using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// <see cref="ProductionSourceFiles"/>（規約テストが共有する本番ソースのキャッシュ、Issue #2108）のテスト。
/// </summary>
/// <remarks>
/// 規約テストはこのキャッシュを走査対象の唯一の入口にするので、ここが縮む（ファイルを取りこぼす）と
/// 規約テストはすべて「違反ゼロ」で緑のまま無力化する。除外の範囲を既知のサンプルで固定し、
/// 実データでも既知のファイルを拾えていることを対で表明する（`.claude/rules/testing.md`
/// 「空振り検出を『各対象が非空であること』で書かない」）。
/// </remarks>
public class ProductionSourceFilesTests : IDisposable
{
    private readonly string _root;

    public ProductionSourceFilesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ProductionSourceFilesTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後片付けの失敗はテストの結果に影響させない
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void EnumerateSourcePaths_ビルド出力の配下は深さと大文字小文字によらず列挙しないこと()
    {
        Touch("App.xaml.cs");
        Touch(Path.Combine("Services", "LendingService.cs"));
        Touch(Path.Combine("Views", "Dialogs", "Deep", "Nested.cs"));
        Touch(Path.Combine("obj", "Release", "App.g.cs"));
        Touch(Path.Combine("obj", "ICCardManager_abc_wpftmp", "Leftover.cs"));
        Touch(Path.Combine("bin", "Debug", "Copied.cs"));
        Touch(Path.Combine("Views", "OBJ", "Upper.cs"));
        Touch(Path.Combine("Views", "Bin", "Mixed.cs"));

        var relative = ProductionSourceFiles.EnumerateSourcePaths(_root, "*.cs")
            .Select(p => p.Substring(_root.Length).TrimStart(Path.DirectorySeparatorChar))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        relative.Should().Equal(
            new[]
            {
                "App.xaml.cs",
                Path.Combine("Services", "LendingService.cs"),
                Path.Combine("Views", "Dialogs", "Deep", "Nested.cs"),
            }.OrderBy(p => p, StringComparer.Ordinal),
            "bin / obj の配下だけを除き、それ以外は入れ子の深さによらずすべて拾うこと");
    }

    [Theory]
    [InlineData("objects", false)]
    [InlineData("binder", false)]
    [InlineData("Obj", true)]
    [InlineData("BIN", true)]
    public void IsBuildOutputDirectoryName_名前が完全に一致するときだけ除外すること(string name, bool expected)
    {
        ProductionSourceFiles.IsBuildOutputDirectoryName(name).Should().Be(expected,
            "前方一致で除外すると、同じ文字で始まる本物のソースフォルダーが走査から黙って外れる");
    }

    [Fact]
    public void CSharp_本番ソースの既知のファイルを拾いビルド出力を含まないこと()
    {
        var files = ProductionSourceFiles.CSharp;

        files.Should().HaveCountGreaterThan(100, "本番ソースが十分に走査されていること");
        files.Select(f => f.RelativePath).Should().Contain(new[]
        {
            "App.xaml.cs",
            Path.Combine("Services", "LendingService.cs"),
            Path.Combine("Data", "DbContext.cs"),
        });
        files.Should().NotContain(
            f => f.RelativePath.Split(Path.DirectorySeparatorChar).Any(ProductionSourceFiles.IsBuildOutputDirectoryName),
            "XAML から生成された .g.cs などのビルド出力を検査対象に入れないこと");
        files.Should().OnlyContain(f => f.FullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Xaml_本番の画面定義を拾うこと()
    {
        ProductionSourceFiles.Xaml.Select(f => f.RelativePath).Should().Contain(new[]
        {
            "App.xaml",
            Path.Combine("Views", "MainWindow.xaml"),
        });
    }

    [Fact]
    public void 読み込みとサニタイズの結果はプロセスで共有されること()
    {
        ProductionSourceFiles.CSharp.Should().BeSameAs(ProductionSourceFiles.CSharp, "毎回読み直さないこと");

        var file = ProductionSourceFiles.CSharp.First(f => f.Name == "LendingService.cs");
        file.CodeOnly.Should().BeSameAs(file.CodeOnly, "サニタイズもファイルごとに 1 回だけ行うこと");
        file.Text.Should().Be(File.ReadAllText(file.FullPath), "読み込んだままのテキストを保持すること");
        file.CodeOnly.Should().Be(TestSourceInspection.ToCodeOnly(file.Text),
            "サニタイズは TestSourceInspection と同じ変換であること（別の実装を持たない）");
        file.CommentsRemovedPreservingLines.Should().Be(TestSourceInspection.RemoveCommentsPreservingLines(file.Text));
        file.CodeOnlyPreservingLines.Should().Be(TestSourceInspection.ToCodeOnlyPreservingLines(file.Text));
    }

    [Fact]
    public void Under_指定したフォルダーの配下だけを返し名前の前方一致で他のフォルダーを拾わないこと()
    {
        var data = ProductionSourceFiles.CSharp.Under("Data").ToList();

        data.Should().Contain(f => f.RelativePath == Path.Combine("Data", "DbContext.cs"));
        data.Should().OnlyContain(f => f.RelativePath.StartsWith("Data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        var viaSlash = ProductionSourceFiles.CSharp.Under("Infrastructure/CardReader").Select(f => f.RelativePath).ToList();
        var viaBackslash = ProductionSourceFiles.CSharp.Under(@"Infrastructure\CardReader").Select(f => f.RelativePath).ToList();
        viaSlash.Should().NotBeEmpty().And.Equal(viaBackslash, "区切り文字の違いで結果を変えないこと");

        var sample = new[]
        {
            new ProductionSourceFiles.SourceFile("a", Path.Combine("Data", "A.cs"), string.Empty),
            new ProductionSourceFiles.SourceFile("b", Path.Combine("DataExport", "B.cs"), string.Empty),
        };
        sample.Under("Data").Select(f => f.RelativePath).Should().Equal(Path.Combine("Data", "A.cs"));
    }

    private void Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// sample");
    }
}
