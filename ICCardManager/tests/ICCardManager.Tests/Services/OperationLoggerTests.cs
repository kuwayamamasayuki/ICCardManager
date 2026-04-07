using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
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
/// </summary>
public class OperationLoggerTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly OperationLogRepository _operationLogRepository;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly OperationLogger _logger;

    public OperationLoggerTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _operationLogRepository = new OperationLogRepository(_dbContext);
        _staffRepositoryMock = new Mock<IStaffRepository>();

        _logger = new OperationLogger(_operationLogRepository, _staffRepositoryMock.Object);
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region LogLedgerUpdateAsync GUI操作テスト

    /// <summary>
    /// operatorIdmがnullの場合、GUI操作用識別子が使用されること
    /// </summary>
    [Fact]
    public async Task LogLedgerUpdateAsync_WithNullOperatorIdm_UsesGuiIdentifier()
    {
        // Arrange
        var beforeLedger = CreateTestLedger(summary: "変更前");
        var afterLedger = CreateTestLedger(summary: "変更後");

        // Act
        await _logger.LogLedgerUpdateAsync(null, beforeLedger, afterLedger);

        // Assert
        var logs = await _operationLogRepository.GetByOperatorAsync(OperationLogger.GuiOperator.Idm);
        logs.Should().HaveCount(1);

        var log = logs.First();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        log.TargetTable.Should().Be(OperationLogger.Tables.Ledger);
        log.Action.Should().Be(OperationLogger.Actions.Update);
    }

    /// <summary>
    /// operatorIdmが空文字列の場合、GUI操作用識別子が使用されること
    /// </summary>
    [Fact]
    public async Task LogLedgerUpdateAsync_WithEmptyOperatorIdm_UsesGuiIdentifier()
    {
        // Arrange
        var beforeLedger = CreateTestLedger(summary: "変更前");
        var afterLedger = CreateTestLedger(summary: "変更後");

        // Act
        await _logger.LogLedgerUpdateAsync(string.Empty, beforeLedger, afterLedger);

        // Assert
        var logs = await _operationLogRepository.GetByOperatorAsync(OperationLogger.GuiOperator.Idm);
        logs.Should().HaveCount(1);

        var log = logs.First();
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
    }

    /// <summary>
    /// 有効なoperatorIdmが渡された場合、そのIDmが使用されること
    /// </summary>
    [Fact]
    public async Task LogLedgerUpdateAsync_WithValidOperatorIdm_UsesProvidedIdm()
    {
        // Arrange
        const string operatorIdm = "FFFF000000000001";
        const string operatorName = "テスト職員";
        var staff = new Staff { StaffIdm = operatorIdm, Name = operatorName };

        _staffRepositoryMock
            .Setup(x => x.GetByIdmAsync(operatorIdm, true))
            .ReturnsAsync(staff);

        var beforeLedger = CreateTestLedger(summary: "変更前");
        var afterLedger = CreateTestLedger(summary: "変更後");

        // Act
        await _logger.LogLedgerUpdateAsync(operatorIdm, beforeLedger, afterLedger);

        // Assert
        var logs = await _operationLogRepository.GetByOperatorAsync(operatorIdm);
        logs.Should().HaveCount(1);

        var log = logs.First();
        log.OperatorIdm.Should().Be(operatorIdm);
        log.OperatorName.Should().Be(operatorName);
    }

    /// <summary>
    /// GUI操作用識別子の値が正しいことを確認
    /// </summary>
    [Fact]
    public void GuiOperator_HasCorrectValues()
    {
        // Assert
        OperationLogger.GuiOperator.Idm.Should().Be("0000000000000000");
        OperationLogger.GuiOperator.Idm.Should().HaveLength(16);
        OperationLogger.GuiOperator.Name.Should().Be("GUI操作");
    }

    /// <summary>
    /// GUI操作のログに変更前・変更後のデータが正しく記録されること
    /// </summary>
    [Fact]
    public async Task LogLedgerUpdateAsync_WithGuiOperation_RecordsBeforeAndAfterData()
    {
        // Arrange
        var beforeLedger = CreateTestLedger(id: 1, summary: "バス（★）");
        var afterLedger = CreateTestLedger(id: 1, summary: "バス（天神～博多）");

        // Act
        await _logger.LogLedgerUpdateAsync(null, beforeLedger, afterLedger);

        // Assert
        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Ledger,
            afterLedger.Id.ToString());
        logs.Should().HaveCount(1);

        var log = logs.First();
        log.BeforeData.Should().Contain("バス（★）");
        log.AfterData.Should().Contain("バス（天神～博多）");
    }

    #endregion

    #region Staff ログ

    /// <summary>
    /// LogStaffInsertAsync: GUI操作で staff テーブル・INSERT・After のみが記録される
    /// </summary>
    [Fact]
    public async Task LogStaffInsertAsync_GuiOperation_RecordsCorrectly()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffInsertAsync(null, staff);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Staff, staff.StaffIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.OperatorIdm.Should().Be(OperationLogger.GuiOperator.Idm);
        log.OperatorName.Should().Be(OperationLogger.GuiOperator.Name);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
        log.AfterData.Should().Contain(staff.Name);
    }

    /// <summary>
    /// LogStaffUpdateAsync: Before/After 両方が記録される
    /// </summary>
    [Fact]
    public async Task LogStaffUpdateAsync_RecordsBeforeAndAfter()
    {
        var before = CreateTestStaff(name: "旧氏名");
        var after = CreateTestStaff(name: "新氏名");

        await _logger.LogStaffUpdateAsync(null, before, after);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Staff, after.StaffIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.BeforeData.Should().Contain("旧氏名");
        log.AfterData.Should().Contain("新氏名");
    }

    /// <summary>
    /// LogStaffDeleteAsync: Before のみ記録、After は null
    /// </summary>
    [Fact]
    public async Task LogStaffDeleteAsync_RecordsBeforeOnly()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffDeleteAsync(null, staff);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Staff, staff.StaffIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.BeforeData.Should().NotBeNullOrEmpty();
        log.AfterData.Should().BeNull();
    }

    /// <summary>
    /// LogStaffRestoreAsync: After のみ記録、Before は null
    /// </summary>
    [Fact]
    public async Task LogStaffRestoreAsync_RecordsAfterOnly()
    {
        var staff = CreateTestStaff();

        await _logger.LogStaffRestoreAsync(null, staff);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Staff, staff.StaffIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// 有効な操作者IDmが渡された場合、StaffRepositoryから氏名を取得して記録する
    /// </summary>
    [Fact]
    public async Task LogStaffInsertAsync_WithValidOperator_LooksUpName()
    {
        const string operatorIdm = "0102030405060708";
        _staffRepositoryMock
            .Setup(x => x.GetByIdmAsync(operatorIdm, true))
            .ReturnsAsync(new Staff { StaffIdm = operatorIdm, Name = "操作者A" });

        var target = CreateTestStaff(idm: "AAAA000000000001", name: "対象者");

        await _logger.LogStaffInsertAsync(operatorIdm, target);

        var logs = await _operationLogRepository.GetByOperatorAsync(operatorIdm);
        logs.Should().HaveCount(1);
        logs.First().OperatorName.Should().Be("操作者A");
    }

    /// <summary>
    /// 操作者がStaffRepositoryに存在しない場合は「不明」と記録される
    /// </summary>
    [Fact]
    public async Task LogStaffInsertAsync_WithUnknownOperator_RecordsAsUnknown()
    {
        const string operatorIdm = "DEADBEEF00000001";
        _staffRepositoryMock
            .Setup(x => x.GetByIdmAsync(operatorIdm, true))
            .ReturnsAsync((Staff)null);

        var target = CreateTestStaff();

        await _logger.LogStaffInsertAsync(operatorIdm, target);

        var logs = await _operationLogRepository.GetByOperatorAsync(operatorIdm);
        logs.Should().HaveCount(1);
        logs.First().OperatorName.Should().Be("不明");
    }

    #endregion

    #region IcCard ログ

    /// <summary>
    /// LogCardInsertAsync: ic_card テーブル・INSERT・After のみ
    /// </summary>
    [Fact]
    public async Task LogCardInsertAsync_RecordsCorrectly()
    {
        var card = CreateTestCard();

        await _logger.LogCardInsertAsync(null, card);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.IcCard, card.CardIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.TargetTable.Should().Be(OperationLogger.Tables.IcCard);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// LogCardUpdateAsync: Before/After 両方
    /// </summary>
    [Fact]
    public async Task LogCardUpdateAsync_RecordsBeforeAndAfter()
    {
        var before = CreateTestCard(cardNumber: "OLD-001");
        var after = CreateTestCard(cardNumber: "NEW-001");

        await _logger.LogCardUpdateAsync(null, before, after);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.IcCard, after.CardIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Update);
        log.BeforeData.Should().Contain("OLD-001");
        log.AfterData.Should().Contain("NEW-001");
    }

    /// <summary>
    /// LogCardDeleteAsync: Before のみ
    /// </summary>
    [Fact]
    public async Task LogCardDeleteAsync_RecordsBeforeOnly()
    {
        var card = CreateTestCard();

        await _logger.LogCardDeleteAsync(null, card);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.IcCard, card.CardIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.BeforeData.Should().NotBeNullOrEmpty();
        log.AfterData.Should().BeNull();
    }

    /// <summary>
    /// LogCardRestoreAsync: After のみ
    /// </summary>
    [Fact]
    public async Task LogCardRestoreAsync_RecordsAfterOnly()
    {
        var card = CreateTestCard();

        await _logger.LogCardRestoreAsync(null, card);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.IcCard, card.CardIdm);
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Ledger ログ（Insert/Delete/Merge/Split）

    /// <summary>
    /// LogLedgerInsertAsync: ledger テーブル・INSERT・After のみ
    /// </summary>
    [Fact]
    public async Task LogLedgerInsertAsync_RecordsCorrectly()
    {
        var ledger = CreateTestLedger(id: 42, summary: "新規行");

        await _logger.LogLedgerInsertAsync(null, ledger);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Ledger, "42");
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.BeforeData.Should().BeNull();
        log.AfterData.Should().Contain("新規行");
    }

    /// <summary>
    /// LogLedgerDeleteAsync: 有効な operatorIdm を渡すと正しく記録される。
    /// （他のメソッドと異なり null は許容されないシグネチャ）
    /// </summary>
    [Fact]
    public async Task LogLedgerDeleteAsync_WithValidOperator_RecordsBeforeOnly()
    {
        const string operatorIdm = "BEEF000000000001";
        _staffRepositoryMock
            .Setup(x => x.GetByIdmAsync(operatorIdm, true))
            .ReturnsAsync(new Staff { StaffIdm = operatorIdm, Name = "削除者" });

        var ledger = CreateTestLedger(id: 99, summary: "削除対象");

        await _logger.LogLedgerDeleteAsync(operatorIdm, ledger);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Ledger, "99");
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        log.OperatorName.Should().Be("削除者");
        log.BeforeData.Should().Contain("削除対象");
        log.AfterData.Should().BeNull();
    }

    /// <summary>
    /// LogLedgerMergeAsync: BeforeData に元レコード配列、AfterData に統合後レコードが入る
    /// </summary>
    [Fact]
    public async Task LogLedgerMergeAsync_RecordsSourcesAndMerged()
    {
        var src1 = CreateTestLedger(id: 1, summary: "元1");
        var src2 = CreateTestLedger(id: 2, summary: "元2");
        var merged = CreateTestLedger(id: 3, summary: "統合後");

        await _logger.LogLedgerMergeAsync(null, new List<Ledger> { src1, src2 }, merged);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Ledger, "3");
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Merge);
        log.BeforeData.Should().Contain("元1").And.Contain("元2");
        log.AfterData.Should().Contain("統合後");
    }

    /// <summary>
    /// LogLedgerSplitAsync: BeforeData に元レコード、AfterData に分割後配列が入る。TargetId は元レコードの ID
    /// </summary>
    [Fact]
    public async Task LogLedgerSplitAsync_RecordsOriginalAndSplits()
    {
        var original = CreateTestLedger(id: 10, summary: "元");
        var split1 = CreateTestLedger(id: 11, summary: "分割1");
        var split2 = CreateTestLedger(id: 12, summary: "分割2");

        await _logger.LogLedgerSplitAsync(null, original, new List<Ledger> { split1, split2 });

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Ledger, "10");
        logs.Should().HaveCount(1);
        var log = logs.First();
        log.Action.Should().Be(OperationLogger.Actions.Split);
        log.BeforeData.Should().Contain("元");
        log.AfterData.Should().Contain("分割1").And.Contain("分割2");
    }

    #endregion

    #region JSONシリアライズ

    /// <summary>
    /// 日本語・特殊文字（&, ", '）を含むデータがエスケープを最小限にして読みやすく記録される
    /// （JavaScriptEncoder.UnsafeRelaxedJsonEscaping の振る舞い）
    /// </summary>
    [Fact]
    public async Task LogStaffInsertAsync_PreservesJapaneseAndSpecialChars()
    {
        var staff = CreateTestStaff(name: "山田 \"太郎\" & 花子");

        await _logger.LogStaffInsertAsync(null, staff);

        var logs = await _operationLogRepository.GetByTargetAsync(
            OperationLogger.Tables.Staff, staff.StaffIdm);
        var log = logs.First();
        // 日本語はエスケープされず生のまま記録される
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
