using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

using System;
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

    #region Helper Methods

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
