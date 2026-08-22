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
