using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1487: DEBUG 限定機能の起動経路が Release ビルドから完全に除外されるよう
/// <c>#if DEBUG</c> ガード（および XAML 側の <c>App.IsDebugBuild</c> バインディング）
/// が効いていることを継続的に保証する回帰テスト。
///
/// 直接の対象は仮想タッチ設定ダイアログ <c>VirtualCardDialog</c>（Issue #640）と
/// その起動経路（コマンド・ViewModel・DI 登録・UI ボタン）。
/// </summary>
public class ConditionalCompilationGuardTests
{
    /// <remarks>
    /// Issue #2101: 旧実装は <c>.git</c> ディレクトリを探して親をたどっていた。git worktree では
    /// <c>.git</c> がファイルになるため、リポジトリ内の worktree（<c>.claude/worktrees/…</c>）では
    /// <b>本体の作業ツリーを黙って検査し</b>、リポジトリ外の worktree では型初期化で落ちる。
    /// ソリューションファイルを基点にする <see cref="TestPaths"/> へ寄せる。
    /// </remarks>
    private static readonly string ProjectRoot = TestPaths.GetProductionSourceRoot();

    [Fact]
    public void MainViewModel_OpenVirtualCardAsync_IsInsideDebugGuard()
    {
        var path = Path.Combine(ProjectRoot, "ViewModels", "MainViewModel.cs");
        AssertIdentifierIsInsideDebugBlock(path, "public async Task OpenVirtualCardAsync(");
    }

    [Fact]
    public void MainViewModel_ProcessVirtualTouchAsync_IsInsideDebugGuard()
    {
        var path = Path.Combine(ProjectRoot, "ViewModels", "MainViewModel.cs");
        AssertIdentifierIsInsideDebugBlock(path, "private async Task ProcessVirtualTouchAsync(");
    }

    [Fact]
    public void App_VirtualCardViewModelRegistration_IsInsideDebugGuard()
    {
        var path = Path.Combine(ProjectRoot, "App.xaml.cs");
        AssertIdentifierIsInsideDebugBlock(path, "AddTransient<VirtualCardViewModel>");
    }

    [Fact]
    public void App_VirtualCardDialogRegistration_IsInsideDebugGuard()
    {
        var path = Path.Combine(ProjectRoot, "App.xaml.cs");
        AssertIdentifierIsInsideDebugBlock(path, "AddTransient<Views.Dialogs.VirtualCardDialog>");
    }

    [Fact]
    public void MainWindow_VirtualCardButton_IsGuardedByIsDebugBuild()
    {
        var path = Path.Combine(ProjectRoot, "Views", "MainWindow.xaml");
        File.Exists(path).Should().BeTrue($"対象 XAML が存在する: {path}");

        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        var commandLineIdx = Array.FindIndex(lines, l => l.Contains("OpenVirtualCardCommand"));
        commandLineIdx.Should().BeGreaterThanOrEqualTo(0,
            "MainWindow.xaml に OpenVirtualCardCommand バインディングが存在する必要がある");

        // 当該 Button から遡って直近の親 StackPanel 開始タグを探し、
        // その開始タグから対象行までの間に App.IsDebugBuild の Visibility バインディングがあること。
        var guardLineIdx = -1;
        var stackPanelOpenIdx = -1;
        for (int i = commandLineIdx; i >= 0; i--)
        {
            if (lines[i].Contains("<StackPanel"))
            {
                stackPanelOpenIdx = i;
                break;
            }
        }
        stackPanelOpenIdx.Should().BeGreaterThanOrEqualTo(0,
            "OpenVirtualCardCommand ボタンの親となる StackPanel 開始タグが見つかる必要がある");

        for (int j = stackPanelOpenIdx; j <= commandLineIdx; j++)
        {
            if (lines[j].Contains("App.IsDebugBuild") &&
                lines[j].IndexOf("Visibility", StringComparison.Ordinal) >= 0)
            {
                guardLineIdx = j;
                break;
            }
            // 開始タグ直後に `>` で閉じる前までを許容範囲とする。
            // 入れ子の別 StackPanel が始まる場合は探索を打ち切る。
            if (j > stackPanelOpenIdx && lines[j].Contains("<StackPanel"))
            {
                break;
            }
        }

        guardLineIdx.Should().BeGreaterThanOrEqualTo(0,
            "OpenVirtualCardCommand ボタンを内包する StackPanel は " +
            "Visibility=\"{Binding Source={x:Static app:App.IsDebugBuild}, ...}\" で " +
            "Release ビルドから非表示になっている必要がある");
    }

