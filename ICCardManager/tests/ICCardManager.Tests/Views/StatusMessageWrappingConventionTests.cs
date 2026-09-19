using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Views;

/// <summary>
/// Issue #2075: ステータス／検証／警告文言を表示する <c>TextBlock</c> が折り返すことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// これらの文言は error-messages.md の「何が／なぜ／どうすれば」3 要素を満たすため長く、
/// <c>ExceptionMessageFormatter.ToUserMessage</c> の結果や保存競合の案内は 100 文字を超える。
/// 折り返しが無いと末尾の「どうすれば」が右端で切れ、職員が回復手段を読めない
/// （ui-conventions.md「長文の可能性があるテキストは『幅』ではなく『折り返し』で担保する」#1687 / #1688）。
/// </para>
/// <para>
/// ViewModel のテストは <c>StatusMessage</c> の値しか見えず、表示領域からはみ出すことを検出できない。
/// 実描画の検証には UI オートメーションが要るため、XAML テキスト上で静的に検証する。
/// </para>
/// <para>
/// 走査対象はファイル名で列挙せず <c>Views/</c> 配下の全 XAML から導出する。画面が追加されたときに
/// 検査から静かに漏れるのを防ぐため（development-conventions.md #1786）。
/// </para>
/// <para>
/// 対象プロパティは Issue #2075 が名指しする「ステータス／検証メッセージ」に限る。
/// <c>BusyMessage</c>（処理中オーバーレイの固定文言）や <c>HistoryStatusMessage</c>
/// （「1～20件を表示（全123件）」という定型の件数表示）は 3 要素の文言ではなく、
/// とくに後者は横方向 <c>StackPanel</c> の中にあるため <c>TextWrapping</c> を付けても機能しない。
/// 機能しない属性を検査で強制すると「緑だが守っていない」状態を作るので対象に含めない。
/// </para>
/// </remarks>
public class StatusMessageWrappingConventionTests
{
    /// <summary>折り返しを必須とするメッセージ系プロパティ。</summary>
    private static readonly string[] MessageProperties =
    {
        "StatusMessage",
        "ValidationMessage",
        "ErrorMessage",
        "WarningMessage",
    };

