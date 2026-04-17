using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// LendingService.IsRetouchWithinTimeout の境界値・状態遷移テスト (Issue #1255)。
///
/// 以下の観点を検証する:
/// 1. タイムアウト境界値（29秒以内 / ちょうど30秒 / 31秒経過）
/// 2. LastProcessedCardIdm / LastOperationType の状態遷移
/// 3. ClearHistory 後の初期状態
/// 4. 複数カードの交互操作時の混同防止
/// 5. AppOptions.RetouchWindowSeconds 設定の反映
///
/// 時刻操作は DateTime.Now ベースのため、LastProcessedTime をリフレクションで
/// 書き換えて経過時間をシミュレートする。
/// </summary>
public class LendingServiceRetouchTimeoutTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly SummaryGenerator _summaryGenerator;
    private readonly CardLockManager _lockManager;

    private const string CardAIdm = "0102030405060708";
    private const string CardBIdm = "A1A2A3A4A5A6A7A8";
    private const string StaffIdm = "1112131415161718";

    public LendingServiceRetouchTimeoutTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(s => s.GetAppSettings()).Returns(new AppSettings());
        _settingsRepositoryMock.Setup(s => s.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());
        _summaryGenerator = new SummaryGenerator();
        _lockManager = new CardLockManager(NullLogger<CardLockManager>.Instance);

        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(StaffIdm, false))
            .ReturnsAsync(new Staff { StaffIdm = StaffIdm, Name = "テスト職員", IsDeleted = false });

        _ledgerRepositoryMock.Setup(x => x.DeleteAllLentRecordsAsync(It.IsAny<string>()))
            .ReturnsAsync(1);
    }

    public void Dispose()
    {
        _lockManager.Dispose();
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private LendingService CreateService(int retouchWindowSeconds = 30)
    {
        return new LendingService(
            _dbContext,
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _settingsRepositoryMock.Object,
            _summaryGenerator,
            _lockManager,
            Options.Create(new AppOptions { RetouchWindowSeconds = retouchWindowSeconds }),
            NullLogger<LendingService>.Instance);
    }

    /// <summary>
    /// 未貸出のカードとしてLendAsync用のモック設定を行う。
    /// </summary>
    private void SetupLendMocks(string cardIdm)
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, false))
            .ReturnsAsync(new IcCard
            {
                CardIdm = cardIdm,
                CardType = "はやかけん",
                CardNumber = "C001",
                IsLent = false,
                IsDeleted = false
            });
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                cardIdm, It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
    }

    /// <summary>
    /// 貸出中のカードとしてReturnAsync用のモック設定を行う。
    /// </summary>
    private void SetupReturnMocks(string cardIdm)
    {
        _cardRepositoryMock.Setup(r => r.GetByIdmAsync(cardIdm, false))
            .ReturnsAsync(new IcCard
            {
                CardIdm = cardIdm,
                CardType = "はやかけん",
                CardNumber = "C001",
                IsLent = true,
                IsDeleted = false
            });
        _cardRepositoryMock.Setup(r => r.UpdateLentStatusAsync(
                cardIdm, It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string?>()))
            .ReturnsAsync(true);
        _ledgerRepositoryMock.Setup(r => r.GetLentRecordAsync(cardIdm))
            .ReturnsAsync(new Ledger
            {
                Id = 1,
                CardIdm = cardIdm,
                LenderIdm = StaffIdm,
                Date = DateTime.Today,
                IsLentRecord = true,
                LentAt = DateTime.Now.AddMinutes(-10)
            });
        _ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(1);
        _ledgerRepositoryMock.Setup(r => r.GetLatestBeforeDateAsync(cardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new Ledger { Balance = 5000 });
        _ledgerRepositoryMock.Setup(r => r.GetExistingDetailKeysAsync(cardIdm, It.IsAny<DateTime>()))
            .ReturnsAsync(new HashSet<(DateTime?, int?, bool)>());
    }

    /// <summary>
    /// LastProcessedTime を指定した時刻に書き換える（経過時間シミュレーション用）。
    /// auto-property の private setter を反射で呼ぶ。
    /// </summary>
    private static void OverrideLastProcessedTime(LendingService service, DateTime overrideTime)
    {
        var property = typeof(LendingService).GetProperty(
            nameof(LendingService.LastProcessedTime),
            BindingFlags.Instance | BindingFlags.Public);
        property.Should().NotBeNull("LastProcessedTime プロパティが存在する必要がある");
        property!.SetValue(service, (DateTime?)overrideTime);
    }

    #region 境界値テスト（29秒以内 / ちょうど30秒 / 31秒経過）

    /// <summary>
    /// 貸出から29秒経過時点での再タッチは30秒ルールの対象（true）であること。
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_29SecondsAfterLend_ReturnsTrue()
    {
        // Arrange: 貸出処理を実行
        var service = CreateService(retouchWindowSeconds: 30);
        SetupLendMocks(CardAIdm);
        await service.LendAsync(StaffIdm, CardAIdm);

        // Act: LastProcessedTime を29秒前に偽装
        OverrideLastProcessedTime(service, DateTime.Now.AddSeconds(-29));

        // Assert: 30秒ルール対象
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeTrue(
            "29秒経過は30秒ルールの対象内");
        service.LastOperationType.Should().Be(LendingOperationType.Lend,
            "直前の操作は貸出のため、次のタッチでは逆操作（返却）が実行される");
    }

    /// <summary>
    /// 返却から31秒経過時点での再タッチは30秒ルールの対象外（false）であること。
    /// 新規操作として扱うべきケース。
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_31SecondsAfterReturn_ReturnsFalse()
    {
        // Arrange: 返却処理を実行
        var service = CreateService(retouchWindowSeconds: 30);
        SetupReturnMocks(CardAIdm);
        await service.ReturnAsync(StaffIdm, CardAIdm, new List<LedgerDetail>());

        // Act: LastProcessedTime を31秒前に偽装
        OverrideLastProcessedTime(service, DateTime.Now.AddSeconds(-31));

        // Assert: タイムアウト超過のため新規操作として扱う
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeFalse(
            "31秒経過は30秒ルールの対象外");
    }

    /// <summary>
    /// 貸出からちょうど30秒経過の時点では、30秒ルールの対象（true）であること（境界値）。
    /// 実装は elapsed.TotalSeconds &lt;= _retouchTimeoutSeconds のため境界は含む。
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_Exactly30Seconds_ReturnsTrue()
    {
        // Arrange
        var service = CreateService(retouchWindowSeconds: 30);
        SetupLendMocks(CardAIdm);
        await service.LendAsync(StaffIdm, CardAIdm);

        // Act: 30秒ちょうど前に偽装
        OverrideLastProcessedTime(service, DateTime.Now.AddSeconds(-30));

        // Assert: 境界値は inclusively true
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeTrue(
            "ちょうど30秒は <= 判定のためtrue");
    }

    #endregion

    #region 状態遷移テスト (Lend → Lend → Return)

    /// <summary>
    /// Lend → (29秒以内) Lend → Return のシナリオ:
    /// 2回目のタッチでは直前がLend状態のため「逆操作=返却」が判定できる。
    /// </summary>
    [Fact]
    public async Task StateTransition_LendThenReTouchWithin29Seconds_DetectsReverseAsReturn()
    {
        // Arrange: 1回目 Lend
        var service = CreateService(retouchWindowSeconds: 30);
        SetupLendMocks(CardAIdm);
        await service.LendAsync(StaffIdm, CardAIdm);

        // Assert 1回目後の状態
        service.LastProcessedCardIdm.Should().Be(CardAIdm);
        service.LastOperationType.Should().Be(LendingOperationType.Lend);
        service.LastProcessedTime.Should().NotBeNull();

        // Act: 29秒前にLastProcessedTimeを偽装 → 2回目タッチを擬似
        OverrideLastProcessedTime(service, DateTime.Now.AddSeconds(-29));

        // Assert: IsRetouchWithinTimeout=true + LastOperationType=Lend
        // MainViewModel はこれを見て「逆操作=Return」を選択する
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeTrue();
        service.LastOperationType.Should().Be(LendingOperationType.Lend,
            "2回目のタッチ判定時点では直前のLend状態が保持されている");
    }

    /// <summary>
    /// Return → (31秒経過) Lend のシナリオ:
    /// 31秒経過後の再タッチは新規操作として扱われ、LastOperationType は
    /// 返却のままだが、IsRetouchWithinTimeout は false を返す。
    /// </summary>
    [Fact]
    public async Task StateTransition_ReturnThenReTouchAfter31Seconds_TreatedAsNewOperation()
    {
        // Arrange: 返却
        var service = CreateService(retouchWindowSeconds: 30);
        SetupReturnMocks(CardAIdm);
        await service.ReturnAsync(StaffIdm, CardAIdm, new List<LedgerDetail>());
        service.LastOperationType.Should().Be(LendingOperationType.Return);

        // Act: 31秒経過を偽装
        OverrideLastProcessedTime(service, DateTime.Now.AddSeconds(-31));

        // Assert: タイムアウト超過
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeFalse(
            "31秒経過後は30秒ルール非適用。新規の貸出として処理される");
        // LastOperationType と LastProcessedCardIdm は残っているが、時間ウィンドウが切れている
        service.LastProcessedCardIdm.Should().Be(CardAIdm);
    }

    #endregion

    #region ClearHistory 後の初期状態

    /// <summary>
    /// ClearHistory 実行後は LastProcessedCardIdm / LastProcessedTime /
    /// LastOperationType が全て null になり、IsRetouchWithinTimeout も false を返すこと。
    /// </summary>
    [Fact]
    public async Task ClearHistory_AfterReturn_InitializesAllStateToNull()
    {
        // Arrange: 返却を実行 → 履歴が蓄積された状態
        var service = CreateService(retouchWindowSeconds: 30);
        SetupReturnMocks(CardAIdm);
        await service.ReturnAsync(StaffIdm, CardAIdm, new List<LedgerDetail>());

        service.LastProcessedCardIdm.Should().Be(CardAIdm);
        service.LastProcessedTime.Should().NotBeNull();
        service.LastOperationType.Should().Be(LendingOperationType.Return);

        // Act
        service.ClearHistory();

        // Assert: 全て初期状態
        service.LastProcessedCardIdm.Should().BeNull();
        service.LastProcessedTime.Should().BeNull();
        service.LastOperationType.Should().BeNull();
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeFalse(
            "履歴クリア後は直前のカードでも30秒ルール非適用");
    }

    #endregion

    #region 複数カードの交互操作での混同防止

    /// <summary>
    /// カードA貸出 → カードB貸出 の順で操作した場合、
    /// LastProcessedCardIdm はカードBに更新され、カードAで IsRetouchWithinTimeout を
    /// 呼んでも false になる（カード混同の防止）。
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_AfterDifferentCardLent_OriginalCardReturnsFalse()
    {
        // Arrange
        var service = CreateService(retouchWindowSeconds: 30);
        SetupLendMocks(CardAIdm);
        SetupLendMocks(CardBIdm);

        // Act: カードA貸出 → カードB貸出
        await service.LendAsync(StaffIdm, CardAIdm);
        await service.LendAsync(StaffIdm, CardBIdm);

        // Assert: LastProcessedCardIdm はカードB
        service.LastProcessedCardIdm.Should().Be(CardBIdm);

        // カードAは30秒ルール対象外
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeFalse(
            "直近の処理対象はカードBのため、カードAは30秒ルール非適用");

        // カードBは30秒ルール対象
        service.IsRetouchWithinTimeout(CardBIdm).Should().BeTrue();
    }

    /// <summary>
    /// カードA貸出 → カードB貸出 → カードA再タッチのシーケンスでは、
    /// カードAは30秒ルール非適用として新規操作扱いになること。
    /// </summary>
    [Fact]
    public async Task IsRetouchWithinTimeout_AlternatingCards_PreventsMixup()
    {
        // Arrange
        var service = CreateService(retouchWindowSeconds: 30);
        SetupLendMocks(CardAIdm);
        SetupLendMocks(CardBIdm);

        // Act: A → B → A
        await service.LendAsync(StaffIdm, CardAIdm);
        service.LastProcessedCardIdm.Should().Be(CardAIdm);
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeTrue();

        await service.LendAsync(StaffIdm, CardBIdm);
        service.LastProcessedCardIdm.Should().Be(CardBIdm);

        // Assert: カードAでタッチしても30秒ルール非適用
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeFalse(
            "カードBでの操作が間に挟まったため、カードAの直近性は失われる");
    }

    #endregion

    #region AppOptions.RetouchWindowSeconds の反映

    /// <summary>
    /// AppOptions.RetouchWindowSeconds がサービスに正しく反映され、
    /// 設定値 - 1 秒は true、設定値 + 1 秒は false になること。
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public async Task IsRetouchWithinTimeout_RespectsAppOptionsRetouchWindowSeconds(int configuredSeconds)
    {
        // Arrange
        var service = CreateService(retouchWindowSeconds: configuredSeconds);
        SetupLendMocks(CardAIdm);
        await service.LendAsync(StaffIdm, CardAIdm);

        // Act/Assert: 設定値 - 1秒 → true
        OverrideLastProcessedTime(service, DateTime.Now.AddSeconds(-(configuredSeconds - 1)));
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeTrue(
            $"RetouchWindowSeconds={configuredSeconds} の場合、{configuredSeconds - 1}秒経過は対象内");

        // Act/Assert: 設定値 + 1秒 → false
        OverrideLastProcessedTime(service, DateTime.Now.AddSeconds(-(configuredSeconds + 1)));
        service.IsRetouchWithinTimeout(CardAIdm).Should().BeFalse(
            $"RetouchWindowSeconds={configuredSeconds} の場合、{configuredSeconds + 1}秒経過は対象外");
    }

    #endregion
}
