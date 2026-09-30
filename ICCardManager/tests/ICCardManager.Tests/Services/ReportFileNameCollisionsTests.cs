using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Dtos;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2154: 帳票ファイル名の衝突判定 <see cref="ReportFileNameCollisions"/> の単体テスト
/// </summary>
/// <remarks>
/// ファイル名は本番の <see cref="ReportFileNameFactory"/> で組み立てる。テスト側で名前を直書きすると、
/// 命名規則（サニタイズ・書式）を変えたときに実装とテストが揃って壊れず、衝突の判定がずれても気付けない。
/// </remarks>
public class ReportFileNameCollisionsTests
{
    private const int FiscalYear = 2026;

    private static readonly ReportFileNameFactory Factory = new();

    private static ReportExportTarget Card(string idm, string number, string type = "はやかけん") =>
        new() { CardIdm = idm, CardType = type, CardNumber = number };

    private static IReadOnlyDictionary<string, IReadOnlyList<ReportExportTarget>> Find(params ReportExportTarget[] cards) =>
        FindWithTargets(null, cards);

    private static IReadOnlyDictionary<string, IReadOnlyList<ReportExportTarget>> FindWithTargets(
        IEnumerable<string> targetIdms, params ReportExportTarget[] cards) =>
        ReportFileNameCollisions.Find(
            cards,
            FiscalYear,
            (cardType, cardNumber) => Factory.GetFiscalYearFileName(cardType, cardNumber, FiscalYear),
            targetIdms);

    private static ReportExportTarget Refunded(string idm, string number, DateTime? refundedAt) =>
        new() { CardIdm = idm, CardType = "はやかけん", CardNumber = number, IsRefunded = true, RefundedAt = refundedAt };

    /// <summary>
    /// 欠陥を突く側: ファイル名に使えない別々の記号は同じ「_」に落ちるため衝突とみなす
    /// </summary>
    [Fact]
    public void 使えない記号が同じ置換文字に落ちる2枚は衝突すること()
    {
        var result = Find(Card("01", "A*B"), Card("02", "A?B"));

        result.Keys.Should().BeEquivalentTo("01", "02");
        result["01"].Select(c => c.CardIdm).Should().Equal("02");
        result["02"].Select(c => c.CardIdm).Should().Equal("01");
    }

