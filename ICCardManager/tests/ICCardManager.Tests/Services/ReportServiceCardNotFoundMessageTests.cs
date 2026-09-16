using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2049: 帳票作成の対象の交通系ICカードが DB に見つからないときの失敗文言とログを検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は「カード情報が見つかりません」「指定されたカード（IDm: 0123456789ABCDEF）は登録されていません。」と
/// 生の IDm を職員向けの文言に出し、行動指示も無かった（#1852 / #1986 の CWE-532 回避、#1275 の 3 要素）。
/// </para>
/// <para>
/// 経路は単票作成（<see cref="ReportService.CreateMonthlyReportAsync"/>。画面の一括作成はこれをカードごとに呼ぶ）と
/// サービスの一括 API（<see cref="ReportService.CreateMonthlyReportsAsync"/>）の 2 つあるため、両方を実サービスで通す。
/// 文言の表明とログの表明は別テストに分け、片方だけが壊れたときにテスト名から読み取れるようにする（#1991）。
/// </para>
/// </remarks>
public class ReportServiceCardNotFoundMessageTests : IDisposable
{
    /// <summary>16 進の英字を含む IDm。マスク後の可視部分（先頭・末尾 4 文字）も文言に現れないことを確かめる。</summary>
    private const string UnknownCardIdm = "0123456789ABCDEF";

    private const int Year = 2024;
    private const int Month = 10;

    /// <summary>16 桁の 16 進数（IDm の形）。</summary>
    private static readonly Regex IdmShaped = new Regex("[0-9A-Fa-f]{16}");

    /// <summary>行動指示で終わる形（error-messages.md「行動指示型で終わる」）。</summary>
    private static readonly Regex EndsWithAction = new Regex("してください。?$");

    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock = new();
    private readonly RecordingLogger<ReportService> _logger = new();
    private readonly string _directory;

    public ReportServiceCardNotFoundMessageTests()
    {
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(UnknownCardIdm, true))
            .ReturnsAsync((IcCard)null);
        _ledgerRepositoryMock
            .Setup(r => r.GetByMonthAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<Ledger>());

