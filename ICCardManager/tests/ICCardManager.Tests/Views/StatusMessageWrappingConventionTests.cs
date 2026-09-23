using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using ICCardManager.Tests.Views.Helpers;
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
/// **走査対象はファイル名で列挙せず <c>Views/</c> 配下の全 XAML から導出する**。画面が追加されたときに
/// 検査から静かに漏れるのを防ぐため（#1786）。あわせて**記述形式の軸でも導出する** —
/// 同じ性質は開始タグの属性のほか <c>&lt;Run Text="{Binding …}"/&gt;</c>・
/// <c>&lt;TextBlock.Text&gt;</c> プロパティ要素・<c>&lt;Setter Property="Text" …/&gt;</c> でも表現でき、
/// 折り返しも <c>&lt;Setter Property="TextWrapping" …/&gt;</c> で与えられる（いずれも本リポジトリに実在）。
/// 開始タグの属性だけを見る形にすると、**同じ欠陥が別の書き方でガードを素通りする**うえ、
/// Setter で折り返している要素を違反と**誤検出**して修正者をガードの弱体化へ誘導する（#1786）。
/// 走査は <see cref="XamlElementInspection"/> へ集約し、私的コピーを増やさない（testing.md）。
/// </para>
/// <para>
/// **検査できない範囲**: 外部のリソース辞書で定義した <c>Style</c> から折り返しを与える形は、
/// XAML テキスト上では解決できない。現時点で対象プロパティにその形は無いが、
/// 使うと誤検出になる（見ていない範囲は「見ていない」と明示する）。
/// </para>
/// <para>
/// **対象プロパティは名前の列挙ではなく命名の規則から導出する**（Issue #2102）。
/// 旧版は <c>StatusMessage</c> / <c>ValidationMessage</c> / <c>ErrorMessage</c> / <c>WarningMessage</c> の
/// 4 つの許可リストだけを見ており、<c>ReportDialog</c> の <c>PreflightWarningText</c>（帳票作成前の
/// 警告文）から折り返しを消しても緑だった — 画面ではなく**プロパティ名**を列挙した形も、
/// ファイル名の列挙と同じ漏れ方をする（#1786）。規則（<see cref="MessagePropertyPattern"/>）は
/// 本リポジトリの <c>Views/**/*.xaml</c> で <c>TextBlock</c> にバインドされている名前を調べて決めた:
/// </para>
/// <list type="bullet">
/// <item><c>…Message</c> で終わる名前は、ViewModel が状況に応じて組み立てる文言（ステータス・検証・
/// 警告・案内）に使われている（<c>StatusMessage</c> / <c>CountdownMessage</c> / <c>EmptyStateMessage</c> /
/// <c>ReturnHistoryReviewMessage</c> …）</item>
/// <item><c>Warning</c> / <c>Error</c> を含み <c>…Text</c> で終わる名前は、警告・エラーの文言
/// （<c>PreflightWarningText</c>）。<c>WarningIcon</c> のような文言でない名前は <c>Text</c> で終わらないので除かれる</item>
/// <item>それ以外の <c>…Text</c>（<c>BackupFolderText</c> / <c>LastRefreshText</c> …）は値や日時の表示で、
/// 3 要素の文言ではないため対象にしない</item>
/// </list>
/// <para>
/// 規則に一致しても対象外とする名前は <see cref="ExcludedProperties"/> に理由とともに置く。
/// <c>BusyMessage</c>（処理中オーバーレイの固定文言）や <c>HistoryStatusMessage</c>
/// （「1～20件を表示（全123件）」という定型の件数表示）、<c>MainWindow</c> の
/// <c>NextActionMessage</c>（「職員証をタッチしてください」等の短い案内）は 3 要素の文言ではない。
/// とくに後ろの 2 つは**横方向 <c>StackPanel</c> の中にあり <c>TextWrapping</c> が機能しない**（#1687。
/// <c>NextActionMessage</c> は属性を持つが効いていない）。機能しない属性を検査で強制すると
/// 「緑だが守っていない」状態を作るので対象に含めない。これらを対象へ戻す前に、
/// 親パネルを幅の制約があるもの（<c>DockPanel</c> / <c>Grid</c>）へ変えること。
/// 除外した名前が画面から消えたら除外も外す（<see cref="除外したプロパティが実在すること"/>）。
/// </para>
/// </remarks>
public class StatusMessageWrappingConventionTests
{
    /// <summary>
    /// 折り返しを必須とする文言系プロパティの命名規則（導出の理由はクラスの remarks を参照）。
    /// </summary>
    private static readonly Regex MessagePropertyPattern = new(
        @"^(?:[A-Za-z0-9_]*Message|[A-Za-z0-9_]*(?:Warning|Error)[A-Za-z0-9_]*Text)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 規則に一致するが対象外とするプロパティ（理由はクラスの remarks を参照）。
    /// </summary>
    private static readonly string[] ExcludedProperties =
    {
        "BusyMessage",
        "HistoryStatusMessage",
        "NextActionMessage",
    };

    private static bool IsMessageProperty(string? name)
        => name != null
           && MessagePropertyPattern.IsMatch(name)
           && !ExcludedProperties.Contains(name, StringComparer.Ordinal);

    /// <summary>折り返しとして認める <c>TextWrapping</c> の値。</summary>
    private static readonly string[] WrappingValues = { "Wrap", "WrapWithOverflow" };

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
    /// 「対象が非空であること」だけを見ると、正規表現が縮んで 1 件も拾わなくなった状態を検出できない（#1786）。
    /// 検査ロジック自体の固定は <see cref="検査ロジックが違反と適合をサンプル入力で区別すること"/> が担う。
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
            // Issue #2102: 許可リストの外にあった警告文。規則から導出されて対象に入ること
            "ReportDialog.xaml|PreflightWarningText",
        });
    }

    /// <summary>
    /// 除外したプロパティが実際に画面にあること（除外が古くなって、同じ名前の新しい文言を
    /// 黙って対象から外すことを防ぐ）。
    /// </summary>
    [Fact]
    public void 除外したプロパティが実在すること()
    {
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateViewXamlFiles())
        {
            var source = XamlElementInspection.StripXmlComments(File.ReadAllText(file));
            foreach (var element in XamlElementInspection.EnumerateElements(source, "TextBlock"))
            {
                foreach (var name in EnumerateBoundTextProperties(element))
                {
                    bound.Add(name);
                }
            }
        }

        bound.Should().Contain(ExcludedProperties,
            "画面から消えた名前は ExcludedProperties から外す（残すと、同じ名前で追加された文言が検査から漏れる）");
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

        // WrapWithOverflow も折り返しとして認める
        Scan(@"<TextBlock Text=""{Binding StatusMessage}"" TextWrapping=""WrapWithOverflow""/>")
            .Should().ContainSingle().Which.HasWrap.Should().BeTrue();

        // Path= 付き・ドット付きパス・単引用符でも拾う（黙って捨てない）
        Scan(@"<TextBlock Text=""{Binding Path=ValidationMessage}""/>")
            .Should().ContainSingle().Which.Property.Should().Be("ValidationMessage");
        Scan(@"<TextBlock Text=""{Binding DataContext.StatusMessage, RelativeSource={RelativeSource Self}}""/>")
            .Should().ContainSingle().Which.Property.Should().Be("StatusMessage");
        Scan(@"<TextBlock Text='{Binding WarningMessage}'/>")
            .Should().ContainSingle().Which.Property.Should().Be("WarningMessage");

        // 属性値に > を含んでいても、開始タグを途中で切らない
        Scan(@"<TextBlock ToolTip=""1 > 0"" Text=""{Binding StatusMessage}"" TextWrapping=""Wrap""/>")
            .Should().ContainSingle().Which.HasWrap.Should().BeTrue();

        // Run で文言を出す形も拾い、折り返しは外側の TextBlock で判定する
        Scan(@"<TextBlock><Run Text=""{Binding StatusMessage}""/></TextBlock>")
            .Should().ContainSingle().Which.HasWrap.Should().BeFalse();
        Scan(@"<TextBlock TextWrapping=""Wrap""><Run Text=""{Binding StatusMessage}""/></TextBlock>")
            .Should().ContainSingle().Which.HasWrap.Should().BeTrue();

        // Style の Setter で Text / TextWrapping を与える形も拾う
        Scan(@"<TextBlock Text=""{Binding StatusMessage}"">
                   <TextBlock.Style><Style TargetType=""TextBlock"">
                       <Setter Property=""TextWrapping"" Value=""Wrap""/>
                   </Style></TextBlock.Style>
               </TextBlock>")
            .Should().ContainSingle().Which.HasWrap.Should().BeTrue();
        Scan(@"<TextBlock><TextBlock.Style><Style TargetType=""TextBlock"">
                   <Setter Property=""Text"" Value=""{Binding ErrorMessage}""/>
               </Style></TextBlock.Style></TextBlock>")
            .Should().ContainSingle().Which.Property.Should().Be("ErrorMessage");

        // TextBlock.Text プロパティ要素も拾う
        Scan(@"<TextBlock><TextBlock.Text><Binding Path=""StatusMessage""/></TextBlock.Text></TextBlock>")
            .Should().ContainSingle().Which.Property.Should().Be("StatusMessage");

        // 規約の理由を書いたコメント自体を違反として拾わない（極性の反転、#1692）
        Scan(@"<!-- StatusMessage には TextWrapping を付けない、ではなく付けること -->
               <TextBlock Text=""{Binding OtherText}""/>")
            .Should().BeEmpty();

        // 対象外のプロパティは拾わない
        Scan(@"<TextBlock Text=""{Binding BusyMessage}""/>").Should().BeEmpty();
        Scan(@"<TextBlock Text=""{Binding HistoryStatusMessage}""/>").Should().BeEmpty();
        Scan(@"<TextBlock Text=""{Binding BackupFolderText}""/>").Should().BeEmpty("値の表示は 3 要素の文言ではない");
        Scan(@"<TextBlock Text=""{Binding WarningIcon}""/>").Should().BeEmpty("文言でない名前は Text で終わらない");

        // 名前を列挙していない文言も規則から拾う（Issue #2102）
        Scan(@"<TextBlock Text=""{Binding PreflightWarningText}""/>")
            .Should().ContainSingle().Which.HasWrap.Should().BeFalse();
        Scan(@"<TextBlock Text=""{Binding ImportResultMessage}""/>")
            .Should().ContainSingle().Which.Property.Should().Be("ImportResultMessage");
        Scan(@"<TextBlock Text=""{Binding SaveErrorText}""/>")
            .Should().ContainSingle().Which.Property.Should().Be("SaveErrorText");

        // Text 以外のバインド（DataTrigger 等）は拾わない
        Scan(@"<DataTrigger Binding=""{Binding StatusMessage}"" Value=""""/>").Should().BeEmpty();

        // 行番号はコメントを挟んでもずれない
        Scan("<!-- 1 行目\n2 行目 -->\n<TextBlock Text=\"{Binding StatusMessage}\"/>")
            .Should().ContainSingle().Which.Line.Should().Be(3);
    }

    private static IReadOnlyList<MessageTextBlock> EnumerateMessageTextBlocks()
    {
        return EnumerateViewXamlFiles()
            .SelectMany(file => Scan(File.ReadAllText(file), file))
            .ToList();
    }

    private static IReadOnlyList<string> EnumerateViewXamlFiles()
    {
        var viewsRoot = Path.Combine(TestPaths.GetProductionSourceRoot(), "Views");
        Directory.Exists(viewsRoot).Should().BeTrue($"View のソースルート {viewsRoot} が存在すべき");

        return Directory
            .EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<MessageTextBlock> Scan(string xaml, string file = "(sample)")
    {
        var source = XamlElementInspection.StripXmlComments(xaml);

        var results = new List<MessageTextBlock>();
        foreach (var element in XamlElementInspection.EnumerateElements(source, "TextBlock"))
        {
            var property = FindMessageProperty(element);
            if (property == null)
            {
                continue;
            }

            results.Add(new MessageTextBlock(
                File: file,
                Line: element.Line,
                Property: property,
                HasWrap: HasWrapping(element)));
        }

        return results;
    }

    /// <summary>
    /// この <c>TextBlock</c> が表示する対象プロパティ名を返す（どの記述形式でも拾う）。
    /// </summary>
    private static string? FindMessageProperty(XamlElementInspection.XamlElement element)
        => EnumerateBoundTextProperties(element).FirstOrDefault(IsMessageProperty);

    /// <summary>
    /// この <c>TextBlock</c> の文言にバインドされているプロパティ名を、記述形式を問わず列挙する。
    /// </summary>
    private static IEnumerable<string> EnumerateBoundTextProperties(XamlElementInspection.XamlElement element)
    {
        // ① 開始タグの Text 属性
        var candidates = new List<string?>
        {
            XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(element.StartTag, "Text")),
            // ② <Setter Property="Text" Value="{Binding …}"/>
            XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetSetterValue(element.Body, "Text")),
        };

        // ③ 本体の <Run Text="{Binding …}"/>
        foreach (var run in XamlElementInspection.EnumerateElements(element.Body, "Run"))
        {
            candidates.Add(XamlElementInspection.GetBindingPropertyName(
                XamlElementInspection.GetAttribute(run.StartTag, "Text")));
        }

        // ④ <TextBlock.Text> プロパティ要素の中の <Binding Path="…"/>
        foreach (var textProperty in XamlElementInspection.EnumerateElements(element.Body, "TextBlock.Text"))
        {
            foreach (var binding in XamlElementInspection.EnumerateElements(textProperty.Body, "Binding"))
            {
                candidates.Add(XamlElementInspection.GetAttribute(binding.StartTag, "Path"));
            }
        }

        return candidates
            .Select(NormalizePropertyName)
            .Where(p => p != null)
            .Select(p => p!);
    }

    /// <summary>パス表記（<c>DataContext.StatusMessage</c>）を最後の区切りへ正規化する。</summary>
    private static string? NormalizePropertyName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var last = path!.Split('.').Last();
        return Regex.IsMatch(last, @"^[A-Za-z_][A-Za-z0-9_]*$") ? last : null;
    }

    private static bool HasWrapping(XamlElementInspection.XamlElement element)
    {
        var fromAttribute = XamlElementInspection.GetAttribute(element.StartTag, "TextWrapping");
        var fromSetter = XamlElementInspection.GetSetterValue(element.Body, "TextWrapping");

        return WrappingValues.Contains(fromAttribute, StringComparer.Ordinal)
               || WrappingValues.Contains(fromSetter, StringComparer.Ordinal);
    }

    private sealed record MessageTextBlock(string File, int Line, string Property, bool HasWrap);
}