    /// <summary>
    /// 欠陥を突く側: Windows はファイル名の大文字・小文字を区別しないため、管理番号の大小違いは衝突とみなす
    /// </summary>
    /// <remarks>
    /// 管理番号の入力規則は英大文字・小文字を両方許し、DB の一意インデックスは大文字・小文字を区別するため、
    /// 画面から登録できる形である。生成名の文字列一致で判定すると、この経路を見落とす。
    /// </remarks>
    [Fact]
    public void 管理番号の大文字小文字だけが違う2枚は衝突すること()
    {
        var result = Find(Card("01", "H001"), Card("02", "h001"));

        result.Keys.Should().BeEquivalentTo("01", "02");
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 通常の管理番号では衝突しない
    /// </summary>
    [Fact]
    public void 管理番号が異なれば衝突しないこと()
    {
        Find(Card("01", "H-001"), Card("02", "H-002"), Card("03", "003")).Should().BeEmpty();
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 管理番号が同じでもカード種別が違えば別のファイルになる
    /// </summary>
    /// <remarks>
    /// 管理番号はカード種別と組でしか一意でない（<c>idx_card_type_number_active</c>）。
    /// 管理番号だけで比べると、正当に登録された 2 枚を衝突と誤判定して帳票を作らせなくなる。
    /// </remarks>
    [Fact]
    public void カード種別が違えば同じ管理番号でも衝突しないこと()
    {
        Find(Card("01", "001", "はやかけん"), Card("02", "001", "nimoca")).Should().BeEmpty();
    }

    /// <summary>
    /// 母集団に同じカードが二重に入っていても、自分自身との衝突として数えない
    /// </summary>
    /// <remarks>
    /// 呼び出し元は「画面の全カード」と「作成対象」を連結して渡すため、同じ IDm が 2 回現れるのが通常である。
    /// </remarks>
    [Fact]
    public void 同じIDmが二重に入っていても自分自身とは衝突しないこと()
    {
        Find(Card("01", "H001"), Card("01", "H001"), Card("02", "H002")).Should().BeEmpty();
    }

    /// <summary>
    /// 3 枚以上が同じ名前に落ちたら、各カードの相手は自分以外の全員になる
    /// </summary>
    [Fact]
    public void 三枚が衝突したら各カードに他の二枚を返すこと()
    {
        var result = Find(Card("01", "A*B"), Card("02", "A?B"), Card("03", "a_b"), Card("04", "C-001"));

        result.Keys.Should().BeEquivalentTo("01", "02", "03");
        result["02"].Select(c => c.CardIdm).Should().Equal("01", "03");
    }

    /// <summary>
    /// ファイル名を得られないカード（生成関数が null を返す）は判定から除く
    /// </summary>
    [Fact]
    public void ファイル名が得られないカードは衝突に数えないこと()
    {
        var result = ReportFileNameCollisions.Find(
            new[] { Card("01", "A"), Card("02", "B") },
            FiscalYear,
            (_, _) => null);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// 年度の初日より前に払い戻したカードは、その年度の年度ファイルを持たないので衝突に数えない
    /// </summary>
    /// <remarks>
    /// 数えると、何年も前に払い戻したカードのために稼働中のカードの帳票が毎月作られなくなる（コードレビューで検出）。
    /// </remarks>
    [Fact]
    public void 年度より前に払い戻したカードとは衝突しないこと()
    {
        Find(Card("01", "H001"), Refunded("02", "h001", new DateTime(FiscalYear, 3, 31, 23, 59, 0))).Should().BeEmpty();
    }

    /// <summary>
    /// 対: その年度の中で払い戻したカードは、その年度の年度ファイルを持ち得るので衝突に数える
    /// </summary>
    [Fact]
    public void 年度の中で払い戻したカードとは衝突すること()
    {
        Find(Card("01", "H001"), Refunded("02", "h001", new DateTime(FiscalYear, 4, 1))).Keys
            .Should().BeEquivalentTo("01", "02");
    }

    /// <summary>
    /// 払戻日が記録されていない払戻済みカードは、取りこぼすと上書きが黙って起きるので衝突に数える
    /// </summary>
    [Fact]
    public void 払戻日が無い払戻済みカードとは衝突すること()
    {
        Find(Card("01", "H001"), Refunded("02", "h001", null)).Keys.Should().BeEquivalentTo("01", "02");
    }

    /// <summary>
    /// 今回帳票を作るカードは、払戻日にかかわらず数える（作る以上、その年度ファイルへ書き込む）
    /// </summary>
    [Fact]
    public void 作成対象なら年度より前に払い戻したカードでも衝突に数えること()
    {
        FindWithTargets(new[] { "02" }, Card("01", "H001"), Refunded("02", "h001", new DateTime(FiscalYear - 3, 5, 1)))
            .Keys.Should().BeEquivalentTo("01", "02");
    }

    [Fact]
    public void 原因_大文字小文字だけの違いならそう名指しすること()
    {
        ReportFileNameCollisions.DescribeCause(Card("01", "H001"), new[] { Card("02", "h001") })
            .Should().Contain("大文字と小文字").And.NotContain("記号");
    }

    [Fact]
    public void 原因_記号の違いなら置き換えを名指しすること()
    {
        ReportFileNameCollisions.DescribeCause(Card("01", "A*B"), new[] { Card("02", "a?b") })
            .Should().Contain("「_」に置き換わる").And.NotContain("大文字と小文字");
    }

    [Fact]
    public void 衝突相手の名前をかぎ括弧で列挙すること()
    {
        ReportFileNameCollisions.FormatCardNames(new[] { Card("01", "A*B"), Card("02", "a?b", "nimoca") })
            .Should().Be("「はやかけん A*B」、「nimoca a?b」");
    }
}
