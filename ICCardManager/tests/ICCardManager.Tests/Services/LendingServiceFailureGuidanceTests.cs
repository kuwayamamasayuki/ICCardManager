#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2328: 貸出・返却が失敗したときの案内が、呼び出し経路から実際に出ること。
/// </summary>
/// <remarks>
/// <para>
/// 文言を組み立てる関数（<see cref="LendingService.BuildLockTimeoutMessage"/> /
/// <see cref="LendingService.GetUserFriendlyErrorMessage"/>）は <c>LendingServiceErrorMessageTests</c> が押さえるが、
/// それだけでは <c>LendAsync</c> / <c>ReturnAsync</c> が旧文言の直書きへ戻ったり、モードを固定値で渡したりしても緑になる
/// （レビュー 2 回目で検出）。ここでは経路を通して、実際に返る <see cref="LendingResult.ErrorMessage"/> を表明する。
/// </para>
/// </remarks>
public sealed class LendingServiceFailureGuidanceTests : IDisposable
{
    private const string CardIdm = "0102030405060708";
    private const string StaffIdm = "1112131415161718";

    private readonly List<DbContext> _dbContexts = new();
    private readonly CardLockManager _lockManager = new(NullLogger<CardLockManager>.Instance);
    private readonly Mock<ICardRepository> _cardRepository = new();
    private readonly Mock<IStaffRepository> _staffRepository = new();
    private readonly Mock<ILedgerRepository> _ledgerRepository = new();
    private readonly Mock<ISettingsRepository> _settingsRepository = new();

    public LendingServiceFailureGuidanceTests()
    {
        _settingsRepository.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _settingsRepository.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _cardRepository.Setup(x => x.GetByIdmAsync(CardIdm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = CardIdm,
            CardType = "はやかけん",
            CardNumber = "H001",
            IsLent = false,
        });
        _staffRepository.Setup(x => x.GetByIdmAsync(StaffIdm, false)).ReturnsAsync(new Staff
        {
            StaffIdm = StaffIdm,
            Name = "テスト職員",
        });
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        foreach (var dbContext in _dbContexts)
        {
            dbContext.Dispose();
        }
    }

    [Fact]
    public async Task 貸出_カードのロックを取れなければ職員証のタッチからやり直すよう案内すること()
    {
        var service = CreateService(isSharedMode: false);

        var result = await WhileLockIsHeldAsync(() => service.LendAsync(StaffIdm, CardIdm));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(LendingService.BuildLockTimeoutMessage("貸出"),
            "失敗後は職員証タッチ待ちへ戻るため、やり直しは職員証のタッチから（#2141）");
    }

    [Fact]
    public async Task 返却_カードのロックを取れなければ職員証のタッチからやり直すよう案内すること()
    {
        var service = CreateService(isSharedMode: false);

        var result = await WhileLockIsHeldAsync(
            () => service.ReturnAsync(StaffIdm, CardIdm, new List<LedgerDetail>()));

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(LendingService.BuildLockTimeoutMessage("返却"));
    }

    /// <summary>
    /// 例外の経路で、実行中のモードが文言へ届いていること（モードを固定値で渡す退行を検出する）。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 貸出_書き込みの例外は実行中のモードで原因を案内すること(bool isSharedMode)
    {
        _ledgerRepository.Setup(x => x.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new IOException("network error"));
        var service = CreateService(isSharedMode);

        var result = await service.LendAsync(StaffIdm, CardIdm);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(
            LendingService.GetUserFriendlyErrorMessage(new IOException("network error"), "貸出", isSharedMode));
        if (isSharedMode)
        {
            result.ErrorMessage.Should().Contain("共有フォルダ");
        }
        else
        {
            result.ErrorMessage.Should().NotContain("共有フォルダ", "ローカルモードに共有フォルダは存在しない")
                .And.NotContain("他のPC");
        }
    }

    /// <summary>
    /// 返却の例外経路も、実行中のモードを文言へ渡していること（静的検査）。
    /// </summary>
    /// <remarks>
    /// 返却を例外まで進めるには貸出中レコード・履歴の組み立てが要り、文言の検証に対して準備が重い。
    /// 呼び出し箇所が「貸出」「返却」の 2 か所で、どちらも <c>_dbContext.IsSharedMode</c> を渡すことを表明する
    /// （<c>true</c> / <c>false</c> の直書きや、呼び出しの削除は赤になる）。
    /// </remarks>
    [Fact]
    public void 貸出と返却の例外経路はどちらも実行中のモードを渡していること()
    {
        var code = TestSourceInspection.RemoveCommentsPreservingLines(File.ReadAllText(Path.Combine(
            TestPaths.GetProductionSourceRoot(), "Services", "LendingService.cs")));

        var calls = Regex.Matches(code, @"GetUserFriendlyErrorMessage\(\s*ex\s*,\s*""(貸出|返却)""\s*,\s*([^)]*)\)")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => (Operation: m.Groups[1].Value, Mode: m.Groups[2].Value.Trim()))
            .ToList();

        calls.Select(c => c.Operation).Should().BeEquivalentTo(new[] { "貸出", "返却" });
        calls.Select(c => c.Mode).Should().OnlyContain(mode => mode == "_dbContext.IsSharedMode",
            "モードを固定値で渡すと、ローカルモードで存在しない原因（他のPC・共有フォルダ）を案内する");
    }

    private async Task<LendingResult> WhileLockIsHeldAsync(Func<Task<LendingResult>> act)
    {
        var cardLock = _lockManager.GetLock(CardIdm);
        (await cardLock.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue("前提: 先にロックを取る");
        try
        {
            return await act();
        }
        finally
        {
            cardLock.Release();
            _lockManager.ReleaseLockReference(CardIdm);
        }
    }

    private LendingService CreateService(bool isSharedMode)
    {
        var dbContext = new DbContext(":memory:", logger: null, forceSharedMode: isSharedMode);
        dbContext.InitializeDatabase();
        _dbContexts.Add(dbContext);

        return new LendingService(
            dbContext,
            _cardRepository.Object,
            _staffRepository.Object,
            _ledgerRepository.Object,
            _settingsRepository.Object,
            new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>()),
            new SummaryGenerator(),
            _lockManager,
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 1 }),
            NullLogger<LendingService>.Instance);
    }
}
