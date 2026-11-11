using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Data;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2237: 返却時に記録する利用履歴の下限（導入行の日付）を、実 DB を通して検証する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は貸出日の 7 日前より古い履歴を一律に捨てており、カードに残る直近 20 件の範囲内にある
/// 未記録の利用（ピッすいを通さずに使われた利用）が記録漏れのまま確定していた。
/// 下限は導入行（「新規購入」「○月から繰越」「前年度より繰越」）の日付で、それより前の利用は導入行の残高に
/// 含まれているので取り込まない。記録済みの利用を二重に記録しないのは既存明細との照合（Issue #326）の役目。
/// </para>
/// <para>
/// 照合と導入行の判定はどちらも DB の行で決まるため、リポジトリはモックにせず実物を使う
/// （モックの既定値では「導入行が無い」「既存明細が無い」が常に成り立ち、下限も照合も検証できない）。
/// </para>
/// </remarks>
public sealed class LendingServiceReturnUsageLowerBoundTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string TestStaffIdm = "FFFF000000000001";
    private const string TestStaffName = "テスト職員";

    /// <summary>Issue #2323: 貸出者（TestStaff）とは別の、返却だけを行う職員</summary>
    private const string ReturnerStaffIdm = "FFFF000000000002";
    private const string ReturnerStaffName = "返却した職員";

    /// <summary>貸出・返却の時刻（固定）。貸出日の 7 日前は 9/13</summary>
    private static readonly DateTime Now = new(2026, 9, 20, 10, 0, 0);

    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly RecordingLogger<LendingService> _logger = new();
    private readonly FixedSystemClock _clock = new(Now);
    private readonly CardLockManager _lockManager;
    private readonly LendingService _service;

    public LendingServiceReturnUsageLowerBoundTests()
    {
        _dbContext = TestDbContextFactory.Create();

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<AppSettings>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<AppSettings>> factory, TimeSpan _) => factory());

        var cacheOptions = Options.Create(new CacheOptions());

        _ledgerRepository = new LedgerRepository(_dbContext);
        var cardRepo = new CardRepository(_dbContext, cacheServiceMock.Object, cacheOptions, NullLogger<CardRepository>.Instance);
        var staffRepo = new StaffRepository(_dbContext, cacheServiceMock.Object, cacheOptions, NullLogger<StaffRepository>.Instance);
        var settingsRepo = new SettingsRepository(_dbContext, cacheServiceMock.Object, cacheOptions);
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);

        _service = new LendingService(
            _dbContext,
            cardRepo,
            staffRepo,
            _ledgerRepository,
            settingsRepo,
            new OperationLogger(Mock.Of<IOperationLogRepository>(), Mock.Of<ICurrentOperatorContext>()),
            new SummaryGenerator(DepartmentType.MayorOffice),
            _lockManager,
            Options.Create(new AppOptions { CardLockTimeoutSeconds = 5, RetouchWindowSeconds = 30 }),
            _logger,
            _clock);

        staffRepo.InsertAsync(new Staff
        {
            StaffIdm = TestStaffIdm,
            Name = TestStaffName,
            Number = "001",
            IsDeleted = false,
        }).GetAwaiter().GetResult();

        staffRepo.InsertAsync(new Staff
        {
            StaffIdm = ReturnerStaffIdm,
            Name = ReturnerStaffName,
            Number = "002",
            IsDeleted = false,
        }).GetAwaiter().GetResult();

        cardRepo.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H-001",
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ReturnAsync_導入行があるとき_貸出日の8日以上前の未記録の利用を記録すること()
    {
        // Arrange: 9/1 に導入。9/5 にピッすいを通さずに使われ、9/20 に貸出 → 返却
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),   // 貸出日の 15 日前（修正前は捨てられた）
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 5), new DateTime(2026, 9, 20) },
            "導入日以降でカードに残っている未記録の利用は、貸出日の前後を問わず記録する");

        var old = usageLedgers.Single(l => l.Date.Date == new DateTime(2026, 9, 5));
        old.Expense.Should().Be(210);
        old.Balance.Should().Be(4790, "残高のチェーンが 9/5 の分だけずれない");
        old.StaffName.Should().Be(TestStaffName, "貸出日より前の利用も、今回の貸出の貸出者の利用として記録する（従来の 7 日以内の分と同じ）");
    }

    /// <summary>
    /// Issue #2323: 貸出者と返却者が別の職員のとき、返却で記録する利用の行の名義は貸出者であること。
    /// </summary>
    /// <remarks>
    /// 上のテストは貸出と返却を同じ職員で行うため、名義に貸出者・返却者のどちらを書いても緑になる。
    /// 名義は貸出者（カードを持ち出して使った職員）を正とする（04_機能設計書 §2.4.1）。
    /// 貸出後の利用（9/20）と貸出日より前の利用（9/5）の両方で、貸出者の IDm・氏名を具体値で表明し、
    /// 返却者の名義の行が 1 行も無いことも併せて表明する（両方に書く実装を検出するため）。
    /// </remarks>
    [Fact]
    public async Task ReturnAsync_貸出者と返却者が別の職員のとき_利用の行は貸出者の名義で記録すること()
    {
        // Arrange: 9/1 に導入。TestStaff が貸し出し、別の職員が返却する
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(ReturnerStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 5), new DateTime(2026, 9, 20) },
            "準備どおり貸出日の前後の利用が 1 行ずつ記録されている（名義の表明が空振りしないことの確認）");
        foreach (var ledger in usageLedgers)
        {
            ledger.LenderIdm.Should().Be(TestStaffIdm, $"{ledger.Date:M/d} の利用は貸出者の IDm で記録する");
            ledger.StaffName.Should().Be(TestStaffName, $"{ledger.Date:M/d} の利用は貸出者の氏名で記録する");
        }

        usageLedgers.Should().NotContain(
            l => l.LenderIdm == ReturnerStaffIdm || l.StaffName == ReturnerStaffName,
            "返却者の名義では記録しない");
    }

    [Fact]
    public async Task ReturnAsync_記録済みの利用は_範囲を広げても二重に記録しないこと()
    {
        // Arrange: 9/5 の利用は前回の返却で記録済み（既存明細のキー = 利用日 + 残高 + チャージ区分）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await InsertRecordedUsageAsync(new DateTime(2026, 9, 5), balanceAfter: 4790);
        await LendAsync();

        // Act: カードには 9/5 の利用が残ったまま
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Count(l => l.Date.Date == new DateTime(2026, 9, 5)).Should().Be(1,
            "記録済みの利用は既存明細との照合（Issue #326）で除外する");
        usageLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 20),
            "未記録の利用は記録する（照合が全件を落としていないことの対の表明）");
    }

    [Fact]
    public async Task ReturnAsync_導入行より前の利用は_貸出日の7日以内でも取り込まないこと()
    {
        // Arrange: 9/18 に導入（それより前の利用は導入行の残高 4790 円に含まれている）
        await InsertIntroductionAsync(new DateTime(2026, 9, 18), balance: 4790);
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 18), balanceAfter: 4580),  // 導入日当日（境界。登録後の利用）
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4790),  // 導入前・貸出日の 5 日前（修正前は取り込んでいた）
            Usage(new DateTime(2026, 9, 10), balanceAfter: 5000),  // 導入前・貸出日の 10 日前
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 18), new DateTime(2026, 9, 20) },
            "導入行より前の利用は導入行の残高と二重に計上になるので取り込まない。導入日当日は取り込む");
    }

    [Fact]
    public async Task ReturnAsync_導入行が無いとき_従来どおり貸出日の7日前より古い利用は取り込まないこと()
    {
        // Arrange: 導入行の無いカード（導入前のデータ・登録時に残額を読めなかった等）。
        // どこまでが計上済みか決める根拠が無いので、挙動を変えない側（貸出日の 7 日前）へ倒す
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 13), balanceAfter: 4580),  // 貸出日の 7 日前（境界・含む）
            Usage(new DateTime(2026, 9, 12), balanceAfter: 4790),  // 貸出日の 8 日前（含まない）
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 13), new DateTime(2026, 9, 20) });
    }

    [Fact]
    public async Task ReturnAsync_貸出日より前の利用を記録したとき_件数と最古の利用日をInformationで残すこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendAsync();

        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 8), balanceAfter: 4580),
            new() { UseDate = new DateTime(2026, 9, 6), Amount = 1000, Balance = 4790, IsCharge = true },  // 数えない
            Usage(new DateTime(2026, 9, 5), balanceAfter: 3790),
        });

        var entry = _logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains("貸出日より前の利用"))
            .Should().ContainSingle(_logger.FormatEntries()).Subject;
        entry.Message.Should().Contain("貸出日=2026-09-20")
            .And.Contain("該当する台帳行=2件", "9/5 と 9/8 の利用の 2 行。9/6 のチャージ行は貸出者の利用ではないので数えない")
            .And.Contain("最も古い利用日=2026-09-05")
            .And.NotContain(TestCardIdm, "カード IDm はログへ生のまま残さない（IdmMasker.Mask）");
    }

    [Fact]
    public async Task ReturnAsync_貸出後の利用だけを記録したとき_貸出日より前の利用のログを出さないこと()
    {
        // 対の表明: 上のテストだけだと「常に出す」実装でも緑になる
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendAsync();

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4790),
        });

        result.CreatedLedgers.Should().NotBeEmpty("台帳行が作られた返却で、ログを出さないことを確かめる");
        _logger.Entries.Should().NotContain(e => e.Message.Contains("貸出日より前の利用"),
            "通常の返却では出さない（正常運用でのログ肥大化を防ぐ）");
    }

    [Fact]
    public async Task ReturnAsync_明細を持たない行で補った利用は_カードに残っていても二重に記録しないこと()
    {
        // Arrange: 旧実装の 7 日の下限で漏れた 9/5 の利用を、職員が履歴の「追加」で手入力していた（明細なし）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 5), balanceAfter: 4790);
        await LendAsync();

        // Act: カードには 9/5 の利用が残ったまま
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 5), balanceAfter: 4790),
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Count(l => l.Date.Date == new DateTime(2026, 9, 5)).Should().Be(1,
            "明細を持たない行は照合（#326）で除けないので、その日以前の履歴は記録しない");
        usageLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 20),
            "明細を持たない行より後の利用は記録する（対の表明）");
    }

    [Theory]
    [InlineData(2026, 9, 20)]  // 貸出当日の朝の利用を手入力で補った
    [InlineData(2027, 3, 1)]   // 日付を誤って未来で入力した
    public async Task ReturnAsync_明細を持たない行が貸出当日や未来の日付でも_貸出後の利用は記録すること(int year, int month, int day)
    {
        // 対の表明: 明細なしの行による下限は貸出日を超えない。翌日以降へ寄せると、今回の貸出中の利用をすべて捨ててしまう
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await InsertLedgerWithoutDetailsAsync(new DateTime(year, month, day), balanceAfter: 4790);
        await LendAsync();

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 20) && l.Expense == 210,
            "貸出日の利用は、明細なしの行の日付にかかわらず記録する");
    }

    [Fact]
    public async Task ReturnAsync_日付をまたいで統合した行の明細は_照合で除いて二重に記録しないこと()
    {
        // Arrange: 9/3 の台帳行に 9/3 と 9/6 の明細が統合されている（統合は日付をまたいで行える）。
        // カードの履歴は 9/6 以降しか残っていないので、照合の起点は 9/6 になる
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        var mergedId = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 9, 3),
            Summary = "鉄道（博多～天神 往復）",
            Expense = 420,
            Balance = 4580,
            LenderIdm = TestStaffIdm,
            StaffName = TestStaffName,
        });
        await _ledgerRepository.InsertDetailsAsync(mergedId, new[]
        {
            Usage(new DateTime(2026, 9, 6), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 3), balanceAfter: 4790),
        });
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 6), balanceAfter: 4580),
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 3), new DateTime(2026, 9, 20) },
            "9/6 の利用は 9/3 の台帳行が持っているので、台帳行の日付ではなく明細の利用日で照合する");
    }

    [Fact]
    public async Task ReturnAsync_導入日が登録日より後のカードでは_導入日の前日までの利用を記録しないこと()
    {
        // Arrange: 9/20 に「9月から繰越」で登録（導入行は 10/1。9 月末までの利用は紙の出納簿に載る扱い）
        await InsertIntroductionAsync(new DateTime(2026, 10, 1), balance: 4790,
            summary: SummaryGenerator.GetMidYearCarryoverSummary(9));
        await LendAsync();

        // Act
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Should().BeEmpty("導入日（10/1）より前の利用は記録しない");
        (await GetUsageLedgersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetLatestLedgerDateWithoutDetailsAsync_導入行と貸出中と明細を持つ行を除いた最新日を返すこと()
    {
        // 明細なしの利用行（9/5・9/8）、明細ありの利用行（9/12）、導入行（9/1）、貸出中レコード（9/20）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 5), balanceAfter: 4790);
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 8), balanceAfter: 4580);
        await InsertRecordedUsageAsync(new DateTime(2026, 9, 12), balanceAfter: 4370);
        await LendAsync();

        var result = await _ledgerRepository.GetLatestLedgerDateWithoutDetailsAsync(TestCardIdm);

        result.Should().Be(new DateTime(2026, 9, 8),
            "明細を持つ行（9/12）・貸出中レコード（9/20）・導入行は除く");
    }

    [Fact]
    public async Task GetLatestLedgerDateWithoutDetailsAsync_明細を持たない利用行が無ければnullを返すこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await InsertRecordedUsageAsync(new DateTime(2026, 9, 5), balanceAfter: 4790);

        var result = await _ledgerRepository.GetLatestLedgerDateWithoutDetailsAsync(TestCardIdm);

        result.Should().BeNull("導入行は明細を持たないが対象外");
    }

    /// <summary>
    /// Issue #2372: 貸出中に履歴の「追加」で手入力した行（貸出日の翌日〜返却日）と同じ日の利用は、
    /// 下限（貸出日を超えない）では除けないので、日単位で記録済みとみなして除くこと。
    /// </summary>
    [Fact]
    public async Task ReturnAsync_貸出中に手入力した行と同じ日の利用は_二重に記録しないこと()
    {
        // Arrange: 9/15 に貸出。9/17 の利用を貸出中のまま履歴の「追加」で手入力した（明細なし）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 17), balanceAfter: 4790);

        // Act: 9/20 に返却。カードには 9/17 と 9/18 の利用が残っている
        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 18), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 4790),
        });

        // Assert
        result.Success.Should().BeTrue(_logger.FormatEntries());
        var usageLedgers = await GetUsageLedgersAsync();
        usageLedgers.Count(l => l.Date.Date == new DateTime(2026, 9, 17)).Should().Be(1,
            "9/17 は手入力の行が記録済みなので、カードに残る 9/17 の利用は取り込まない（取り込むと二重になる）");
        usageLedgers.Single(l => l.Date.Date == new DateTime(2026, 9, 17)).StaffName.Should().Be("別の職員",
            "残る 1 行は手入力の行");
        usageLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 18) && l.Expense == 210,
            "手入力の行が無い日の利用は記録する（対の表明）");
    }

    [Fact]
    public async Task ReturnAsync_貸出中に手入力した行と同じ日の利用を除いたとき_件数と日付をInformationで残すこと()
    {
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        // 9/17 の 2 区間（210 円 × 2）をまとめて手入力した行
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 17), balanceAfter: 4580, expense: 420);

        await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 18), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 4790),
        });

        var entry = _logger.Entries
            .Where(e => e.Level == LogLevel.Information && e.Message.Contains("記録済みとみなして返却時の記録から除きました"))
            .Should().ContainSingle(_logger.FormatEntries()).Subject;
        entry.Message.Should().Contain("貸出日=2026-09-15")
            .And.Contain("除いた履歴=2件", "9/17 の 2 件")
            .And.Contain("対象日=2026-09-17")
            .And.NotContain(TestCardIdm, "カード IDm はログへ生のまま残さない（IdmMasker.Mask）");
    }

    [Fact]
    public async Task ReturnAsync_明細を持たない行が無いとき_日単位の除外をせずログも出さないこと()
    {
        // 対の表明: 「常に除く」「常にログを出す」実装を検出する
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 18), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 4790),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        (await GetUsageLedgersAsync()).Select(l => l.Date.Date).Should().Equal(
            new DateTime(2026, 9, 17), new DateTime(2026, 9, 18));
        _logger.Entries.Should().NotContain(e => e.Message.Contains("記録済みとみなして返却時の記録から除きました"));
    }

    [Fact]
    public async Task ReturnAsync_貸出当日の手入力の行では_その日の利用を除かないこと()
    {
        // 対の表明: 貸出当日の手入力の行は従来どおり（当日の貸出中の利用を捨てない。Issue #2237）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 15), balanceAfter: 4790);

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4580),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 15) && l.Expense == 210,
            "貸出当日の利用は、同じ日に手入力の行があっても記録する");
    }

    [Fact]
    public async Task ReturnAsync_返却日の手入力の行と同じ日の利用も_記録しないこと()
    {
        // 範囲は貸出日の翌日から返却日まで（返却日を含む）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 20), balanceAfter: 4580);

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 20), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 16), balanceAfter: 4790),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 16) },
            "返却日の手入力の行と同じ日の利用は除き、他の日の利用は記録する");
    }

    [Fact]
    public async Task ReturnAsync_未来の日付の手入力の行があっても_貸出中の手入力の行の日だけを除くこと()
    {
        // 下限は最新の手入力の行（未来の日付）で決まり貸出日に切り詰められるが、除外は日ごとに見る
        // （最新日だけを見る実装では 9/17 を除けない）。未来の日付の行は範囲外なので、他の日の利用は捨てない
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 17), balanceAfter: 4580);
        await InsertLedgerWithoutDetailsAsync(new DateTime(2027, 3, 1), balanceAfter: 4160);

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 18), balanceAfter: 4370),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 4580),
            Usage(new DateTime(2026, 9, 15), balanceAfter: 4790),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Select(l => l.Date.Date).OrderBy(d => d).Should().Equal(
            new[] { new DateTime(2026, 9, 15), new DateTime(2026, 9, 18) });
    }

    [Fact]
    public async Task ReturnAsync_貸出中に手入力したチャージの行の日は_チャージだけを除き乗車は記録すること()
    {
        // 「明細を持たない行」の定義は下限（GetLatestLedgerDateWithoutDetailsAsync）と同じで、チャージの行も含む。
        // ただし除くのは手入力の行と同じ種類（受入）の履歴だけ。日ごと除くと 9/17 の乗車を捨て、残高のチェーンが切れる
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await InsertChargeWithoutDetailsAsync(new DateTime(2026, 9, 17), income: 1000, balanceAfter: 6000);

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 18), balanceAfter: 5580),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 5790),
            Charge(new DateTime(2026, 9, 17), amount: 1000, balanceAfter: 6000),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Should().NotContain(l => l.Income > 0,
            "手入力のチャージの行がある 9/17 のチャージは二重に記録しない");
        result.CreatedLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 17) && l.Expense == 210 && l.Balance == 5790,
            "同じ日の乗車は手入力の行と種類が違うので記録する");
        result.CreatedLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 18) && l.Expense == 210);
    }

    [Fact]
    public async Task ReturnAsync_貸出中に手入力した利用の行の日は_利用だけを除きチャージは記録すること()
    {
        // 対の向き: 払出の手入力の行は利用だけを記録済みとみなす
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 17), balanceAfter: 4790);

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 18), balanceAfter: 5580),
            Charge(new DateTime(2026, 9, 17), amount: 1000, balanceAfter: 5790),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 4790),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 17) && l.Income == 1000,
            "同じ日のチャージは手入力の行（払出）と種類が違うので記録する");
        result.CreatedLedgers.Should().NotContain(l => l.Date.Date == new DateTime(2026, 9, 17) && l.Expense > 0,
            "手入力の利用の行がある 9/17 の利用は二重に記録しない");
        result.CreatedLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 18) && l.Expense == 210);
    }

    [Fact]
    public async Task ReturnAsync_受入も払出も0円の手入力の行の日は_種類を問わずその日の履歴を除くこと()
    {
        // 金額が 0 円の行（何を記録した行か金額から判定できない）は、下限と同じく「記録済み」の側へ倒す
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 5000);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 9, 17),
            Summary = "鉄道（博多～天神）",
            Income = 0,
            Expense = 0,
            Balance = 5000,
            StaffName = "別の職員",
        });

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Usage(new DateTime(2026, 9, 18), balanceAfter: 5580),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 5790),
            Charge(new DateTime(2026, 9, 17), amount: 1000, balanceAfter: 6000),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Select(l => l.Date.Date).Should().Equal(
            new[] { new DateTime(2026, 9, 18) },
            "9/17 のチャージと利用はどちらも記録済みとみなす。9/18 の利用は記録する");
    }

    [Fact]
    public async Task ReturnAsync_残高不足の形で手入力した利用の行の日は_組のチャージも除くこと()
    {
        // 残高不足の特殊処理どおり（受入 0・払出＝運賃 210 − チャージ 10 = 200、残額 0）に手入力した行は払出だけを持つ。
        // カードには不足分のチャージ（10 円）と利用（210 円）の組が残る。組のチャージを取り込むと受入が二重になる
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 200);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 9, 17),
            Summary = "鉄道（博多～天神）",
            Income = 0,
            Expense = 200,
            Balance = 0,
            StaffName = "別の職員",
            Note = "支払額210円のうち不足額10円は現金で支払（旅費支給）",
        });

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Charge(new DateTime(2026, 9, 18), amount: 1000, balanceAfter: 1000),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 0),
            Charge(new DateTime(2026, 9, 17), amount: 10, balanceAfter: 210),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        result.CreatedLedgers.Should().NotContain(l => l.Date.Date == new DateTime(2026, 9, 17),
            "9/17 の利用と、それと組になる不足分のチャージはどちらも手入力の行が記録済み");
        result.CreatedLedgers.Should().ContainSingle(l => l.Date.Date == new DateTime(2026, 9, 18) && l.Income == 1000,
            "別の日のチャージは記録する");
    }

    [Fact]
    public async Task ReturnAsync_チャージだけを手入力した日に残高不足の組があれば_組を残して残高不足の行として記録すること()
    {
        // 残高 200 のカードで 9/17 に不足分 10 円チャージ → 210 円区間（残額 0）→ 1,000 円チャージ。
        // 貸出中に 1,000 円のチャージだけを手入力した。組のチャージ（10 円）まで除くと利用が相手を失い、
        // 払出 210・備考なしで記録される（業務ルールでは払出 200・備考あり。残高のチェーンが 10 円ずれる）
        await InsertIntroductionAsync(new DateTime(2026, 9, 1), balance: 200);
        await LendOnAsync(new DateTime(2026, 9, 15, 9, 0, 0));
        await InsertChargeWithoutDetailsAsync(new DateTime(2026, 9, 17), income: 1000, balanceAfter: 1000);

        var result = await _service.ReturnAsync(TestStaffIdm, TestCardIdm, new List<LedgerDetail>
        {
            Charge(new DateTime(2026, 9, 17), amount: 1000, balanceAfter: 1000),
            Usage(new DateTime(2026, 9, 17), balanceAfter: 0),
            Charge(new DateTime(2026, 9, 17), amount: 10, balanceAfter: 210),
        });

        result.Success.Should().BeTrue(_logger.FormatEntries());
        var created = result.CreatedLedgers.Should().ContainSingle(
            "組（10 円チャージと 210 円区間）は残高不足の 1 行として記録し、手入力した 1,000 円のチャージは二重に記録しない").Subject;
        created.Date.Date.Should().Be(new DateTime(2026, 9, 17));
        created.Income.Should().Be(0);
        created.Expense.Should().Be(200, "払出＝運賃 210 − 不足分のチャージ 10");
        created.Balance.Should().Be(0);
        created.Note.Should().Contain("現金", "残高不足の特殊処理の備考を付ける");
    }

    [Fact]
    public void ExcludeUsageOnDaysRecordedWithoutDetails_受入の行の日は_残高不足の組のチャージを利用と一緒に残すこと()
    {
        var shortfallCharge = Charge(new DateTime(2026, 9, 17), amount: 10, balanceAfter: 210);
        var usage = Usage(new DateTime(2026, 9, 17), balanceAfter: 0);
        var normalCharge = Charge(new DateTime(2026, 9, 17), amount: 1000, balanceAfter: 1000);

        var (toRecord, excluded) = LendingService.ExcludeUsageOnDaysRecordedWithoutDetails(
            new List<LedgerDetail> { normalCharge, usage, shortfallCharge },
            new[] { new DayRecordedWithoutDetails(new DateTime(2026, 9, 17), hasIncome: true, hasExpense: false) });

        excluded.Should().Equal(normalCharge);
        toRecord.Should().Equal(usage, shortfallCharge);
    }

    [Fact]
    public void ExcludeUsageOnDaysRecordedWithoutDetails_払出の行の日は_残高不足の組のチャージだけを除き通常のチャージは残すこと()
    {
        // 対の表明: 同じ日に組のチャージ（10 円）と通常のチャージ（1,000 円）がある
        var shortfallCharge = Charge(new DateTime(2026, 9, 17), amount: 10, balanceAfter: 210);
        var usage = Usage(new DateTime(2026, 9, 17), balanceAfter: 0);
        var normalCharge = Charge(new DateTime(2026, 9, 17), amount: 1000, balanceAfter: 1000);

        var (toRecord, excluded) = LendingService.ExcludeUsageOnDaysRecordedWithoutDetails(
            new List<LedgerDetail> { normalCharge, usage, shortfallCharge },
            new[] { new DayRecordedWithoutDetails(new DateTime(2026, 9, 17), hasIncome: false, hasExpense: true) });

        excluded.Should().Equal(usage, shortfallCharge);
        toRecord.Should().Equal(normalCharge);
    }

    [Fact]
    public async Task GetDaysRecordedWithoutDetailsAsync_範囲内の明細を持たない行の日と種類を_導入行と貸出中と明細を持つ行を除いて返すこと()
    {
        // 導入行（9/16）、明細なしの利用行（9/14・9/17・9/19・9/22）、明細なしのチャージの行（9/15・9/17）、
        // 明細なしの金額 0 の行（9/18）、明細ありの利用行（9/18）、貸出中レコード（9/20）
        await InsertIntroductionAsync(new DateTime(2026, 9, 16), balance: 5000);
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 14), balanceAfter: 5000);
        await InsertChargeWithoutDetailsAsync(new DateTime(2026, 9, 15), income: 1000, balanceAfter: 6000);
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 17), balanceAfter: 4790);
        await InsertChargeWithoutDetailsAsync(new DateTime(2026, 9, 17, 18, 0, 0), income: 1000, balanceAfter: 5790);
        await InsertRecordedUsageAsync(new DateTime(2026, 9, 18), balanceAfter: 5580);
        await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = new DateTime(2026, 9, 18, 12, 0, 0),
            Summary = "鉄道（博多～天神）",
            Balance = 5580,
            StaffName = "別の職員",
        });
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 19), balanceAfter: 5370);
        await InsertLedgerWithoutDetailsAsync(new DateTime(2026, 9, 22), balanceAfter: 5160);
        await LendAsync();

        var result = await _ledgerRepository.GetDaysRecordedWithoutDetailsAsync(
            TestCardIdm, new DateTime(2026, 9, 15), new DateTime(2026, 9, 20, 10, 0, 0));

        result.Select(d => (d.Date, d.HasIncome, d.HasExpense)).Should().Equal(
            new[]
            {
                (new DateTime(2026, 9, 15), true, false),
                (new DateTime(2026, 9, 17), true, true),
                (new DateTime(2026, 9, 18), false, false),
                (new DateTime(2026, 9, 19), false, true),
            },
            "範囲外（9/14・9/22）・導入行（9/16）・明細を持つ行（9/18 の利用）・貸出中レコード（9/20）は除き、同じ日の行の種類は合わせる");
    }

    [Fact]
    public void ExcludeUsageOnDaysRecordedWithoutDetails_日付の無い履歴は除かないこと()
    {
        var noDate = new LedgerDetail { UseDate = null, Amount = 210, Balance = 4790 };
        var onDay = Usage(new DateTime(2026, 9, 17), balanceAfter: 4580);
        var otherDay = Usage(new DateTime(2026, 9, 18), balanceAfter: 4370);

        var (toRecord, excluded) = LendingService.ExcludeUsageOnDaysRecordedWithoutDetails(
            new List<LedgerDetail> { otherDay, onDay, noDate },
            new[] { new DayRecordedWithoutDetails(new DateTime(2026, 9, 17), hasIncome: false, hasExpense: true) });

        toRecord.Should().Equal(otherDay, noDate);
        excluded.Should().Equal(onDay);
    }

    [Fact]
    public void ExcludeUsageOnDaysRecordedWithoutDetails_ポイント還元は受入の手入力の行の日に除くこと()
    {
        var pointRedemption = new LedgerDetail { UseDate = new DateTime(2026, 9, 17), Amount = 100, Balance = 4890, IsPointRedemption = true };
        var usage = Usage(new DateTime(2026, 9, 17), balanceAfter: 4790);

        var (toRecord, excluded) = LendingService.ExcludeUsageOnDaysRecordedWithoutDetails(
            new List<LedgerDetail> { pointRedemption, usage },
            new[] { new DayRecordedWithoutDetails(new DateTime(2026, 9, 17), hasIncome: true, hasExpense: false) });

        excluded.Should().Equal(pointRedemption);
        toRecord.Should().Equal(usage);
    }

    private async Task LendOnAsync(DateTime lentAt)
    {
        _clock.Now = lentAt;
        try
        {
            await LendAsync();
        }
        finally
        {
            _clock.Now = Now;
        }
    }

    private async Task LendAsync()
    {
        var lendResult = await _service.LendAsync(TestStaffIdm, TestCardIdm);
        lendResult.Success.Should().BeTrue($"貸出が失敗（{lendResult.ErrorMessage}）");
    }

    private async Task InsertIntroductionAsync(DateTime date, int balance, string summary = "新規購入")
    {
        await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = summary,
            Income = Ledger.InitialRecordCarriesIncome(summary) ? balance : 0,
            Expense = 0,
            Balance = balance,
        });
    }

    private async Task InsertRecordedUsageAsync(DateTime date, int balanceAfter)
    {
        var id = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "鉄道（博多～天神）",
            Expense = 210,
            Balance = balanceAfter,
            LenderIdm = TestStaffIdm,
            StaffName = TestStaffName,
        });
        await _ledgerRepository.InsertDetailsAsync(id, new[] { Usage(date, balanceAfter) });
    }

    /// <summary>
    /// 履歴の「追加」で手入力した行と同じ形（明細を持たない利用行）。
    /// 利用者は貸出者（返却で記録する行の名義）と別の職員にする — 同じだと同日統合（#837）が新しい明細をこの行へ取り込み、
    /// 二重に記録しても行数が増えないため、下限の効果を行数で観測できない
    /// </summary>
    private async Task InsertLedgerWithoutDetailsAsync(DateTime date, int balanceAfter, int expense = 210)
    {
        await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "鉄道（博多～天神）",
            Expense = expense,
            Balance = balanceAfter,
            StaffName = "別の職員",
        });
    }

    /// <summary>履歴の「追加」で手入力したチャージの行（明細なし。受入のみ）</summary>
    private async Task InsertChargeWithoutDetailsAsync(DateTime date, int income, int balanceAfter)
    {
        await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = "役務費によりチャージ",
            Income = income,
            Balance = balanceAfter,
        });
    }

    /// <summary>導入行・貸出中レコードを除いた、利用の台帳行（日付昇順）</summary>
    private async Task<List<Ledger>> GetUsageLedgersAsync()
    {
        var ledgers = await _ledgerRepository.GetByDateRangeAsync(
            TestCardIdm, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        return ledgers
            .Where(l => !l.IsLentRecord && !l.IsInitialRecord)
            .OrderBy(l => l.Date)
            .ToList();
    }

    private static LedgerDetail Charge(DateTime useDate, int amount, int balanceAfter) => new()
    {
        UseDate = useDate,
        Amount = amount,
        Balance = balanceAfter,
        IsCharge = true,
    };

    private static LedgerDetail Usage(DateTime useDate, int balanceAfter) => new()
    {
        UseDate = useDate,
        EntryStation = "博多",
        ExitStation = "天神",
        Amount = 210,
        Balance = balanceAfter,
    };
}