        _directory = Path.Combine(Path.GetTempPath(), $"ReportCardNotFound_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 後始末の失敗はテスト結果に影響させない
        }
    }

    private ReportService CreateService()
        => new(
            _cardRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            new ReportDataBuilder(_cardRepositoryMock.Object, _ledgerRepositoryMock.Object),
            _logger);

    private string OutputPath() => Path.Combine(_directory, "物品出納簿_はやかけん_001_2024年度.xlsx");

    private async Task<ReportGenerationResult> CreateSingleAsync()
        => await CreateService().CreateMonthlyReportAsync(UnknownCardIdm, Year, Month, OutputPath());

    private async Task<ReportGenerationResult> CreateBatchAsync()
    {
        var batch = await CreateService().CreateMonthlyReportsAsync(new[] { UnknownCardIdm }, Year, Month, _directory);
        batch.Results.Should().ContainSingle();
        return batch.Results.Single().Result;
    }

    /// <summary>文言の品質（IDm を含まない・交通系ICカードと明記・行動指示で終わる）を表明する。</summary>
    private static void AssertMessageQuality(ReportGenerationResult result)
    {
        result.Success.Should().BeFalse();
        result.IsCommonFailure.Should().BeFalse("そのカードだけの問題なので、一括作成は続ける");

        foreach (var (name, text) in new[]
                 {
                     ("ErrorMessage", result.ErrorMessage),
                     ("DetailedErrorMessage", result.DetailedErrorMessage),
                 })
        {
            text.Should().NotBeNullOrWhiteSpace(name);
            IdmShaped.IsMatch(text).Should().BeFalse($"{name} に 16 桁の IDm を出さない: {text}");
            text.Should().NotContain(UnknownCardIdm.Substring(0, 4), $"{name} にはマスク後の可視部分も出さない");
            text.Should().NotContain(UnknownCardIdm.Substring(12), $"{name} にはマスク後の可視部分も出さない");
            text.Should().NotContain("IDm", $"{name} は職員が識別できない技術用語を使わない");
            text.Should().Contain("交通系ICカード", $"{name} は職員証と区別できる呼び方にする");
            text.Should().NotContain("カード情報", $"{name} は「カード情報」ではなく交通系ICカードと明記する");
            EndsWithAction.IsMatch(text).Should().BeTrue($"{name} は行動指示で終わる: {text}");
        }

        // 失敗一覧（ReportViewModel）は ErrorMessage しか表示しないため、見出しにも行動指示が要る。
        // 詳細は「何が／なぜ／どうすれば」を満たす長さを持つ。
        result.DetailedErrorMessage.Length.Should().BeGreaterOrEqualTo(20);
        result.DetailedErrorMessage.Should().Contain("一覧", "なぜ見つからないのか（原因の候補）を示す");
    }

    private void AssertMaskedLog()
    {
        var entries = _logger.Entries;
        var because = _logger.FormatEntries();

        entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains(IdmMasker.Mask(UnknownCardIdm)),
            "障害調査のために、どのカードかをマスクした IDm でログへ残す: " + because);
        entries.Should().NotContain(
            e => e.Message.Contains(UnknownCardIdm),
            "ログにも生の IDm を出さない（#1852）: " + because);
    }

    [Fact]
    public async Task CreateMonthlyReportAsync_未登録カードの文言にIDmを含めず行動指示で終わること()
    {
        var result = await CreateSingleAsync();

        AssertMessageQuality(result);
    }

    [Fact]
    public async Task CreateMonthlyReportAsync_未登録カードはマスクしたIDmをログへ残すこと()
    {
        await CreateSingleAsync();

        AssertMaskedLog();
    }

    [Fact]
    public async Task CreateMonthlyReportsAsync_未登録カードの文言にIDmを含めず行動指示で終わること()
    {
        var result = await CreateBatchAsync();

        AssertMessageQuality(result);
    }

    [Fact]
    public async Task CreateMonthlyReportsAsync_未登録カードはマスクしたIDmをログへ残すこと()
    {
        await CreateBatchAsync();

        AssertMaskedLog();
    }

    /// <summary>
    /// 対の表明: 登録済みのカードではこの失敗を返さず、見つからない旨のログも出さないこと
    /// </summary>
    /// <remarks>
    /// これが無いと、常に「登録されていません」を返す実装やログを常に出す実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportAsync_登録済みカードでは見つからない旨を返さないこと()
    {
        const string registeredIdm = "FEDCBA9876543210";
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(registeredIdm, true))
            .ReturnsAsync(new IcCard { CardIdm = registeredIdm, CardType = "はやかけん", CardNumber = "001" });

        var result = await CreateService().CreateMonthlyReportAsync(registeredIdm, Year, Month, OutputPath());

        var notFound = ReportService.BuildCardNotFoundResult();
        result.ErrorMessage.Should().NotBe(notFound.ErrorMessage, _logger.FormatEntries());
        _logger.Entries.Should().NotContain(e => e.Message.Contains(IdmMasker.Mask(registeredIdm)));
    }

    /// <summary>
    /// 対の表明（一括 API 側）: 登録済みのカードは見つからない旨の結果にしないこと
    /// </summary>
    /// <remarks>
    /// 一括 API はカードの取得を自前で行うため、単票側の対の表明とは別に置く。
    /// </remarks>
    [Fact]
    public async Task CreateMonthlyReportsAsync_登録済みカードでは見つからない旨を返さないこと()
    {
        const string registeredIdm = "FEDCBA9876543210";
        _cardRepositoryMock
            .Setup(r => r.GetByIdmAsync(registeredIdm, true))
            .ReturnsAsync(new IcCard { CardIdm = registeredIdm, CardType = "はやかけん", CardNumber = "001" });

        var batch = await CreateService().CreateMonthlyReportsAsync(new[] { registeredIdm }, Year, Month, _directory);

        batch.Results.Should().ContainSingle();
        var (_, cardName, result) = batch.Results.Single();
        cardName.Should().Be("はやかけん 001", "カードが見つかった経路ではカード名を解決する");
        result.ErrorMessage.Should().NotBe(ReportService.BuildCardNotFoundResult().ErrorMessage, _logger.FormatEntries());
        _logger.Entries.Should().NotContain(e => e.Message.Contains(IdmMasker.Mask(registeredIdm)));
    }
}
