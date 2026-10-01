using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// <see cref="MainViewModelSourceFiles"/>（Issue #2158）の導出と、<c>MainViewModel</c> の partial 分割の構成を固定する。
/// </summary>
/// <remarks>
/// <c>MainViewModel</c> を読む静的検査（<c>ConditionalCompilationGuardTests</c>・<c>ReturnFlowDialogHeaderColorConventionTests</c> 等）は
/// このヘルパーから走査対象を取る。導出が 0 件や本体だけに縮むと、それらの検査は<b>緑のまま空振りする</b>ため、
/// 「導出が実際に partial ファイルまで届いていること」をここで対の表明として置く（#1786）。
/// </remarks>
[Trait("Category", "Unit")]
public class MainViewModelSourceFilesTests
{
    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

    [Fact]
    public void 構成ファイルは本体とMainフォルダーのpartialファイルすべてであること()
    {
        var files = MainViewModelSourceFiles.All.Select(f => Normalize(f.RelativePath)).ToList();

        files.Should().Contain("ViewModels/MainViewModel.cs", "本体（フィールド・コンストラクタ・状態遷移）");
        files.Should().HaveCountGreaterThan(1, "MainViewModel は責務ごとの partial ファイルへ分割されている");

        // Main フォルダーに置いたファイルが、宣言の照合から漏れていないこと（導出が実際に届いている表明）
        var partialFiles = ProductionSourceFiles.CSharp
            .Under(Path.Combine("ViewModels", "Main"))
            .Select(f => Normalize(f.RelativePath))
            .ToList();
        partialFiles.Should().NotBeEmpty();
        files.Should().Contain(partialFiles);
    }

    [Fact]
    public void partialファイルはMainフォルダーにMainViewModel_責務名_csの名前で置くこと()
    {
        // 撮影対応表（screenshot-sources.json）は "ViewModels/Main/MainViewModel.*.cs" の glob で partial ファイルを覆う。
        // 別の場所・別の名前へ置くと、撮り直しの検知から漏れる
        var misplaced = MainViewModelSourceFiles.All
            .Select(f => Normalize(f.RelativePath))
            .Where(p => p != "ViewModels/MainViewModel.cs")
            .Where(p => !Regex.IsMatch(p, @"^ViewModels/Main/MainViewModel\.[A-Za-z]+\.cs$"))
            .ToList();

        misplaced.Should().BeEmpty(
            "MainViewModel の partial ファイルは ViewModels/Main/MainViewModel.<責務名>.cs に置くこと（Issue #2158）");
    }

    [Fact]
    public void ifDEBUGはDebugのpartialファイルに閉じていること()
    {
        // DEBUG 限定の仮想タッチ（Issue #1487）を 1 ファイルへ閉じ、Release から除外される範囲を見通せるようにする。
        // 行頭の指令だけを見る（「#if DEBUG ブロック内」と説明する XML コメントを数えない）
        var withDebugBlock = MainViewModelSourceFiles.All
            .Where(f => Regex.IsMatch(f.Text, @"(?m)^[ \t]*#if[ \t]+DEBUG\b"))
            .Select(f => Normalize(f.RelativePath))
            .ToList();

        withDebugBlock.Should().Equal(new[] { "ViewModels/Main/MainViewModel.Debug.cs" },
            "#if DEBUG は MainViewModel.Debug.cs だけに置くこと（Issue #2158）");
    }

    [Theory]
    [InlineData("public void Exit()", "ViewModels/MainViewModel.cs")]
    [InlineData("public async Task OpenVirtualCardAsync(", "ViewModels/Main/MainViewModel.Debug.cs")]
    [InlineData("internal async Task HandleReturnSuccessAsync(", "ViewModels/Main/MainViewModel.ReturnFlow.cs")]
    [InlineData("private async Task HandleCardInIcCardWaitingStateAsync(", "ViewModels/Main/MainViewModel.CardTouch.cs")]
    public void FindDeclaringFileは宣言しているファイルを返すこと(string declaration, string expected)
    {
        Normalize(MainViewModelSourceFiles.FindDeclaringFile(declaration).RelativePath).Should().Be(expected);
    }

    [Fact]
    public void FindDeclaringFileは見つからない宣言で例外を投げること()
    {
        // 黙って null や先頭のファイルを返すと、呼び出し側の検査が無関係なファイルを見て空振りする
        Action act = () => MainViewModelSourceFiles.FindDeclaringFile("public void ThisMethodDoesNotExist__()");

        act.Should().Throw<InvalidOperationException>().WithMessage("*0 件*");
    }

    [Theory]
    [InlineData("public partial class MainViewModel : ViewModelBase", true)]
    [InlineData("public partial class MainViewModel\n{", true)]
    [InlineData("partial  class  MainViewModel", true)]
    [InlineData("public partial class MainViewModelFactory", false)]
    [InlineData("public class MainViewModel", false)]
    [InlineData("// public partial class MainViewModel", false)]
    [InlineData("var s = \"partial class MainViewModel\";", false)]
    public void partialクラス宣言の照合はコメントと文字列と別の型名を拾わないこと(string source, bool expected)
    {
        // 照合はコメント・文字列リテラルを除いた本文（CodeOnly）に対して行う。
        // コメントの「partial class MainViewModel」で無関係なファイルを構成ファイルに含めない
        MainViewModelSourceFiles.PartialClassDeclarationPattern
            .IsMatch(TestSourceInspection.ToCodeOnly(source))
            .Should().Be(expected);
    }
}
