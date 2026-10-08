using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2255: 繰越情報の復旧ダイアログの ViewModel。
/// </summary>
/// <remarks>
/// <para>
/// 書き込みは実 DB（インメモリ SQLite）で行い、DB に実際に入った値と操作ログを表明する。
/// 認証（職員証タッチ）だけをモックにする。
/// </para>
/// <para>
/// 「保存する側」（認証のうえ書き戻す・書き戻したら消失の警告が消える）と「保存しない側」
/// （入力の誤り・認証の取り消し・競合では何も書かない）を対で置く。
/// </para>
/// </remarks>
public sealed class CarryoverRecoveryViewModelTests : IDisposable
{
    private const string TestCardIdm = "07FE112233445566";
    private const string OperatorIdm = "FFFF000000000009";
    private const string OperatorName = "庶務 担当";

    private readonly DbContext _dbContext;
    private readonly CardRepository _cardRepository;
    private readonly OperationLogRepository _operationLogRepository;
    private readonly Mock<IStaffAuthService> _staffAuthService = new();

    /// <summary>今は 2026 年 10 月（＝2026 年度）。年度の上限の判定に使う</summary>
    private readonly FixedSystemClock _clock = new(new DateTime(2026, 10, 8, 10, 0, 0));

    public CarryoverRecoveryViewModelTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        var cacheService = new Mock<ICacheService>();
        cacheService.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan _) => factory());
        _cardRepository = new CardRepository(
            _dbContext, cacheService.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);
        _operationLogRepository = new OperationLogRepository(_dbContext);

        _staffAuthService.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = OperatorIdm, StaffName = OperatorName });
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 初期化

    [Fact]
    public async Task InitializeAsync_失われた項目は失われた値を_失われていない項目は現在の値を入力欄に入れること()
    {
        // Arrange: 開始ページ番号と繰越累計受入だけが失われたカード。払出は現在 0 円、年度は現在 2025
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, 2025));
        var target = Item(lostPage: 7, lostIncome: 45000);
        var vm = CreateViewModel();

        // Act
        await vm.InitializeAsync(target);

        // Assert
        vm.CardDisplayName.Should().Be("はやかけん 001");
        vm.StartingPageNumberText.Should().Be("7");
        vm.CarryoverIncomeTotalText.Should().Be("45000", "桁区切りなしで入れる（そのまま編集しやすい）");
        vm.CarryoverExpenseTotalText.Should().Be("0");
        vm.CarryoverFiscalYearText.Should().Be("2025");

        vm.LostStartingPageNumberText.Should().Be("7");
        vm.LostCarryoverIncomeTotalText.Should().Be(DisplayFormatters.FormatAmountWithUnit(45000));
        vm.LostCarryoverExpenseTotalText.Should().Be(CarryoverDataLossViewModel.NotLostText);
        vm.LostCarryoverFiscalYearText.Should().Be(CarryoverDataLossViewModel.NotLostText);

        vm.CurrentStartingPageNumberText.Should().Be("1");
        vm.CurrentCarryoverIncomeTotalText.Should().Be(DisplayFormatters.FormatAmountWithUnit(0));
        vm.CurrentCarryoverFiscalYearText.Should().Be("2025年度");

        vm.CanSave.Should().BeTrue();
        vm.StatusMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_対象年度が現在も失われてもいなければ空欄にすること()
    {
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var vm = CreateViewModel();

        await vm.InitializeAsync(Item(lostPage: 7));

        vm.CarryoverFiscalYearText.Should().BeEmpty();
        vm.CurrentCarryoverFiscalYearText.Should().Be("（なし）");
    }

    [Fact]
    public async Task InitializeAsync_一覧を作った後に他のパソコンが既に書き戻していたら_上書きさせないこと()
    {
        // 一覧（検出結果）は古いまま、別の PC が紙の出納簿と突き合わせて 8 ページ目で復旧した。
        // ここで保存させると、操作ログの古い値 7 で 8 を上書きする（開いた後の競合判定では、
        // 開いた時点の値 8 が基準になるため検出できない）
        await SeedCardAsync(new CarryoverInfo(8, 0, 0, null));
        var vm = CreateViewModel();

        await vm.InitializeAsync(Item(lostPage: 7));

        vm.CanSave.Should().BeFalse();
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.StatusMessage.Should().Be(CarryoverRecoveryViewModel.BuildAlreadyRecoveredMessage("はやかけん 001"));
        await vm.SaveAsync();
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(8);
        _staffAuthService.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_既に書き戻されていたら_一覧の作り直しが古いキャッシュを返さないよう捨てること()
    {
        // 一覧の検知はキャッシュ付きのカード一覧を使う（共有モードの TTL は最大 15 秒）。捨てないと、
        // 「一覧で確認して」と案内した直後の作り直しに同じ行がまた出る
        var repository = new Mock<ICardRepository>();
        repository.Setup(r => r.GetByIdmAsync(TestCardIdm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            StartingPageNumber = 8,
        });
        var vm = CreateViewModel(repository.Object);

        await vm.InitializeAsync(Item(lostPage: 7));

        vm.CanSave.Should().BeFalse();
        repository.Verify(r => r.InvalidateCache(), Times.Once);
    }

    [Fact]
    public async Task InitializeAsync_書き戻されていなければ_キャッシュを捨てないこと()
    {
        // 上の対。開くたびに捨てると、共有モードで一覧の読み込みが毎回 DB へ行く
        var repository = new Mock<ICardRepository>();
        repository.Setup(r => r.GetByIdmAsync(TestCardIdm, false)).ReturnsAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
        });
        var vm = CreateViewModel(repository.Object);

        await vm.InitializeAsync(Item(lostPage: 7));

        vm.CanSave.Should().BeTrue();
        repository.Verify(r => r.InvalidateCache(), Times.Never);
    }

    [Theory]
    [InlineData(7, null, null, null, 1, 0, 0, null, false)]   // 失われたページはまだ既定値
    [InlineData(7, null, null, null, 1, 5000, 0, 2024, false)] // 失われていない項目が値を持つのはよい
    [InlineData(7, null, null, null, 8, 0, 0, null, true)]
    [InlineData(null, 45000, null, null, 1, 100, 0, null, true)]
    [InlineData(null, null, 37500, null, 1, 0, 100, null, true)]
    [InlineData(null, null, null, 2025, 1, 0, 0, 2024, true)]
    public void IsAlreadyRecovered_失われた項目のいずれかがもう既定値でなければ真であること(
        int? lostPage, int? lostIncome, int? lostExpense, int? lostYear,
        int page, int income, int expense, int? year, bool expected)
    {
        CarryoverRecoveryViewModel.IsAlreadyRecovered(
                Item(lostPage, lostIncome, lostExpense, lostYear), new CarryoverInfo(page, income, expense, year))
            .Should().Be(expected);
    }

    [Fact]
    public async Task InitializeAsync_カードが削除されていたら_案内して保存できなくすること()
    {
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        (await _cardRepository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);
        var vm = CreateViewModel();

        await vm.InitializeAsync(Item(lostPage: 7));

        vm.CanSave.Should().BeFalse();
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Be(CarryoverRecoveryViewModel.BuildCardNotFoundMessage("はやかけん 001"));
    }

    [Fact]
    public async Task InitializeAsync_読み込みに失敗したら_例外を投げず案内して保存できなくすること()
    {
        // ダイアログを開く経路で例外を投げると、画面を開けないまま一覧へ戻り何が起きたか分からない
        var failingRepository = new Mock<ICardRepository>();
        failingRepository.Setup(r => r.GetByIdmAsync(TestCardIdm, false))
            .ThrowsAsync(new InvalidOperationException("database is locked"));
        var vm = CreateViewModel(failingRepository.Object);

        await vm.InitializeAsync(Item(lostPage: 7));

        vm.CanSave.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Be(
            ExceptionMessageFormatter.ToUserMessage(new InvalidOperationException("database is locked"), "カード情報の読み込み"));
        vm.StatusMessage.Should().NotContain("database is locked", "生の例外メッセージを出さない（#1614）");
    }

    #endregion

    #region 保存する側

    [Fact]
    public async Task SaveAsync_職員証の認証のうえ_入力した値で書き戻し監査ログを残すこと()
    {
        // Arrange
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var vm = CreateViewModel();
        await vm.InitializeAsync(Item(lostPage: 7, lostIncome: 45000, lostExpense: 37500, lostYear: 2025));
        vm.StartingPageNumberText = "8";   // 紙の出納簿と突き合わせて直した

        // Act
        await vm.SaveAsync();

        // Assert
        vm.IsSaved.Should().BeTrue(vm.StatusMessage);
        _staffAuthService.Verify(s => s.RequestAuthenticationAsync("繰越情報の復旧"), Times.Once);
        CarryoverInfo.From((await _cardRepository.GetByIdmAsync(TestCardIdm))!)
            .Should().Be(new CarryoverInfo(8, 45000, 37500, 2025), "初期値ではなく、職員が直した値で書く");

        var log = (await _operationLogRepository.SearchAllAsync(new OperationLogSearchCriteria
        {
            TargetTable = OperationLogger.Tables.IcCard,
        })).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.OperatorIdm.Should().Be(OperatorIdm);
        CarryoverInfo.From(JsonSerializer.Deserialize<IcCard>(log.AfterData!)!)
            .Should().Be(new CarryoverInfo(8, 45000, 37500, 2025));
    }

    [Fact]
    public async Task SaveAsync_書き戻したら_繰越情報の消失として検知されなくなること()
    {
        // 「保存したのに警告が消えない」が起きないことを、実際の検知（CarryoverDataLossDetector）へ通して表明する。
        // Arrange: Issue #1726 以前の版が書いた消失の操作ログ（非既定値 → 既定値）と、消失した状態のカード
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        await SeedLossLogAsync(lost: new CarryoverInfo(7, 45000, 37500, 2025));
        var detector = new CarryoverDataLossDetector(_operationLogRepository, _cardRepository);
        var detected = (await detector.DetectAsync()).Should().ContainSingle().Subject;

        var vm = CreateViewModel();
        await vm.InitializeAsync(detected);

        // Act: 失われた値をそのまま保存する
        await vm.SaveAsync();

        // Assert
        vm.IsSaved.Should().BeTrue(vm.StatusMessage);
        (await detector.DetectAsync()).Should().BeEmpty("書き戻したカードは警告の対象から外れる");
    }

    #endregion

    #region 保存しない側

    [Fact]
    public async Task SaveAsync_入力に誤りがあれば_認証を求めず何も書かずに誤りの欄を示すこと()
    {
        // Arrange
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var vm = CreateViewModel();
        await vm.InitializeAsync(Item(lostPage: 7, lostIncome: 45000));
        vm.CarryoverIncomeTotalText = "0";   // 失われた項目に既定値
        var focused = new List<CarryoverInputField>();
        vm.FocusRequested += (_, field) => focused.Add(field);

        // Act
        await vm.SaveAsync();

        // Assert
        vm.IsSaved.Should().BeFalse();
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().StartWith("繰越累計受入が0円です。");
        focused.Should().Equal(CarryoverInputField.CarryoverIncomeTotal);
        _staffAuthService.Verify(s => s.RequestAuthenticationAsync(It.IsAny<string>()), Times.Never,
            "職員証をタッチさせてから入力の誤りを伝えない");
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_対象年度の上限は注入した時計の来年度であること()
    {
        // 2026 年 10 月は 2026 年度。来年度の 2027 までは受け付け、2028 年度は受け付けない（実時計を読む実装なら境界が食い違う）
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var vm = CreateViewModel();
        await vm.InitializeAsync(Item(lostPage: 7, lostIncome: 45000, lostYear: 2025));
        vm.CarryoverFiscalYearText = "2028";

        await vm.SaveAsync();

        vm.IsSaved.Should().BeFalse();
        vm.StatusMessage.Should().Contain("来年度（2027年度）");
    }

    [Fact]
    public async Task SaveAsync_認証が完了しなければ_何も書かずに入力を残すこと()
    {
        // Arrange
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        _staffAuthService.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>())).ReturnsAsync((StaffAuthResult?)null);
        var vm = CreateViewModel();
        await vm.InitializeAsync(Item(lostPage: 7));

        // Act
        await vm.SaveAsync();

        // Assert
        vm.IsSaved.Should().BeFalse();
        vm.StatusMessage.Should().Be(CarryoverRecoveryViewModel.AuthenticationCancelledMessage);
        vm.StartingPageNumberText.Should().Be("7", "入力を消さない（もう一度「保存」を押せばよい）");
        vm.CanSave.Should().BeTrue();
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_開いた後に他のパソコンが先に復旧していたら_上書きせず保存できなくすること()
    {
        // Arrange: 開いた時点は既定値。認証を待つ間に別の PC が 5 ページ目で復旧した
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var vm = CreateViewModel();
        await vm.InitializeAsync(Item(lostPage: 7));
        _staffAuthService.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .Returns(async () =>
            {
                await RecoverDirectlyAsync(new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(5, 0, 0, null));
                return new StaffAuthResult { Idm = OperatorIdm, StaffName = OperatorName };
            });

        // Act
        await vm.SaveAsync();

        // Assert
        vm.IsSaved.Should().BeFalse();
        vm.CanSave.Should().BeFalse("開いたときの値はもう古い。同じ画面からやり直させない");
        vm.StatusMessage.Should().Be(CarryoverRecoveryViewModel.BuildConflictMessage("はやかけん 001"));
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(5, "見ていない値を上書きしない");
    }

    [Fact]
    public async Task SaveAsync_読み直しの後で書き込む前に先を越されたら_上書きせず保存できなくすること()
    {
        // 読み直した値は開いたときと一致したが、UPDATE の WHERE 句（読んだ値との比較）で負けた経路。
        // 読み取りだけを古い値を返す代役にし、書き込みは実 DB の行（他の PC が 5 ページ目で復旧済み）へ届ける
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var staleCard = (await _cardRepository.GetByIdmAsync(TestCardIdm))!;
        await RecoverDirectlyAsync(new CarryoverInfo(1, 0, 0, null), new CarryoverInfo(5, 0, 0, null));
        var staleReads = new Mock<ICardRepository>();
        staleReads.Setup(r => r.GetByIdmAsync(TestCardIdm, false)).ReturnsAsync(staleCard);
        var vm = CreateViewModel(staleReads.Object);
        await vm.InitializeAsync(Item(lostPage: 7));

        await vm.SaveAsync();

        vm.IsSaved.Should().BeFalse();
        vm.CanSave.Should().BeFalse();
        vm.StatusMessage.Should().Be(CarryoverRecoveryViewModel.BuildConflictMessage("はやかけん 001"));
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(5, "見ていない値を上書きしない");
        staleReads.Verify(r => r.InvalidateCache(), Times.Once, "一覧で確認させる前に古いキャッシュを捨てる");
    }

    [Fact]
    public async Task InitializeAsync_読み込みの間は処理中を表示すること()
    {
        // 共有モードでは読み込みに秒単位かかり得る。空の入力欄のまま何も出ないと壊れて見える
        var pending = new TaskCompletionSource<IcCard?>();
        var repository = new Mock<ICardRepository>();
        repository.Setup(r => r.GetByIdmAsync(TestCardIdm, false)).Returns(pending.Task);
        var vm = CreateViewModel(repository.Object);

        var loading = vm.InitializeAsync(Item(lostPage: 7));
        var busyWhileLoading = vm.IsBusy;
        pending.SetResult(new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "001" });
        await loading;

        busyWhileLoading.Should().BeTrue();
        vm.IsBusy.Should().BeFalse("読み込みが終わったら処理中を解く");
        vm.CanSave.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_開いた後にカードが削除されていたら_書かずに案内すること()
    {
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var vm = CreateViewModel();
        await vm.InitializeAsync(Item(lostPage: 7));
        (await _cardRepository.DeleteAsync(TestCardIdm)).Should().Be(CardOperationResult.Success);

        await vm.SaveAsync();

        vm.IsSaved.Should().BeFalse();
        vm.CanSave.Should().BeFalse();
        vm.StatusMessage.Should().Be(CarryoverRecoveryViewModel.BuildConflictMessage("はやかけん 001"));
        (await _cardRepository.GetByIdmAsync(TestCardIdm, includeDeleted: true))!.StartingPageNumber.Should().Be(1);
    }

    [Fact]
    public async Task SaveAsync_書き込みに失敗したら_生の例外メッセージを出さず案内すること()
    {
        // Arrange: 操作ログの書き込みだけを失敗させる（本処理も巻き戻る）
        await SeedCardAsync(new CarryoverInfo(1, 0, 0, null));
        var failingLogs = new Mock<IOperationLogRepository>();
        failingLogs.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("disk I/O error"));
        var vm = CreateViewModel(operationLogRepository: failingLogs.Object);
        await vm.InitializeAsync(Item(lostPage: 7));

        // Act
        await vm.SaveAsync();

        // Assert
        vm.IsSaved.Should().BeFalse();
        vm.IsBusy.Should().BeFalse("処理中の表示を解く");
        vm.IsStatusError.Should().BeTrue();
        vm.StatusMessage.Should().Be(
            ExceptionMessageFormatter.ToUserMessage(new InvalidOperationException("disk I/O error"), "繰越情報の復旧"));
        (await _cardRepository.GetByIdmAsync(TestCardIdm))!.StartingPageNumber.Should().Be(1);
    }

    #endregion

    #region ヘルパー

    private CarryoverRecoveryViewModel CreateViewModel(
        ICardRepository? cardRepository = null, IOperationLogRepository? operationLogRepository = null)
    {
        var operatorContext = new Mock<ICurrentOperatorContext>();
        operatorContext.SetupGet(c => c.HasSession).Returns(true);
        operatorContext.SetupGet(c => c.CurrentIdm).Returns(OperatorIdm);
        operatorContext.SetupGet(c => c.CurrentName).Returns(OperatorName);

        var service = new CardManagementService(
            _dbContext,
            _cardRepository,
            new OperationLogger(operationLogRepository ?? _operationLogRepository, operatorContext.Object),
            NullLogger<CardManagementService>.Instance);

        return new CarryoverRecoveryViewModel(
            cardRepository ?? _cardRepository,
            service,
            _staffAuthService.Object,
            _clock,
            NullLogger<CarryoverRecoveryViewModel>.Instance);
    }

    private static CarryoverDataLossItem Item(
        int? lostPage = null, int? lostIncome = null, int? lostExpense = null, int? lostYear = null) => new()
        {
            CardIdm = TestCardIdm,
            CardDisplayName = "はやかけん 001",
            LostStartingPageNumber = lostPage,
            LostCarryoverIncomeTotal = lostIncome,
            LostCarryoverExpenseTotal = lostExpense,
            LostCarryoverFiscalYear = lostYear,
            LostAt = new DateTime(2025, 5, 20, 14, 30, 0),
            OperatorName = "総務 花子",
        };

    private async Task SeedCardAsync(CarryoverInfo carryover)
    {
        (await _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            StartingPageNumber = carryover.StartingPageNumber,
            CarryoverIncomeTotal = carryover.CarryoverIncomeTotal,
            CarryoverExpenseTotal = carryover.CarryoverExpenseTotal,
            CarryoverFiscalYear = carryover.CarryoverFiscalYear,
        })).Should().BeTrue();
    }

    /// <summary>
    /// Issue #1726 以前の版の編集が書いた、繰越情報の消失の操作ログ（変更前＝失われた値、変更後＝既定値）
    /// </summary>
    private async Task SeedLossLogAsync(CarryoverInfo lost)
    {
        var before = new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "001",
            StartingPageNumber = lost.StartingPageNumber,
            CarryoverIncomeTotal = lost.CarryoverIncomeTotal,
            CarryoverExpenseTotal = lost.CarryoverExpenseTotal,
            CarryoverFiscalYear = lost.CarryoverFiscalYear,
        };
        var after = new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "001", Note = "誤字を直した" };
        await _operationLogRepository.InsertAsync(new OperationLog
        {
            Timestamp = new DateTime(2025, 5, 20, 14, 30, 0),
            OperatorIdm = "FFFF000000000001",
            OperatorName = "総務 花子",
            TargetTable = OperationLogger.Tables.IcCard,
            TargetId = TestCardIdm,
            Action = OperationLogger.Actions.Update,
            BeforeData = JsonSerializer.Serialize(before),
            AfterData = JsonSerializer.Serialize(after),
        });
    }

    /// <summary>他の PC の復旧を模して、画面を通さずに繰越情報を書き換える</summary>
    private async Task RecoverDirectlyAsync(CarryoverInfo expected, CarryoverInfo replacement)
    {
        using var scope = await _dbContext.BeginTransactionAsync();
        (await _cardRepository.UpdateCarryoverInfoAsync(TestCardIdm, expected, replacement, scope.Transaction))
            .Should().BeTrue();
        scope.Commit();
    }

    #endregion
}
