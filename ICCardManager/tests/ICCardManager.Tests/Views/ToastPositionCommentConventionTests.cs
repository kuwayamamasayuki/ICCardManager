using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #1697: トースト通知の表示位置は設定（<see cref="ToastPosition"/>: 右上/左上/右下/左下、
/// 既定は右上）で変更できるにもかかわらず、位置設定の追加以前に書かれた
/// 「画面右上に表示」と断定するコメントがコード内に残り、実装と乖離していた。
/// このコメントを根拠に PR 本文へ「画面右上に表示される」という不正確な記述が伝播した
/// 実例（Issue #1683 / PR #1696）があるため、再発を静的に固定する。
/// </summary>
/// <remarks>
/// 実際の配置座標の検証には WPF ウィンドウの実描画（UI オートメーション）が必要なため、
/// ここでは (1) コメントが特定の画面隅を断定していないこと、(2) その根拠となる配置ロジックが
/// <see cref="ToastPosition"/> の全メンバーを分岐していること、を軽量に検証する。
/// </remarks>
public class ToastPositionCommentConventionTests
{
    /// <summary>
    /// 検査対象のトースト関連ソース（ソースルートからの相対パス → トースト専用ファイルか）。
    /// トースト専用ファイルは全行を検査する。そうでないファイル（MainViewModel など）は
    /// 「トースト」「Toast」を含む行のみを検査する。メイン画面の警告エリア（画面右下）のように
    /// トースト以外の固定位置を正当に説明している記述を誤検出しないため。
    /// </summary>
    private static readonly Dictionary<string, bool> ToastRelatedSources = new Dictionary<string, bool>
    {
        [Path.Combine("Services", "IToastNotificationService.cs")] = true,
        [Path.Combine("Services", "ToastNotificationService.cs")] = true,
        [Path.Combine("Views", "ToastNotificationWindow.xaml.cs")] = true,
        [Path.Combine("Common", "ToastLayoutCalculator.cs")] = true,
        [Path.Combine("ViewModels", "MainViewModel.cs")] = false,
    };

    /// <summary>
    /// トースト専用ではないファイルで「トーストについての記述」と判定するためのキーワード。
    /// </summary>
    private static readonly string[] ToastContextWords =
    {
        "トースト",
        "Toast",
    };

    /// <summary>
    /// 「画面◯◯」と特定の隅を断定する表現。設定で変更できるため誤り。
    /// </summary>
    private static readonly string[] FixedCornerClaims =
    {
        "画面右上",
        "画面左上",
        "画面右下",
        "画面左下",
    };

    /// <summary>
    /// 既定値の説明として隅の名称に言及するのは正当なため、これらの語を含む行は除外する。
    /// </summary>
    private static readonly string[] DefaultValueContextWords =
    {
        "既定",
        "デフォルト",
    };

    [Fact]
    public void Toast_related_sources_should_not_assert_a_fixed_screen_corner()
    {
        var sourceRoot = GetSourceRoot();
        var violations = new List<string>();

        foreach (var entry in ToastRelatedSources)
        {
            var relativePath = entry.Key;
            var isToastDedicatedFile = entry.Value;
            var fullPath = Path.Combine(sourceRoot, relativePath);
            File.Exists(fullPath).Should().BeTrue($"検査対象ソースが存在する: {relativePath}");

            var lines = File.ReadAllText(fullPath).Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (DefaultValueContextWords.Any(w => line.Contains(w)))
                {
                    continue;
                }

                if (!isToastDedicatedFile && !ToastContextWords.Any(w => line.Contains(w)))
                {
                    continue;
                }

                var hit = FixedCornerClaims.FirstOrDefault(claim => line.Contains(claim));
                if (hit != null)
                {
                    violations.Add($"  - {relativePath}:{i + 1}  「{hit}」  ({line.Trim()})");
                }
            }
        }

        violations.Should().BeEmpty(
            "トーストの表示位置は設定（ToastPosition）で 4 隅から選べるため、特定の隅を断定するコメントは " +
            "実装と乖離する。「設定された画面隅（既定は右上）」等の表現に修正すること。\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void PositionToast_should_branch_on_every_ToastPosition_value()
    {
        var windowSource = File.ReadAllText(
            Path.Combine(GetSourceRoot(), "Views", "ToastNotificationWindow.xaml.cs"));

        var missing = Enum.GetNames(typeof(ToastPosition))
            .Where(name => !windowSource.Contains($"case ToastPosition.{name}:"))
            .ToList();

        missing.Should().BeEmpty(
            "ToastNotificationWindow.PositionToast が ToastPosition の全メンバーを分岐していなければ、" +
            "「設定された画面隅に表示される」というコメント／マニュアルの記述が実装と乖離する。" +
            $"分岐が無い値: {string.Join(", ", missing)}");
    }

    [Fact]
    public void CurrentPosition_default_should_be_TopRight()
    {
        var windowSource = File.ReadAllText(
            Path.Combine(GetSourceRoot(), "Views", "ToastNotificationWindow.xaml.cs"));

        windowSource.Should().Contain("CurrentPosition { get; set; } = ToastPosition.TopRight;",
            "コメント・マニュアルが「既定は右上」と説明しているため、既定値を変更する場合は " +
            "それらの記述も併せて更新する必要がある（Issue #1697）。");
    }

    /// <summary>
    /// テスト実行ディレクトリから親方向に `ICCardManager.sln` を探索し、
    /// 見つかった階層から `src/ICCardManager/` を返す。
    /// （<see cref="ICCardManager.Tests.UserFacingTextConventionTests"/> と同じ探索パターン）
    /// </summary>
    private static string GetSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ICCardManager.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new InvalidOperationException(
                $"ICCardManager.sln が AppContext.BaseDirectory ({AppContext.BaseDirectory}) から見つからない。" +
                "テスト実行ディレクトリの構造を確認してください。");
        }

        return Path.Combine(dir.FullName, "src", "ICCardManager");
    }
}
