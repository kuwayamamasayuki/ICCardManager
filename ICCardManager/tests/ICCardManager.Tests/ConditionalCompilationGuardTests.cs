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
    private static readonly string ProjectRoot = Path.Combine(
        FindRepoRoot(), "ICCardManager", "src", "ICCardManager");

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
    /// 指定識別子を含む行が <c>#if DEBUG</c> ブロック内にあることを表明する。
    /// </summary>
    private static void AssertIdentifierIsInsideDebugBlock(string filePath, string identifier)
    {
        File.Exists(filePath).Should().BeTrue($"対象ファイルが存在する: {filePath}");
        var source = File.ReadAllText(filePath);
        var lines = source.Replace("\r\n", "\n").Split('\n');

        var targetLineNumber = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(identifier))
            {
                targetLineNumber = i + 1;
                break;
            }
        }
        targetLineNumber.Should().BeGreaterThan(0,
            $"対象識別子 '{identifier}' がファイル '{Path.GetFileName(filePath)}' に存在する必要がある");

        IsLineInsideDebugBlock(source, targetLineNumber).Should().BeTrue(
            $"'{identifier}' は #if DEBUG ガード内になければならない " +
            $"({Path.GetFileName(filePath)}:{targetLineNumber}) " +
            "— Release ビルドからの除外が崩れている可能性があります");
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

            if (trimmed.StartsWith("#if DEBUG", StringComparison.Ordinal))
            {
                stack.Push(true);
            }
            else if (trimmed.StartsWith("#if !DEBUG", StringComparison.Ordinal))
            {
                stack.Push(false);
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
}
