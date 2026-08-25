using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Common.Charting;
using Xunit;

namespace ICCardManager.Tests.Common.Charting;

/// <summary>
/// 同名系列の一意化（Issue #1886）
/// </summary>
/// <remarks>
/// 代替一覧はスウォッチを持たない色以外のチャネル（Issue #1856）なので、
/// ラベルが唯一の判別手段になる。同姓同名の 2 行がそこで判別できることを固定する。
/// </remarks>
public class ChartSeriesLabelDisambiguatorTests
{
    private static ChartSeriesLabelSource Source(string baseName, string qualifier = null)
        => new ChartSeriesLabelSource { BaseName = baseName, Qualifier = qualifier };

    private static IReadOnlyList<string> Run(params ChartSeriesLabelSource[] sources)
        => ChartSeriesLabelDisambiguator.DisambiguateDuplicateNames(sources);

    [Fact]
    public void 重複が無ければ職員番号を添えないこと()
    {
        // 常に添えると、同姓同名が居ない通常運用でラベルが長くなるだけで情報量は増えない。
        Run(Source("福岡 太郎", "A001"), Source("博多 花子", "A002"))
            .Should().Equal(new[] { "福岡 太郎", "博多 花子" });
    }

    [Fact]
    public void 同姓同名は職員番号で判別できること()
    {
        Run(Source("福岡 太郎", "A001"), Source("福岡 太郎", "A002"))
            .Should().Equal(new[] { "福岡 太郎（職員番号 A001）", "福岡 太郎（職員番号 A002）" });
    }

    [Fact]
    public void 職員番号が無い側は素の氏名のまま残すこと()
    {
        // 番号を添えた時点で他と重複しなくなるので、番号を持たない側に通し番号は要らない。
        // 「必要なときだけ修飾する」の帰結。
        Run(Source("福岡 太郎", "A001"), Source("福岡 太郎"))
            .Should().Equal(new[] { "福岡 太郎（職員番号 A001）", "福岡 太郎" });
    }

    [Fact]
    public void 職員番号を持たない同名同士は通し番号で判別できること()
    {
        // lender_idm を持たない過去のインポート行は職員マスタを引けず職員番号が無い。
        // それでも一意にならなければ、代替一覧では判別できないままになる。
        Run(Source("旧職員 A"), Source("旧職員 A"), Source("旧職員 A"))
            .Should().Equal(new[] { "旧職員 A（1 人目）", "旧職員 A（2 人目）", "旧職員 A（3 人目）" });
    }

    [Fact]
    public void 通し番号は重複した組の全員に添えること()
    {
        // 片方だけ「（2 人目）」にすると、修飾の無い側が「1 人目」なのか
        // 無関係な系列なのかを利用者が読み取れない。
        var labels = Run(Source("（職員名なし）"), Source("（職員名なし）"));

        labels.Should().OnlyContain(l => l.StartsWith("（職員名なし）（", StringComparison.Ordinal));
    }

    [Fact]
    public void 職員番号まで重複していれば通し番号まで進むこと()
    {
        // 職員番号は任意入力で、運用ミスによる重複があり得る。
        // そこで打ち切ると、防ごうとしている「同一ラベルが 2 行」に戻る。
        Run(Source("福岡 太郎", "A001"), Source("福岡 太郎", "A001"))
            .Should().Equal(new[]
            {
                "福岡 太郎（職員番号 A001）（1 人目）",
                "福岡 太郎（職員番号 A001）（2 人目）"
            });
    }

    [Fact]
    public void 空白だけの職員番号は識別情報として扱わないこと()
    {
        Run(Source("福岡 太郎", "   "), Source("福岡 太郎", null))
            .Should().Equal(new[] { "福岡 太郎（1 人目）", "福岡 太郎（2 人目）" });
    }

    [Fact]
    public void 職員番号の前後の空白は落として表示すること()
    {
        Run(Source("福岡 太郎", " A001 "), Source("福岡 太郎", "A002"))
            .Should().Equal(new[] { "福岡 太郎（職員番号 A001）", "福岡 太郎（職員番号 A002）" });
    }

    [Fact]
    public void 入力の並びと件数を保つこと()
    {
        // 呼び出し元は添字で系列と突き合わせる。並べ替えや間引きが起きると
        // 別の職員のラベルが付く（#1857 と同じ「添字が何を数えているか」の問題）。
        var labels = Run(Source("C"), Source("A"), Source("A"), Source("B"));

        labels.Should().HaveCount(4);
        labels[0].Should().Be("C");
        labels[3].Should().Be("B");
    }

    [Fact]
    public void 修飾後も全体で一意になること()
    {
        var labels = Run(
            Source("福岡 太郎", "A001"),
            Source("福岡 太郎", "A001"),
            Source("福岡 太郎"),
            Source("博多 花子"),
            Source("（職員名なし）"),
            Source("（職員名なし）"));

        labels.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 空の入力を受け付けること()
    {
        Run().Should().BeEmpty();
    }

    [Fact]
    public void 入力がnullなら例外を投げること()
    {
        Action act = () => ChartSeriesLabelDisambiguator.DisambiguateDuplicateNames(null);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 要素がnullなら例外を投げること()
    {
        // 黙って空文字へ丸めると、無関係な系列が「（職員名なし）」側の重複に混ざる。
        Action act = () => ChartSeriesLabelDisambiguator.DisambiguateDuplicateNames(
            new ChartSeriesLabelSource[] { Source("A"), null });

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 基底名は必ずラベルの先頭に残ること()
    {
        // 対の表明。衝突回避のために氏名そのものを置き換えてしまうと、
        // 誰の系列なのかが読み取れなくなる。
        Run(Source("福岡 太郎", "A001"), Source("福岡 太郎"))
            .Should().OnlyContain(l => l.StartsWith("福岡 太郎", StringComparison.Ordinal));
    }
}