    /// <summary>
    /// 識別子の出現位置の判定がサンプル入力で期待どおり働くこと（Issue #2101）
    /// </summary>
    /// <remarks>
    /// <para>
    /// 旧実装は識別子の<b>最初の出現</b>しか見ておらず、<c>#if DEBUG</c> の内側に 1 つ目の登録を残したまま
    /// ガードの外へ 2 つ目の <c>AddTransient&lt;VirtualCardViewModel&gt;</c> を書いても緑だった。
    /// 全出現を検査し、1 つでもガードの外にあれば違反とする。
    /// </para>
    /// <para>
    /// 全出現を見るとコメント中の言及まで拾うため、コメントを除去してから照合する
    /// （「ここでは登録しない」と説明したコメントが違反になる極性の反転。#1692）。
    /// 「検出しない形」を対で置くのは、すべての出現を違反とみなす実装でも緑にならないようにするため。
    /// </para>
    /// </remarks>
    [Theory]
    // 検出しない形: 唯一の出現が #if DEBUG の内側
    [InlineData("a();\n#if DEBUG\nReg<X>();\n#endif\n", 1, "")]
    // 検出しない形: 2 つとも #if DEBUG の内側
    [InlineData("#if DEBUG\nReg<X>();\n#endif\nb();\n#if DEBUG\nReg<X>();\n#endif\n", 2, "")]
    // 検出しない形: ガードの外にあるのはコメント中の言及だけ
    [InlineData("// Reg<X>(); は DEBUG 限定\n#if DEBUG\nReg<X>();\n#endif\n", 1, "")]
    // 検出する形: 2 つ目の出現がガードの外（旧実装は最初の出現しか見ないため緑だった）
    [InlineData("#if DEBUG\nReg<X>();\n#endif\nReg<X>();\n", 2, "4")]
    // 検出する形: DEBUG 以外の条件（#if 別記号 / #else 側）
    [InlineData("#if TRACE\nReg<X>();\n#endif\n", 1, "2")]
    [InlineData("#if DEBUG\na();\n#else\nReg<X>();\n#endif\n", 1, "4")]
    // 検出する形: DEBUG で始まる別の記号（旧実装は前方一致で DEBUG とみなしていた）
    [InlineData("#if DEBUG_EXTRA\nReg<X>();\n#endif\n", 1, "2")]
    public void 識別子の全出現がDEBUGガード内かを判定できること(
        string source, int expectedOccurrences, string expectedOutsideLines)
    {
        var (occurrences, outside) = FindOccurrencesOutsideDebugBlock(source, "Reg<X>();");

        occurrences.Should().Be(expectedOccurrences);
        string.Join(",", outside).Should().Be(expectedOutsideLines);
    }

    /// <summary>
    /// 指定識別子の<b>すべての出現</b>が <c>#if DEBUG</c> ブロック内にあることを表明する。
    /// </summary>
    private static void AssertIdentifierIsInsideDebugBlock(string filePath, string identifier)
    {
        File.Exists(filePath).Should().BeTrue($"対象ファイルが存在する: {filePath}");
        var (occurrences, outside) = FindOccurrencesOutsideDebugBlock(File.ReadAllText(filePath), identifier);

        occurrences.Should().BeGreaterThan(0,
            $"対象識別子 '{identifier}' がファイル '{Path.GetFileName(filePath)}' に存在する必要がある");

        outside.Should().BeEmpty(
            $"'{identifier}' はすべての出現が #if DEBUG ガード内になければならない " +
            $"({Path.GetFileName(filePath)}:{string.Join(", ", outside)}) " +
            "— Release ビルドからの除外が崩れている可能性があります");
    }

    /// <summary>
    /// コメントを除いたコード中の <paramref name="identifier"/> の出現数と、
    /// そのうち <c>#if DEBUG</c> ブロックの外にある行番号（1 始まり）を返す。
    /// </summary>
    internal static (int Occurrences, IReadOnlyList<int> OutsideLines) FindOccurrencesOutsideDebugBlock(
        string source, string identifier)
    {
        // 行番号を保ったままコメントを除去する（プリプロセッサディレクティブは残る）
        var codeSource = TestSourceInspection.RemoveCommentsPreservingLines(source);
        var lines = codeSource.Split('\n');
        var occurrences = 0;
        var outside = new List<int>();

        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(identifier))
            {
                continue;
            }

            occurrences++;
            if (!IsLineInsideDebugBlock(codeSource, i + 1))
            {
                outside.Add(i + 1);
            }
        }

        return (occurrences, outside);
    }

    /// <summary>
    /// C# プリプロセッサディレクティブのスタックを追跡し、
    /// 指定行が <c>#if DEBUG</c>（または <c>#else</c> で反転した !DEBUG）の評価で
    /// 「DEBUG ブロック内」となっているかを判定する。
    /// </summary>
    /// <remarks>
    /// 入れ子の <c>#if</c> は AND で結合する。いずれかが false なら DEBUG ブロック内とはみなさない。
    /// </remarks>
    internal static bool IsLineInsideDebugBlock(string source, int targetLineNumber)
    {
        var stack = new Stack<bool>();
        var lines = source.Replace("\r\n", "\n").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            // 前方一致だと `#if DEBUG_EXTRA` のような別の記号を DEBUG とみなす（Issue #2101）。
            // 判定は RemoveDebugOnlyRegions と同じ語境界付きの照合へ寄せる
            if (TestSourceInspection.IsDebugOnlyDirective(trimmed))
            {
                stack.Push(true);
            }
            else if (trimmed.StartsWith("#if ", StringComparison.Ordinal))
            {
                stack.Push(false);
            }
            else if (trimmed.StartsWith("#else", StringComparison.Ordinal) && stack.Count > 0)
            {
                var top = stack.Pop();
                stack.Push(!top);
            }
            else if (trimmed.StartsWith("#elif ", StringComparison.Ordinal) && stack.Count > 0)
            {
                stack.Pop();
                stack.Push(false);
            }
            else if (trimmed.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0)
            {
                stack.Pop();
            }

            if (i + 1 == targetLineNumber)
            {
                return stack.Count > 0 && stack.All(x => x);
            }
        }

        return false;
    }
}
