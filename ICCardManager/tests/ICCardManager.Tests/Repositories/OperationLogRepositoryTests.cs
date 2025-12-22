using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Repositories;

/// <summary>
/// OperationLogRepositoryの単体テスト
/// </summary>
public class OperationLogRepositoryTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly OperationLogRepository _repository;

    public OperationLogRepositoryTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _repository = new OperationLogRepository(_dbContext);

        // マイグレーション時に自動挿入されるログをクリア
        ClearOperationLogs();
    }

    private void ClearOperationLogs()
    {
        var connection = _dbContext.GetConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM operation_log";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
        GC.SuppressFinalize(this);
    }

    #region InsertAsync テスト

    [Fact]
    public async Task InsertAsync_ValidLog_ReturnsId()
    {
        // Arrange
        var log = CreateTestLog();

        // Act
        var id = await _repository.InsertAsync(log);

        // Assert
        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task InsertAsync_MultipleLogs_ReturnsIncrementingIds()
    {
        // Arrange & Act
        var id1 = await _repository.InsertAsync(CreateTestLog());
        var id2 = await _repository.InsertAsync(CreateTestLog());
        var id3 = await _repository.InsertAsync(CreateTestLog());

        // Assert
        id2.Should().Be(id1 + 1);
        id3.Should().Be(id2 + 1);
    }

    #endregion

    #region GetByDateRangeAsync テスト

    [Fact]
    public async Task GetByDateRangeAsync_WithLogsInRange_ReturnsLogs()
    {
        // Arrange
        var log1 = CreateTestLog(timestamp: new DateTime(2024, 1, 15, 10, 0, 0));
        var log2 = CreateTestLog(timestamp: new DateTime(2024, 1, 20, 14, 30, 0));
        var log3 = CreateTestLog(timestamp: new DateTime(2024, 2, 5, 9, 0, 0));

        await _repository.InsertAsync(log1);
        await _repository.InsertAsync(log2);
        await _repository.InsertAsync(log3);

        // Act
        var result = await _repository.GetByDateRangeAsync(
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        // Assert
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetByDateRangeAsync_EmptyRange_ReturnsEmpty()
    {
        // Arrange
        var log = CreateTestLog(timestamp: new DateTime(2024, 1, 15));
        await _repository.InsertAsync(log);

        // Act
        var result = await _repository.GetByDateRangeAsync(
            new DateTime(2024, 2, 1),
            new DateTime(2024, 2, 28));

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetByOperatorAsync テスト

    [Fact]
    public async Task GetByOperatorAsync_WithMatchingOperator_ReturnsLogs()
    {
        // Arrange
        var log1 = CreateTestLog(operatorIdm: "OPERATOR001");
        var log2 = CreateTestLog(operatorIdm: "OPERATOR001");
        var log3 = CreateTestLog(operatorIdm: "OPERATOR002");

        await _repository.InsertAsync(log1);
        await _repository.InsertAsync(log2);
        await _repository.InsertAsync(log3);

        // Act
        var result = await _repository.GetByOperatorAsync("OPERATOR001");

        // Assert
        result.Should().HaveCount(2);
        result.All(l => l.OperatorIdm == "OPERATOR001").Should().BeTrue();
    }

    #endregion

    #region GetByTargetAsync テスト

    [Fact]
    public async Task GetByTargetAsync_WithMatchingTarget_ReturnsLogs()
    {
        // Arrange
        var log1 = CreateTestLog(targetTable: "ic_card", targetId: "CARD001");
        var log2 = CreateTestLog(targetTable: "ic_card", targetId: "CARD001");
        var log3 = CreateTestLog(targetTable: "ic_card", targetId: "CARD002");

        await _repository.InsertAsync(log1);
        await _repository.InsertAsync(log2);
        await _repository.InsertAsync(log3);

        // Act
        var result = await _repository.GetByTargetAsync("ic_card", "CARD001");

        // Assert
        result.Should().HaveCount(2);
    }

    #endregion

    #region SearchAsync テスト

    [Fact]
    public async Task SearchAsync_WithDateRange_FiltersCorrectly()
    {
        // Arrange
        await SeedTestLogs();

        var criteria = new OperationLogSearchCriteria
        {
            FromDate = new DateTime(2024, 1, 10),
            ToDate = new DateTime(2024, 1, 20)
        };

        // Act
        var result = await _repository.SearchAsync(criteria);

        // Assert
        result.Items.Should().NotBeEmpty();
        result.Items.All(l => l.Timestamp >= new DateTime(2024, 1, 10) &&
                               l.Timestamp <= new DateTime(2024, 1, 20, 23, 59, 59)).Should().BeTrue();
    }

    [Fact]
    public async Task SearchAsync_WithAction_FiltersCorrectly()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(action: "INSERT"));
        await _repository.InsertAsync(CreateTestLog(action: "UPDATE"));
        await _repository.InsertAsync(CreateTestLog(action: "DELETE"));

        var criteria = new OperationLogSearchCriteria { Action = "INSERT" };

        // Act
        var result = await _repository.SearchAsync(criteria);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items.First().Action.Should().Be("INSERT");
    }

    [Fact]
    public async Task SearchAsync_WithTargetTable_FiltersCorrectly()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(targetTable: "staff"));
        await _repository.InsertAsync(CreateTestLog(targetTable: "ic_card"));
        await _repository.InsertAsync(CreateTestLog(targetTable: "ledger"));

        var criteria = new OperationLogSearchCriteria { TargetTable = "ic_card" };

        // Act
        var result = await _repository.SearchAsync(criteria);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items.First().TargetTable.Should().Be("ic_card");
    }

    [Fact]
    public async Task SearchAsync_WithOperatorName_FiltersWithPartialMatch()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(operatorName: "山田太郎"));
        await _repository.InsertAsync(CreateTestLog(operatorName: "山田花子"));
        await _repository.InsertAsync(CreateTestLog(operatorName: "鈴木一郎"));

        var criteria = new OperationLogSearchCriteria { OperatorName = "山田" };

        // Act
        var result = await _repository.SearchAsync(criteria);

        // Assert
        result.Items.Should().HaveCount(2);
        result.Items.All(l => l.OperatorName.Contains("山田")).Should().BeTrue();
    }

    [Fact]
    public async Task SearchAsync_WithTargetId_FiltersWithPartialMatch()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(targetId: "0123456789ABCDEF"));
        await _repository.InsertAsync(CreateTestLog(targetId: "FEDCBA9876543210"));

        var criteria = new OperationLogSearchCriteria { TargetId = "0123" };

        // Act
        var result = await _repository.SearchAsync(criteria);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items.First().TargetId.Should().Contain("0123");
    }

    [Fact]
    public async Task SearchAsync_WithMultipleCriteria_FiltersCorrectly()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(
            timestamp: new DateTime(2024, 1, 15),
            action: "INSERT",
            targetTable: "ic_card"));
        await _repository.InsertAsync(CreateTestLog(
            timestamp: new DateTime(2024, 1, 15),
            action: "UPDATE",
            targetTable: "ic_card"));
        await _repository.InsertAsync(CreateTestLog(
            timestamp: new DateTime(2024, 2, 15),
            action: "INSERT",
            targetTable: "ic_card"));

        var criteria = new OperationLogSearchCriteria
        {
            FromDate = new DateTime(2024, 1, 1),
            ToDate = new DateTime(2024, 1, 31),
            Action = "INSERT",
            TargetTable = "ic_card"
        };

        // Act
        var result = await _repository.SearchAsync(criteria);

        // Assert
        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchAsync_Pagination_WorksCorrectly()
    {
        // Arrange
        for (int i = 0; i < 25; i++)
        {
            await _repository.InsertAsync(CreateTestLog());
        }

        var criteria = new OperationLogSearchCriteria();

        // Act
        var page1 = await _repository.SearchAsync(criteria, page: 1, pageSize: 10);
        var page2 = await _repository.SearchAsync(criteria, page: 2, pageSize: 10);
        var page3 = await _repository.SearchAsync(criteria, page: 3, pageSize: 10);

        // Assert
        page1.Items.Should().HaveCount(10);
        page1.TotalCount.Should().Be(25);
        page1.TotalPages.Should().Be(3);
        page1.CurrentPage.Should().Be(1);
        page1.HasPreviousPage.Should().BeFalse();
        page1.HasNextPage.Should().BeTrue();

        page2.Items.Should().HaveCount(10);
        page2.HasPreviousPage.Should().BeTrue();
        page2.HasNextPage.Should().BeTrue();

        page3.Items.Should().HaveCount(5);
        page3.HasPreviousPage.Should().BeTrue();
        page3.HasNextPage.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_EmptyCriteria_ReturnsAll()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog());
        await _repository.InsertAsync(CreateTestLog());
        await _repository.InsertAsync(CreateTestLog());

        var criteria = new OperationLogSearchCriteria();

        // Act
        var result = await _repository.SearchAsync(criteria);

        // Assert
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(3);
    }

    #endregion

    #region SearchAllAsync テスト

    [Fact]
    public async Task SearchAllAsync_WithCriteria_ReturnsAllMatchingLogs()
    {
        // Arrange
        for (int i = 0; i < 100; i++)
        {
            await _repository.InsertAsync(CreateTestLog(action: i % 2 == 0 ? "INSERT" : "UPDATE"));
        }

        var criteria = new OperationLogSearchCriteria { Action = "INSERT" };

        // Act
        var result = await _repository.SearchAllAsync(criteria);

        // Assert
        result.Should().HaveCount(50);
    }

    #endregion

    #region Helper Methods

    private static OperationLog CreateTestLog(
        DateTime? timestamp = null,
        string? operatorIdm = null,
        string? operatorName = null,
        string? targetTable = null,
        string? targetId = null,
        string? action = null,
        string? beforeData = null,
        string? afterData = null)
    {
        return new OperationLog
        {
            Timestamp = timestamp ?? DateTime.Now,
            OperatorIdm = operatorIdm ?? "TEST_OPERATOR",
            OperatorName = operatorName ?? "テスト操作者",
            TargetTable = targetTable ?? "ic_card",
            TargetId = targetId ?? "TEST_TARGET_ID",
            Action = action ?? "INSERT",
            BeforeData = beforeData,
            AfterData = afterData ?? "{\"test\":\"data\"}"
        };
    }

    private async Task SeedTestLogs()
    {
        var logs = new[]
        {
            CreateTestLog(timestamp: new DateTime(2024, 1, 5, 9, 0, 0), action: "INSERT"),
            CreateTestLog(timestamp: new DateTime(2024, 1, 15, 10, 0, 0), action: "UPDATE"),
            CreateTestLog(timestamp: new DateTime(2024, 1, 20, 14, 30, 0), action: "DELETE"),
            CreateTestLog(timestamp: new DateTime(2024, 2, 1, 9, 0, 0), action: "INSERT"),
            CreateTestLog(timestamp: new DateTime(2024, 2, 15, 16, 0, 0), action: "UPDATE"),
        };

        foreach (var log in logs)
        {
            await _repository.InsertAsync(log);
        }
    }

    #endregion
}
