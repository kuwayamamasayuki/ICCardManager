using System.IO;
using FluentAssertions;
using ICCardManager.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1820: 帳票ファイル名が組織設定 <c>ReportLayout.FileNameFormat</c> に追従することを検証する。
/// </summary>
/// <remarks>
/// 修正前は <c>ReportService.GetFiscalYearFileName</c> が <c>static</c> で
/// <c>new OrganizationOptions().ReportLayout.FileNameFormat</c> を使っており、設定は無視されていた。
/// <b>既定と異なる書式を設定してから呼ぶ</b>こと（既定のままだとハードコードと偶然一致し、
/// 修正前のコードでも緑になる）。Issue #1818 で確立した消費側テストの作法。
/// </remarks>
public class ReportFileNameFactoryTests
{
    private const string CustomFormat = "出納簿【{0}】{1}（{2}年度）.xlsx";

    private static ReportFileNameFactory CreateFactory(string fileNameFormat)
    {
        var options = new OrganizationOptions();
        options.ReportLayout.FileNameFormat = fileNameFormat;
        return new ReportFileNameFactory(Options.Create(options));
    }

    #region 設定追従

    [Fact]
    public void 組織設定のファイル名フォーマットが反映される()
    {
        var fileName = CreateFactory(CustomFormat)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("出納簿【はやかけん】H001（2024年度）.xlsx");
    }

    [Fact]
    public void 組織設定を変更したとき既定の書式では生成されない()
    {
        // 対のテスト: 「新旧どちらの書式でも通る」広すぎる実装を検出する
        var fileName = CreateFactory(CustomFormat)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().NotBe("物品出納簿_はやかけん_H001_2024年度.xlsx");
        fileName.Should().NotStartWith("物品出納簿_");
    }

    [Fact]
    public void 設定未指定なら既定の書式で生成される()
    {
        var fileName = new ReportFileNameFactory()
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
    }

    [Fact]
    public void プレースホルダの並び順を入れ替えた書式にも従う()
    {
        var fileName = CreateFactory("{2}_{1}_{0}.xlsx")
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("2024_H001_はやかけん.xlsx");
    }

    #endregion

    #region フォールバック（空・不正な書式）

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空の書式は既定へフォールバックする(string format)
    {
        // 空書式のまま string.Format を通すとファイル名が空になり、
        // Path.Combine の結果が「出力フォルダそのもの」になって保存が壊れる
        var fileName = CreateFactory(format)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
    }

    [Theory]
    [InlineData("物品出納簿_{3}.xlsx")]   // 存在しないプレースホルダ
    [InlineData("物品出納簿_{0.xlsx")]    // 閉じ括弧の欠落
    public void 不正なプレースホルダの書式は既定へフォールバックする(string format)
    {
        // 管理者の設定ミスで帳票作成が例外終了しないこと
        var fileName = CreateFactory(format)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
    }

    [Theory]
    [InlineData(@"..\..\evil\{0}_{1}_{2}.xlsx")]
    [InlineData("sub/dir/{0}_{1}_{2}.xlsx")]
    public void 書式自体がパス構造を含む場合は既定へフォールバックする(string format)
    {
        // Issue #1703 の保証（生成名は単一のファイル名）は、構成要素のサニタイズだけでは
        // 書式側から破られる。設定値も sink 側で検査する。
        var fileName = CreateFactory(format)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
        Path.GetFileName(fileName).Should().Be(fileName);
    }