    /// <summary>
    /// <c>TextBlock</c> の開始タグ。<c>&lt;TextBlock.Style&gt;</c> のようなプロパティ要素に
    /// 一致しないよう、要素名の直後が空白・<c>/</c>・<c>&gt;</c> であることを要求する。
    /// </summary>
    private static readonly Regex TextBlockStartTagPattern =
        new(@"<TextBlock(?=[\s/>])[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex MessageBindingPattern =
        new(@"Text\s*=\s*""\{Binding\s+(?:Path\s*=\s*)?(?<prop>[A-Za-z0-9_]+)\s*[,}]", RegexOptions.Compiled);

    private static readonly Regex XamlCommentPattern =
        new(@"<!--[\s\S]*?-->", RegexOptions.Compiled);

    private static readonly Regex TextWrappingWrapPattern =
        new(@"TextWrapping\s*=\s*""Wrap""", RegexOptions.Compiled);

    [Fact]
    public void ステータス系メッセージのTextBlockはすべて折り返すこと()
    {
        var violations = EnumerateMessageTextBlocks()
            .Where(t => !t.HasWrap)
            .Select(t => $"{t.File}:{t.Line} ({t.Property})")
            .ToList();

        violations.Should().BeEmpty(
            "Issue #2075: 3 要素の文言は右端で切れて「どうすれば」が読めなくなるため、" +
            "TextWrapping=\"Wrap\" を明示すること（違反: " + string.Join(", ", violations) + "）");
    }

    /// <summary>
    /// 走査が空振りしていないことを、Issue #2075 で実際に是正した画面の存在で固定する。
    /// </summary>
    /// <remarks>
    /// 「対象が非空であること」だけを見ると、正規表現が縮んで 1 件も拾わなくなった状態を検出できない
    /// （development-conventions.md #1786）。検査ロジック自体の固定は
    /// <see cref="検査ロジックが違反と適合をサンプル入力で区別すること"/> が担う。
    /// </remarks>
    [Fact]
    public void Issue2075で是正した画面がすべて走査対象に含まれること()
    {
        var found = EnumerateMessageTextBlocks()
            .Select(t => $"{Path.GetFileName(t.File)}|{t.Property}")
            .ToHashSet(StringComparer.Ordinal);

        found.Should().Contain(new[]
        {
            "LedgerRowEditDialog.xaml|ValidationMessage",
            "LedgerRowEditDialog.xaml|StatusMessage",
            "LedgerDetailDialog.xaml|StatusMessage",
            "OperationLogDialog.xaml|StatusMessage",
            "PrintPreviewDialog.xaml|StatusMessage",
        });
    }

    [Fact]
    public void 検査ロジックが違反と適合をサンプル入力で区別すること()
    {
        // 折り返しが無い → 違反として拾う
        Scan(@"<TextBlock Text=""{Binding StatusMessage}"" Foreground=""Red""/>")
            .Should().ContainSingle().Which.HasWrap.Should().BeFalse();

        // 折り返しがある → 拾うが違反ではない
        Scan(@"<TextBlock Text=""{Binding StatusMessage, Mode=OneWay}"" TextWrapping=""Wrap""/>")
            .Should().ContainSingle().Which.HasWrap.Should().BeTrue();

        // Path= 付きのバインドも拾う
        Scan(@"<TextBlock Text=""{Binding Path=ValidationMessage}""/>")
            .Should().ContainSingle().Which.Property.Should().Be("ValidationMessage");

        // 開始タグのみのTextBlock（子要素に TextBlock.Style を持つ形）も拾う
        Scan(@"<TextBlock Text=""{Binding WarningMessage}"">
                   <TextBlock.Style><Style TargetType=""TextBlock""/></TextBlock.Style>
               </TextBlock>")
            .Should().ContainSingle().Which.Property.Should().Be("WarningMessage");

        // 規約の理由を書いたコメント自体を違反として拾わない（極性の反転、#1692）
        Scan(@"<!-- StatusMessage には TextWrapping を付けない、ではなく付けること -->
               <TextBlock Text=""{Binding OtherText}""/>")
            .Should().BeEmpty();

        // 対象外のプロパティは拾わない
        Scan(@"<TextBlock Text=""{Binding BusyMessage}""/>").Should().BeEmpty();

        // Text 以外のバインド（DataTrigger 等）は拾わない
        Scan(@"<DataTrigger Binding=""{Binding StatusMessage}"" Value=""""/>").Should().BeEmpty();
    }

    private static IReadOnlyList<MessageTextBlock> EnumerateMessageTextBlocks()
    {
        var viewsRoot = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views");
        Directory.Exists(viewsRoot).Should().BeTrue($"View のソースルート {viewsRoot} が存在すべき");

        return Directory
            .EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(file => Scan(File.ReadAllText(file), file))
            .ToList();
    }

    private static IReadOnlyList<MessageTextBlock> Scan(string xaml, string file = "(sample)")
    {
        // 規約の理由を書いたコメントが違反として検出されないよう、先にコメントを取り除く（#1692）。
        var withoutComments = XamlCommentPattern.Replace(xaml, match => new string('\n', match.Value.Count(c => c == '\n')));

        var results = new List<MessageTextBlock>();
        foreach (Match tag in TextBlockStartTagPattern.Matches(withoutComments))
        {
            var binding = MessageBindingPattern.Match(tag.Value);
            if (!binding.Success)
            {
                continue;
            }

            var property = binding.Groups["prop"].Value;
            if (!MessageProperties.Contains(property, StringComparer.Ordinal))
            {
                continue;
            }

            results.Add(new MessageTextBlock(
                File: file,
                Line: withoutComments.Take(tag.Index).Count(c => c == '\n') + 1,
                Property: property,
                HasWrap: TextWrappingWrapPattern.IsMatch(tag.Value)));
        }

        return results;
    }

    private sealed record MessageTextBlock(string File, int Line, string Property, bool HasWrap);
}
