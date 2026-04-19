using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Timing;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace ICCardManager.Tests.Services;

/// <summary>
/// OperationLoggerの単体テスト
/// Issue #1265: 操作者情報は ICurrentOperatorContext から一元的に解決される。
/// 旧シグネチャに渡された operatorIdm は無視される（監査ログなりすまし防止）。
/// </summary>
public class OperationLoggerTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly OperationLogRepository _operationLogRepository;
    private readonly Mock<ISystemClock> _clockMock;
    private readonly CurrentOperatorContext _operatorContext;
    private readonly OperationLogger _logger;
    private DateTime _now;

    public OperationLoggerTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _operationLogRepository = new OperationLogRepository(_dbContext);

        _now = new DateTime(2026, 4, 17, 10, 0, 0);
        _clockMock = new Mock<ISystemClock>();
        _clockMock.Setup(c => c.Now).Returns(() => _now);
        _operatorContext = new CurrentOperatorContext(_clockMock.Object);

        _logger = new OperationLogger(_operationLogRepository, _operatorContext);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GuiOperator 定数テスト

    /// <summary>GUI操作用識別子の値が正しいことを確認</summary>
    [Fact]
    public void GuiOperator_HasCorrectValues()
    {
        OperationLogger.GuiOperator.Idm.Should().Be("0000000000000000");
        OperationLogger.GuiOperator.Idm.Should().HaveLength(16);
        OperationLogger.GuiOperator.Name.Should().Be("GUI操作");
    }

    #endregion

    #region 新API: context なし → GuiOperator フォールバック

    [Fact]
    public async Task LogLedgerUpdateAsync_WithoutContext_UsesGuiIdentifier()
    {
        // Arrange: context 未設定
        var beforeLedger = CreateTestLedger(summary: "変更前");
        var afterLedger = CreateTestLedger(summary: "変更後");

        // Act: 新 API（operator 引数なし）
        await _logger.LogLedgerUpdateAsync(beforeLedger, afterLedger);

        // Assert
        var logs = await _operationLogRepository.GetByOperatorAsync(OperationLogger.GuiOperator.Idm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        log.TargetTable.Should().Be(OperationLogger.Tables.Ledger);
        log.Action.Should().Be(OperationLogger.Actions.Update);
    }

    [Fact]
    public async Task LogStaffInsertAsync_WithoutContext_UsesGuiIdentifier()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffInsertAsync(staff);

        var logs = await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        log.Action.Should().Be(OperationLogger.Actions.Insert);
    }

    #endregion

    #region 新API: context あり → context 値を使用

    [Fact]
    public async Task LogLedgerDeleteAsync_WithContext_RecordsContextOperator()
    {
        // Arrange: 認証済み operator を context に設定
        const string authIdm = "AAAA000000000001";
        const string authName = "認証済み職員";
        _operatorContext.BeginSession(authIdm, authName);

        var ledger = CreateTestLedger(id: 99, summary: "削除対象");

        // Act
        await _logger.LogLedgerDeleteAsync(ledger);

        // Assert
        var logs = await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "99");
        var log = logs.Single();
        log.OperatorIdm.Should().Be(authIdm);
        log.OperatorName.Should().Be(authName);
        log.Action.Should().Be(OperationLogger.Actions.Delete);
    }

    [Fact]
    public async Task LogStaffInsertAsync_WithContext_RecordsContextOperator()
    {
        _operatorContext.BeginSession("BBBB000000000002", "山田 花子");

        var staff = CreateTestStaff();
        await _logger.LogStaffInsertAsync(staff);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.OperatorIdm.Should().Be("BBBB000000000002");
        log.OperatorName.Should().Be("山田 花子");
    }

    #endregion

    #region Issue #1265: 監査ログなりすまし防止

    /// <summary>
    /// 旧 API に操作者 IDm を渡しても、context が設定されている場合は
    /// context の値が優先される（呼び出し側は他人の IDm でなりすますことができない）。
    /// </summary>
    [Fact]
    public async Task ObsoleteApi_WithContext_IgnoresPassedOperatorIdm_AntiSpoofing()
    {
        // Arrange: 実際の認証操作者は AAAA...
        const string authenticatedIdm = "AAAA000000000001";
        const string authenticatedName = "認証済み職員";
        _operatorContext.BeginSession(authenticatedIdm, authenticatedName);

        // 攻撃者が悪意を持って他の職員の IDm を渡す
        const string spoofedIdm = "FFFF000000000002";
        var ledger = CreateTestLedger(id: 50, summary: "なりすましターゲット");

        // Act: [Obsolete] な旧 API に偽の IDm を渡す
#pragma warning disable CS0618 // Obsolete 警告を抑制（このテストこそが非推奨 API の振る舞いを検証）
        await _logger.LogLedgerDeleteAsync(spoofedIdm, ledger);
#pragma warning restore CS0618

        // Assert: 記録は context の認証済み操作者で行われ、偽IDm は記録されない
        var spoofedLogs = await _operationLogRepository.GetByOperatorAsync(spoofedIdm);
        spoofedLogs.Should().BeEmpty("なりすまし IDm でのログは一切記録されてはならない");

        var authenticatedLogs = await _operationLogRepository.GetByOperatorAsync(authenticatedIdm);
        authenticatedLogs.Should().HaveCount(1);
        authenticatedLogs.Single().OperatorName.Should().Be(authenticatedName);
    }

    /// <summary>
    /// context が未設定のときに旧 API に操作者 IDm を渡しても、
    /// それは無視され GUI 操作としてフォールバックする（引数経由でのなりすましを防ぐ）。
    /// </summary>
    [Fact]
    public async Task ObsoleteApi_WithoutContext_IgnoresPassedOperatorIdm_FallsBackToGui()
    {
        // Arrange: context 未設定
        const string spoofedIdm = "FFFF000000000003";
        var ledger = CreateTestLedger(id: 51, summary: "context未設定の偽称ターゲット");

        // Act: 悪意ある呼び出し側が他の職員の IDm を渡す
#pragma warning disable CS0618
        await _logger.LogLedgerDeleteAsync(spoofedIdm, ledger);
#pragma warning restore CS0618

        // Assert: 偽 IDm のログは一切作られず、GUI 操作として記録される
        var spoofedLogs = await _operationLogRepository.GetByOperatorAsync(spoofedIdm);
        spoofedLogs.Should().BeEmpty();

        var guiLogs = await _operationLogRepository.GetByOperatorAsync(OperationLogger.GuiOperator.Idm);
        guiLogs.Should().HaveCount(1);
        guiLogs.Single().OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
    }

    /// <summary>
    /// セッション失効後は、旧 API 経由の操作者 IDm も context も使われず GUI 操作扱い。
    /// </summary>
    [Fact]
    public async Task ObsoleteApi_AfterContextExpiration_UsesGuiIdentifier()
    {
        // Arrange: 短い有効期間の context
        var shortLived = new CurrentOperatorContext(_clockMock.Object, TimeSpan.FromSeconds(10));
        shortLived.BeginSession("CCCC000000000003", "期限切れ予定職員");
        var logger = new OperationLogger(_operationLogRepository, shortLived);

        // 11 秒経過 → セッション失効
        _now = _now.AddSeconds(11);

        var ledger = CreateTestLedger(id: 52, summary: "失効後ターゲット");

        // Act
#pragma warning disable CS0618
        await logger.LogLedgerDeleteAsync("FFFF000000000004", ledger);
#pragma warning restore CS0618

        // Assert: GUI 操作としてフォールバック
        var logs = await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "52");
        var log = logs.Single();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
    }

    #endregion

    #region 後方互換: 旧 API は新 API と同じ結果を返す

    [Fact]
    public async Task ObsoleteLogStaffInsertAsync_DelegatesToNewApi()
    {
        var staff = CreateTestStaff();

#pragma warning disable CS0618
        await _logger.LogStaffInsertAsync(null, staff);
#pragma warning restore CS0618

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ObsoleteLogLedgerMergeAsync_DelegatesToNewApi()
    {
        var src1 = CreateTestLedger(id: 1, summary: "元1");
        var src2 = CreateTestLedger(id: 2, summary: "元2");
        var merged = CreateTestLedger(id: 3, summary: "統合後");

#pragma warning disable CS0618
        await _logger.LogLedgerMergeAsync(null, new List<Ledger> { src1, src2 }, merged);
#pragma warning restore CS0618

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "3")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Merge);
        log.BeforeData.Should().Contain("元1").And.Contain("元2");
        log.AfterData.Should().Contain("統合後");
    }

    #endregion

    #region 各テーブルのログ記録 (新 API)

    [Fact]
    public async Task LogStaffUpdateAsync_RecordsBeforeAndAfter()
    {
        var before = CreateTestStaff(name: "旧氏名");
        var after = CreateTestStaff(name: "新氏名");

        await _logger.LogStaffUpdateAsync(before, after);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, after.StaffIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.BeforeData.Should().Contain("旧氏名");
        log.AfterData.Should().Contain("新氏名");
    }

    [Fact]
    public async Task LogStaffDeleteAsync_RecordsBeforeOnly()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffDeleteAsync(staff);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.BeforeData.Should().NotBeNullOrEmpty();
        log.AfterData.Should().BeNull();
    }

    [Fact]
    public async Task LogStaffRestoreAsync_RecordsAfterOnly()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffRestoreAsync(staff);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LogCardInsertAsync_RecordsCorrectly()
    {
        var card = CreateTestCard();

        await _logger.LogCardInsertAsync(card);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.IcCard, card.CardIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LogCardUpdateAsync_RecordsBeforeAndAfter()
    {
        var before = CreateTestCard(cardNumber: "OLD-001");
        var after = CreateTestCard(cardNumber: "NEW-001");

        await _logger.LogCardUpdateAsync(before, after);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.IcCard, after.CardIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.BeforeData.Should().Contain("OLD-001");
        log.AfterData.Should().Contain("NEW-001");
    }

    [Fact]
    public async Task LogCardDeleteAsync_RecordsBeforeOnly()
    {
        var card = CreateTestCard();

        await _logger.LogCardDeleteAsync(card);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.IcCard, card.CardIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.BeforeData.Should().NotBeNullOrEmpty();
        log.AfterData.Should().BeNull();
    }

    [Fact]
    public async Task LogCardRestoreAsync_RecordsAfterOnly()
    {
        var card = CreateTestCard();

        await _logger.LogCardRestoreAsync(card);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.IcCard, card.CardIdm)).Single();
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LogLedgerInsertAsync_RecordsCorrectly()
    {
        var ledger = CreateTestLedger(id: 42, summary: "新規行");

        await _logger.LogLedgerInsertAsync(ledger);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "42")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().Contain("新規行");
    }

    [Fact]
    public async Task LogLedgerDeleteAsync_WithoutContext_RecordsBeforeOnlyAsGui()
    {
        var ledger = CreateTestLedger(id: 77, summary: "GUI削除対象");

        await _logger.LogLedgerDeleteAsync(ledger);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "77")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        log.BeforeData.Should().Contain("GUI削除対象");
        log.AfterData.Should().BeNull();
    }

    [Fact]
    public async Task LogLedgerMergeAsync_RecordsSourcesAndMerged()
    {
        var src1 = CreateTestLedger(id: 1, summary: "元1");
        var src2 = CreateTestLedger(id: 2, summary: "元2");
        var merged = CreateTestLedger(id: 3, summary: "統合後");

        await _logger.LogLedgerMergeAsync(new List<Ledger> { src1, src2 }, merged);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "3")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Merge);
        log.BeforeData.Should().Contain("元1").And.Contain("元2");
        log.AfterData.Should().Contain("統合後");
    }

    [Fact]
    public async Task LogLedgerSplitAsync_RecordsOriginalAndSplits()
    {
        var original = CreateTestLedger(id: 10, summary: "元");
        var split1 = CreateTestLedger(id: 11, summary: "分割1");
        var split2 = CreateTestLedger(id: 12, summary: "分割2");

        await _logger.LogLedgerSplitAsync(original, new List<Ledger> { split1, split2 });

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Ledger, "10")).Single();
        log.Action.Should().Be(OperationLogger.Actions.Split);
        log.BeforeData.Should().Contain("元");
        log.AfterData.Should().Contain("分割1").And.Contain("分割2");
    }

    #endregion

    #region JSONシリアライズ

    /// <summary>日本語・特殊文字を含むデータが読みやすく記録される</summary>
    [Fact]
    public async Task LogStaffInsertAsync_PreservesJapaneseAndSpecialChars()
    {
        var staff = CreateTestStaff(name: "山田 \"太郎\" & 花子");

        await _logger.LogStaffInsertAsync(staff);

        var log = (await _operationLogRepository.GetByTargetAsync(OperationLogger.Tables.Staff, staff.StaffIdm)).Single();
        log.AfterData.Should().Contain("山田");
        log.AfterData.Should().Contain("花子");
    }

    #endregion

    #region Helper Methods

    private static Staff CreateTestStaff(
        string idm = "1234000000000001",
        string name = "テスト職員")
    {
        return new Staff
        {
            StaffIdm = idm,
            Name = name,
        };
    }

    private static IcCard CreateTestCard(
        string cardIdm = "07FE112233445566",
        string cardNumber = "TEST-001")
    {
        return new IcCard
        {
            CardIdm = cardIdm,
            CardNumber = cardNumber,
        };
    }

    private static Ledger CreateTestLedger(
        int id = 1,
        string cardIdm = "07FE112233445566",
        string summary = "テスト摘要",
        int income = 0,
        int expense = 200,
        int balance = 4800)
    {
        return new Ledger
        {
            Id = id,
            CardIdm = cardIdm,
            Date = DateTime.Now.Date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            StaffName = "テスト職員",
            Note = "テストデータ"
        };
    }

    #endregion
}
