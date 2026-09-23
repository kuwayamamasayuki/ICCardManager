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

    /// <summary>
    /// <c>PositionToast</c> の switch が、<see cref="ToastPosition"/> の各メンバーについて
    /// <b>そのメンバーが名指す画面隅へ</b>配置する座標計算を持つこと。
    /// </summary>
    /// <remarks>
    /// Issue #2102: 旧版は <c>case ToastPosition.X:</c> という字句がファイルのどこかにあれば合格しており、
    /// フォールスルー（<c>case TopRight: case TopLeft: …</c>）で 4 隅を同じ座標にしても、
    /// コメントアウトした case でも緑だった。コメントを除いた <c>PositionToast</c> の本体だけを見て、
    /// case ごとの節を切り出し、節の中の <c>Left</c> / <c>Top</c> の代入が
    /// 名前の示す作業領域の辺（<c>workArea.Right</c> / <c>workArea.Left</c> /
    /// <c>workArea.Top</c> / <c>workArea.Bottom</c>）を参照していることを表明する。
    /// </remarks>
    [Fact]
    public void PositionToast_should_place_each_ToastPosition_value_at_its_own_corner()
    {
        var sections = ExtractPositionToastSections();
        var problems = new List<string>();

        foreach (var name in Enum.GetNames(typeof(ToastPosition)))
        {
            if (!sections.TryGetValue(name, out var section))
            {
                problems.Add($"{name}: case が無い");
                continue;
            }

            var left = GetSingleAssignment(section, "Left");
            var top = GetSingleAssignment(section, "Top");
            if (left == null || top == null)
            {
                problems.Add($"{name}: 節の中に Left / Top の代入がそれぞれ 1 つずつ無い（フォールスルー等）: {section.Trim()}");
                continue;
            }

            var expectedHorizontal = name.Contains("Right") ? "workArea.Right" : "workArea.Left";
            var expectedVertical = name.Contains("Top") ? "workArea.Top" : "workArea.Bottom";
            if (!left.Contains(expectedHorizontal))
            {
                problems.Add($"{name}: Left が {expectedHorizontal} を基準にしていない: {left}");
            }

            if (!top.Contains(expectedVertical))
            {
                problems.Add($"{name}: Top が {expectedVertical} を基準にしていない: {top}");
            }
        }

        problems.Should().BeEmpty(
            "ToastNotificationWindow.PositionToast が ToastPosition の各メンバーを、その名前が示す画面隅へ" +
            "配置していなければ、「設定された画面隅に表示される」というコメント／マニュアルの記述が実装と乖離する。\n" +
            string.Join("\n", problems));
    }

    /// <summary>
    /// 節の切り出し（<see cref="ExtractPositionToastSections"/>）の検出力をサンプル入力で固定する。
    /// フォールスルーした case は空の節になり、コメントアウトした case は節として現れないこと。
    /// </summary>
    [Fact]
    public void SplitSwitchSections_should_expose_fallthrough_and_ignore_commented_out_cases()
    {
        const string sample = @"
private void PositionToast()
{
    switch (CurrentPosition)
    {
        case ToastPosition.TopRight:
        case ToastPosition.TopLeft:
            Left = workArea.Left + margin;
            Top = workArea.Top + margin;
            break;
        // case ToastPosition.BottomRight:
        //     Left = workArea.Right - ActualWidth - margin;
        default:
            Left = 0;
            break;
    }
}";
        var body = TestSourceInspection.ExtractMethodBody(
            TestSourceInspection.ToCodeOnly(sample), "private void PositionToast");

        var sections = SplitSwitchSections(body);

        sections.Keys.Should().BeEquivalentTo(new[] { "TopRight", "TopLeft", "default" });
        GetSingleAssignment(sections["TopRight"], "Left").Should().BeNull("フォールスルーした case は自分の座標計算を持たない");
        GetSingleAssignment(sections["TopLeft"], "Left").Should().Be("workArea.Left + margin");
    }

    /// <summary>
    /// 範囲外・未保存の設定値は右上（既定）へ倒れ、各メンバーは保存と読み込みで往復すること。
    /// </summary>
    /// <remarks>
    /// Issue #2102: 旧版は <c>ToastNotificationWindow.CurrentPosition</c> の初期値の字句しか見ておらず、
    /// 実際に既定値を決めている <c>SettingsRepository</c> の変換（DB に値が無い／読めないときの
    /// フォールバック）を右上以外へ変えても緑だった。コメント・マニュアルの「既定は右上」を
    /// 支えているのは起動時に DB から読んだ値なので、変換そのものを挙動で固定する。
    /// 変換は private なのでリフレクションで呼ぶ。
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("center")]
    public void SettingsRepository_should_fall_back_to_TopRight_for_missing_or_unknown_value(string? stored)
    {
        InvokeSettingsRepositoryConverter<ToastPosition>("ParseToastPosition", stored)
            .Should().Be(ToastPosition.TopRight,
                "コメント・マニュアルが「既定は右上」と説明しているため（Issue #1697）");
    }

    /// <summary>
    /// 既存の DB に保存されている表示位置の文字列。<b>本体から導出せずリテラルで固定する</b>。
    /// </summary>
    /// <remarks>
    /// 保存と読み込みの往復だけを見ると、両方向の書式を一貫して入れ替えても緑になる（コードレビューで検出）。
    /// そうなると、既に DB に入っている値（例: 利用者が左下を選んで保存した <c>bottom_left</c>）が
    /// 別の隅として読まれる（または読めずに右上へ倒れる）。保存値は既存 DB との互換の契約なので、
    /// 変えるならマイグレーションと併せて変える。
    /// </remarks>
    private static readonly Dictionary<ToastPosition, string> StoredToastPositionLiterals = new()
    {
        [ToastPosition.TopRight] = "top_right",
        [ToastPosition.TopLeft] = "top_left",
        [ToastPosition.BottomRight] = "bottom_right",
        [ToastPosition.BottomLeft] = "bottom_left",
    };

    [Fact]
    public void SettingsRepository_should_round_trip_every_ToastPosition_value()
    {
        StoredToastPositionLiterals.Keys.Should().BeEquivalentTo(
            Enum.GetValues(typeof(ToastPosition)).Cast<ToastPosition>(),
            "表示位置を足したら、既存 DB との互換を考えたうえで保存値をここへ足す");

        foreach (var entry in StoredToastPositionLiterals)
        {
            InvokeSettingsRepositoryConverter<string>("ToastPositionToString", entry.Key)
                .Should().Be(entry.Value, $"{entry.Key} は既存の DB と同じ「{entry.Value}」で保存される");
            InvokeSettingsRepositoryConverter<ToastPosition>("ParseToastPosition", entry.Value)
                .Should().Be(entry.Key, $"既存の DB の「{entry.Value}」は {entry.Key} として読まれる");
        }
    }

    /// <summary>
    /// 範囲外の値（<c>default:</c> 節）が右上（既定）へ配置されること。
    /// </summary>
    /// <remarks>
    /// <see cref="PositionToast_should_place_each_ToastPosition_value_at_its_own_corner"/> は名前の付いた
    /// 4 つの case しか見ない（コードレビューで検出）。<c>(ToastPosition)99</c> のような値や、将来の
    /// メンバーの追加で case を書き忘れた値の置き場所は <c>default:</c> が決めるので、そこも既定と同じ隅であることを表明する。
    /// </remarks>
    [Fact]
    public void PositionToast_should_place_out_of_range_value_at_TopRight()
    {
        var sections = ExtractPositionToastSections();

        sections.Should().ContainKey("default", "範囲外の値の置き場所を default: で決めていること");
        var left = GetSingleAssignment(sections["default"], "Left");
        var top = GetSingleAssignment(sections["default"], "Top");

        left.Should().NotBeNull("default: の節が自分の座標計算を持つこと");
        top.Should().NotBeNull("default: の節が自分の座標計算を持つこと");
        left!.Should().Contain("workArea.Right", "範囲外の値は既定（右上）と同じ右端へ置く（Issue #1697）");
        top!.Should().Contain("workArea.Top", "範囲外の値は既定（右上）と同じ上端へ置く（Issue #1697）");
    }

    [Fact]
    public void Defaults_should_be_TopRight()
    {
        new AppSettings().ToastPosition.Should().Be(ToastPosition.TopRight,
            "コメント・マニュアルが「既定は右上」と説明しているため、既定値を変更する場合は " +
            "それらの記述も併せて更新する必要がある（Issue #1697）。");

        // ToastNotificationWindow は WPF の Window なので型の初期化を避け、コメントを除いたソースで確かめる。
        var windowCode = TestSourceInspection.ToCodeOnly(File.ReadAllText(
            Path.Combine(GetSourceRoot(), "Views", "ToastNotificationWindow.xaml.cs")));
        windowCode.Should().Contain("CurrentPosition { get; set; } = ToastPosition.TopRight;",
            "設定の読み込み前に表示されたトーストも右上に出る（Issue #1697）");
    }

    private static T InvokeSettingsRepositoryConverter<T>(string methodName, object? argument)
    {
        var method = typeof(ICCardManager.Data.Repositories.SettingsRepository).GetMethod(
            methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull($"SettingsRepository.{methodName} が存在する（改名したら本テストを追随させる）");
        return (T)method!.Invoke(null, new[] { argument })!;
    }

    private static Dictionary<string, string> ExtractPositionToastSections()
    {
        var code = TestSourceInspection.ToCodeOnly(File.ReadAllText(
            Path.Combine(GetSourceRoot(), "Views", "ToastNotificationWindow.xaml.cs")));
        var body = TestSourceInspection.ExtractMethodBody(code, "private void PositionToast");
        return SplitSwitchSections(body);
    }

    /// <summary>
    /// switch 本体を <c>case ToastPosition.X:</c> / <c>default:</c> のラベルで区切り、
    /// ラベル名（<c>X</c> または <c>default</c>）→ 次のラベルまでの節、を返す。
    /// </summary>
    private static Dictionary<string, string> SplitSwitchSections(string codeOnlyBody)
    {
        var labels = System.Text.RegularExpressions.Regex.Matches(
                codeOnlyBody, @"\bcase\s+ToastPosition\.(?<name>\w+)\s*:|\bdefault\s*:")
            .Cast<System.Text.RegularExpressions.Match>()
            .ToList();
        var sections = new Dictionary<string, string>();
        for (var i = 0; i < labels.Count; i++)
        {
            var start = labels[i].Index + labels[i].Length;
            var end = i + 1 < labels.Count ? labels[i + 1].Index : codeOnlyBody.Length;
            var name = labels[i].Groups["name"].Success ? labels[i].Groups["name"].Value : "default";
            sections[name] = codeOnlyBody.Substring(start, end - start);
        }

        return sections;
    }

    /// <summary>節の中の <c>{property} = …;</c> がちょうど 1 つならその右辺を、そうでなければ null を返す。</summary>
    private static string? GetSingleAssignment(string section, string property)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            section, $@"(?<![\w.]){property}\s*=(?!=)\s*(?<value>[^;]+);");
        return matches.Count == 1 ? matches[0].Groups["value"].Value.Trim() : null;
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
