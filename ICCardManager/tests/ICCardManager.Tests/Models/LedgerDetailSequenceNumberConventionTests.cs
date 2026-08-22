using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Services;
using ICCardManager.Tests.Views.Helpers;
using Xunit;

// 名前空間は ICCardManager.Tests.Domain とする。
// ICCardManager.Tests.Models を作ると、他テストの `Models.LedgerDetail` 等の参照が
// 本番の ICCardManager.Models ではなくこちらへ束縛されてビルドが壊れる。
namespace ICCardManager.Tests.Domain;

/// <summary>
/// Issue #1822: <see cref="LedgerDetail.SequenceNumber"/> の XML doc が実装と逆の規約
/// （「小さい値ほど先（古い）の利用」）を主張していた回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 実装側の合意は「FeliCa 互換で<b>小さい値ほど新しい</b>」（Issue #548 / #880）。
/// 誤った doc を信じて新しい集計を書くと「最新明細＝最小 SequenceNumber」を取り違え、
/// 摘要の順序や最新残高が逆転する。実行時の不具合は起こさないが、次に読む人を誤らせる。
/// </para>
/// <para>
/// 挙動側（<c>SummaryGenerator.SortChronologically</c>）と doc の両方を対で固定する。
/// 挙動テストだけでは doc の退行を検出できず、doc テストだけでは実装が逆へ変わったときに
/// 「doc は正しいが実装が違う」状態を見逃すため。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class LedgerDetailSequenceNumberConventionTests : IDisposable
{
    private readonly SummaryGenerator _generator;

    public LedgerDetailSequenceNumberConventionTests()
    {
        SummaryGenerator.ResetToDefaults();
        _generator = new SummaryGenerator();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 小さい SequenceNumber が「新しい利用」として扱われ、摘要は古い順に並ぶこと。
    /// </summary>
    /// <remarks>
    /// UseDate と Balance を同値にして、順序の決定要因を SequenceNumber だけに絞っている。
    /// doc が主張していた「小さい値ほど古い」を実装したなら、結果は逆順の
    /// 「鉄道（薬院～大橋、博多～天神）」になる。
    /// </remarks>
    [Fact]
    public void 小さいSequenceNumberが新しい利用として時系列の後ろに並ぶこと()
    {
        var sameMoment = new DateTime(2026, 2, 10, 10, 0, 0);
        var details = new List<LedgerDetail>
        {
            // 新しい利用（小さい SequenceNumber）
            new LedgerDetail
            {
                EntryStation = "薬院",
                ExitStation = "大橋",
                Amount = 210,
                UseDate = sameMoment,
                Balance = 1000,
                SequenceNumber = 1
            },
            // 古い利用（大きい SequenceNumber）
            new LedgerDetail
            {
                EntryStation = "博多",
                ExitStation = "天神",
                Amount = 260,
                UseDate = sameMoment,
                Balance = 1000,
                SequenceNumber = 2
            }
        };

        var result = _generator.Generate(details);

        result.Should().Be(
            "鉄道（博多～天神、薬院～大橋）",
            "FeliCa 互換で小さい SequenceNumber ＝ 新しい利用のため、摘要は古い順（大きい Seq が先）になる");
    }

    /// <summary>
    /// XML doc が実装と同じ規約を述べていること（正しい形の存在）。
    /// </summary>
    [Fact]
    public void SequenceNumberのXmlDocが小さい値ほど新しいと述べていること()
    {
        var doc = ExtractSequenceNumberDoc();

        doc.Should().Contain(
            "小さい値ほど新しい",
            "実装（SummaryGenerator.SortChronologically の降順ソート、LendingService の Reverse()）が" +
            "前提にしている規約を doc で明示すること（Issue #1822）");
    }

    /// <summary>
    /// 逆の主張が再導入されていないこと（禁止された形の不在）。
    /// </summary>
    /// <remarks>
    /// 「正しい形の存在」だけを見ると、両方の主張が併記された doc でも緑になる。
    /// </remarks>
    [Theory]
    [InlineData("小さい値ほど先（古い）")]
    [InlineData("小さい値ほど古い")]
    public void SequenceNumberのXmlDocが逆の規約を述べていないこと(string forbidden)
    {
        var doc = ExtractSequenceNumberDoc();

        doc.Should().NotContain(
            forbidden,
            "実装は「小さい値ほど新しい」（FeliCa 互換）。逆の記述は次に集計を書く人を誤らせる（Issue #1822）");
    }

    /// <summary>
    /// <see cref="LedgerDetail.SequenceNumber"/> 宣言直前の XML doc コメント塊を取り出す。
    /// </summary>
    /// <remarks>
    /// 抽出範囲が縮んで空振りしないよう、呼び出し側ではなくここで最低限の妥当性
    /// （remarks を含むこと）を表明する。
    /// </remarks>
    private static string ExtractSequenceNumberDoc()
    {
        var path = ViewSourceLocator.Resolve(Path.Combine("Models", "LedgerDetail.cs"));
        var source = File.ReadAllText(path);

        const string declaration = "public int SequenceNumber { get; set; }";
        var declarationIndex = source.IndexOf(declaration, StringComparison.Ordinal);
        declarationIndex.Should().BeGreaterThan(0, "LedgerDetail.SequenceNumber の宣言が見つからない");

        var summaryIndex = source.LastIndexOf("/// <summary>", declarationIndex, StringComparison.Ordinal);
        summaryIndex.Should().BeGreaterThan(0, "SequenceNumber の XML doc が見つからない");

        var doc = source.Substring(summaryIndex, declarationIndex - summaryIndex);
        doc.Should().Contain("<remarks>", "抽出した範囲が SequenceNumber の doc 本体であること");
        return doc;
    }
}