    [Theory]
    [InlineData("物品出納簿_{0}_{1}_{2}年度*.xlsx")]   // ワイルドカード
    [InlineData("物品出納簿_{0}_{1}_{2}年度?.xlsx")]   // ワイルドカード
    [InlineData("物品出納簿_{0}_{1}_{2}年度|.xlsx")]   // パイプ
    public void ファイル名に使えない文字を含む書式は既定へフォールバックする(string format)
    {
        // '*' / '?' は Path.GetInvalidPathChars に含まれないため Path.GetFileName を通り抜ける。
        // ここで倒しておかないと SaveAs の時点で例外になり、帳票作成が止まる。
        var fileName = CreateFactory(format)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()).Should().BeLessThan(0);
    }

    [Fact]
    public void 正当な書式はフォールバックせずそのまま使われる()
    {
        // 対のテスト: 検査が広すぎて正当な書式まで倒していないこと
        var fileName = CreateFactory(CustomFormat)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("出納簿【はやかけん】H001（2024年度）.xlsx");
    }

    #endregion

    #region Issue #2041 カードを区別しない書式・拡張子のない書式

    [Theory]
    [InlineData("物品出納簿_{2}年度.xlsx")]        // カード種別も管理番号も無い
    [InlineData("物品出納簿_{0}_{2}年度.xlsx")]    // 管理番号が無い（種別違いの同番号が衝突する）
    [InlineData("物品出納簿_{1}_{2}年度.xlsx")]    // カード種別が無い（管理番号は種別込みでしか一意でない）
    public void カードを区別しない書式は既定へフォールバックする(string format)
    {
        // 一括作成で全カードが同じファイルへ書かれ、各カードが同じ月シートを
        // 書き直すため、最後の 1 枚以外の台帳が失われる（しかも全件 Success になる）
        var fileName = CreateFactory(format)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
    }

    [Fact]
    public void 年度を区別しない書式は既定へフォールバックする()
    {
        // 年度ファイルは月シートを積み上げるため、年度が名前に出ないと
        // 2024年度の「4月」シートを2025年度の「4月」が上書きする
        var fileName = CreateFactory("物品出納簿_{0}_{1}.xlsx")
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
    }

    [Fact]
    public void 異なるカードは異なるファイル名になる()
    {
        // 不変条件: 「カードを区別する」という性質そのものを表明する。
        // プレースホルダの有無を綴りで見る実装（{0,10} / {0:D} を取りこぼす）への
        // 退行を、書式を問わず検出できる。
        var factory = CreateFactory("物品出納簿_{2}年度.xlsx");

        var a = factory.GetFiscalYearFileName("はやかけん", "H001", 2024);
        var b = factory.GetFiscalYearFileName("nimoca", "H001", 2024);
        var c = factory.GetFiscalYearFileName("はやかけん", "H002", 2024);
        var d = factory.GetFiscalYearFileName("はやかけん", "H001", 2025);

        new[] { a, b, c, d }.Should().OnlyHaveUniqueItems(
            "同じファイル名になると、後から作成したカードの台帳が前のカードのシートを消す");
    }

    [Theory]
    [InlineData("物品出納簿_{0}_{1}_{2}年度")]         // 拡張子なし
    [InlineData("物品出納簿_{0}_{1}_{2}年度.xlsx.")]   // 末尾がピリオド
    [InlineData("物品出納簿_{0}_{1}_{2}年度.csv")]     // Excel 以外の拡張子
    [InlineData("物品出納簿_{0}_{1}_{2}年度.xls")]     // 旧形式（ClosedXML は保存できない）
    [InlineData("物品出納簿_{0}_{1}_{2}年度.xlsm")]    // マクロ有効ブック（中身は通常ブックになる）
    public void Excelの拡張子で終わらない書式は既定へフォールバックする(string format)
    {
        // ClosedXML の SaveAs は拡張子で形式を決めるため、ここで倒しておかないと
        // ArgumentException が汎用 catch に落ちて全カードの帳票作成が失敗する。
        // .xlsm は保存できてしまうが、ReportService は #2040 以降ストリームへ保存しており
        // 拡張子で形式を選ぶ分岐を通らないため、中身が通常ブックのまま拡張子だけマクロ有効に
        // なったファイルができる（Excel が形式の不一致を警告する）。
        var fileName = CreateFactory(format)
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.xlsx");
    }

    [Fact]
    public void 管理番号が拡張子で終わるカードでも書式に拡張子が無ければフォールバックする()
    {
        // 判定はカードごとの生成名ではなく書式に対して行う（#1818「設定値で生成したものは、
        // 設定値で判定する」）。生成名に対して拡張子を検査すると、管理番号がたまたま
        // ".xlsx" で終わるカードだけが検査を通り、同じ設定・同じフォルダーで
        // カードごとに命名規則が食い違う（縮退のログも通らなかったカードでしか出ない）。
        var fileName = CreateFactory("物品出納簿_{2}年度_{0}_{1}")
            .GetFiscalYearFileName("はやかけん", "H001.xlsx", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001.xlsx_2024年度.xlsx");
    }

    [Fact]
    public void 大文字の拡張子の書式は塞がない()
    {
        // 対のテスト: 検査が広すぎて正当な書式まで倒していないこと
        var fileName = CreateFactory("物品出納簿_{0}_{1}_{2}年度.XLSX")
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("物品出納簿_はやかけん_H001_2024年度.XLSX");
    }

    [Fact]
    public void プレースホルダに書式指定子を伴う書式は塞がない()
    {
        // 対のテスト: 綴りで {0} を探す実装だと通らない正当な書式。
        // string.Format の整列指定は string 引数にもそのまま効く。
        var fileName = CreateFactory("出納簿_{0}_{1}_{2:0000}年度.xlsx")
            .GetFiscalYearFileName("はやかけん", "H001", 2024);

        fileName.Should().Be("出納簿_はやかけん_H001_2024年度.xlsx");
    }

    #endregion

    #region Issue #1703 のサニタイズが書式変更後も効くこと

    [Theory]
    [InlineData(@"x\..\..\Users\Public\report", "H001")]
    [InlineData("はやかけん", @"..\..\Users\Public\evil")]
    public void カスタム書式でも構成要素のパス区切りは無害化される(string cardType, string cardNumber)
    {
        var fileName = CreateFactory(CustomFormat)
            .GetFiscalYearFileName(cardType, cardNumber, 2024);

        fileName.Should().NotContain("/");
        fileName.Should().NotContain("\\");
        Path.GetFileName(fileName).Should().Be(fileName);
    }

    #endregion
}
