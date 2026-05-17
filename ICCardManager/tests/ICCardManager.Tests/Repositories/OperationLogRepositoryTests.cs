using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using Xunit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


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

        // 各テストの前にテストデータをクリーンアップ
        ClearTestData();
    }

    /// <summary>
    /// テストデータをクリアして、テスト間の分離を確保する
    /// </summary>
    /// <remarks>
    /// インメモリSQLiteデータベースでは、同一接続を使用しないと
    /// 別のデータベースインスタンスに対して操作してしまうため、
    /// DbContext.GetConnection()を使用して同じ接続を取得する。
    /// </remarks>
    private void ClearTestData()
    {
        using var lease = _dbContext.LeaseConnection();
        var connection = lease.Connection;
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

    [Fact]
    public async Task GetByDateRangeAsync_ReturnsSortedByTimestampAscending()
    {
        // Arrange: 異なる時刻で3件登録（Issue #787: 古い順にソートされることを検証）
        var log1 = CreateTestLog(timestamp: new DateTime(2024, 1, 20, 14, 0, 0));
        var log2 = CreateTestLog(timestamp: new DateTime(2024, 1, 10, 9, 0, 0));
        var log3 = CreateTestLog(timestamp: new DateTime(2024, 1, 15, 12, 0, 0));

        await _repository.InsertAsync(log1);
        await _repository.InsertAsync(log2);
        await _repository.InsertAsync(log3);

        // Act
        var result = (await _repository.GetByDateRangeAsync(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 31))).ToList();

        // Assert: 古い順（ASC）で返ること
        result.Should().HaveCount(3);
        result[0].Timestamp.Should().Be(new DateTime(2024, 1, 10, 9, 0, 0));
        result[1].Timestamp.Should().Be(new DateTime(2024, 1, 15, 12, 0, 0));
        result[2].Timestamp.Should().Be(new DateTime(2024, 1, 20, 14, 0, 0));
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
    public async Task SearchFirstPageAsync_WithDateRange_FiltersCorrectly()
    {
        // Arrange
        await SeedTestLogs();

        var criteria = new OperationLogSearchCriteria
        {
            FromDate = new DateTime(2024, 1, 10),
            ToDate = new DateTime(2024, 1, 20)
        };

        // Act
        var result = await _repository.SearchFirstPageAsync(criteria, pageSize: 100);

        // Assert
        result.Items.Should().NotBeEmpty();
        result.Items.All(l => l.Timestamp >= new DateTime(2024, 1, 10) &&
                               l.Timestamp <= new DateTime(2024, 1, 20, 23, 59, 59)).Should().BeTrue();
    }

    [Fact]
    public async Task SearchFirstPageAsync_WithAction_FiltersCorrectly()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(action: "INSERT"));
        await _repository.InsertAsync(CreateTestLog(action: "UPDATE"));
        await _repository.InsertAsync(CreateTestLog(action: "DELETE"));

        var criteria = new OperationLogSearchCriteria { Action = "INSERT" };

        // Act
        var result = await _repository.SearchFirstPageAsync(criteria, pageSize: 100);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].Action.Should().Be("INSERT");
    }

    [Fact]
    public async Task SearchFirstPageAsync_WithTargetTable_FiltersCorrectly()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(targetTable: "staff"));
        await _repository.InsertAsync(CreateTestLog(targetTable: "ic_card"));
        await _repository.InsertAsync(CreateTestLog(targetTable: "ledger"));

        var criteria = new OperationLogSearchCriteria { TargetTable = "ic_card" };

        // Act
        var result = await _repository.SearchFirstPageAsync(criteria, pageSize: 100);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].TargetTable.Should().Be("ic_card");
    }

    [Fact]
    public async Task SearchFirstPageAsync_WithOperatorName_FiltersWithPartialMatch()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(operatorName: "山田太郎"));
        await _repository.InsertAsync(CreateTestLog(operatorName: "山田花子"));
        await _repository.InsertAsync(CreateTestLog(operatorName: "鈴木一郎"));

        var criteria = new OperationLogSearchCriteria { OperatorName = "山田" };

        // Act
        var result = await _repository.SearchFirstPageAsync(criteria, pageSize: 100);

        // Assert
        result.Items.Should().HaveCount(2);
        result.Items.All(l => l.OperatorName.Contains("山田")).Should().BeTrue();
    }

    [Fact]
    public async Task SearchFirstPageAsync_WithTargetId_FiltersWithPartialMatch()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog(targetId: "0123456789ABCDEF"));
        await _repository.InsertAsync(CreateTestLog(targetId: "FEDCBA9876543210"));

        var criteria = new OperationLogSearchCriteria { TargetId = "0123" };

        // Act
        var result = await _repository.SearchFirstPageAsync(criteria, pageSize: 100);

        // Assert
        result.Items.Should().HaveCount(1);
        result.Items[0].TargetId.Should().Contain("0123");
    }

    [Fact]
    public async Task SearchFirstPageAsync_WithMultipleCriteria_FiltersCorrectly()
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
        var result = await _repository.SearchFirstPageAsync(criteria, pageSize: 100);

        // Assert
        result.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task SearchFirstPageAsync_EmptyCriteria_ReturnsAll()
    {
        // Arrange
        await _repository.InsertAsync(CreateTestLog());
        await _repository.InsertAsync(CreateTestLog());
        await _repository.InsertAsync(CreateTestLog());

        var criteria = new OperationLogSearchCriteria();

        // Act
        var result = await _repository.SearchFirstPageAsync(criteria, pageSize: 100);

        // Assert
        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(3);
    }

    [Fact]
    public async Task SearchFirstPageAsync_ReturnsSortedByTimestampAscending()
    {
        // Arrange: Issue #787 — 古い順にソートされることを検証
        await _repository.InsertAsync(CreateTestLog(timestamp: new DateTime(2024, 1, 20, 14, 0, 0)));
        await _repository.InsertAsync(CreateTestLog(timestamp: new DateTime(2024, 1, 10, 9, 0, 0)));
        await _repository.InsertAsync(CreateTestLog(timestamp: new DateTime(2024, 1, 15, 12, 0, 0)));

        var criteria = new OperationLogSearchCriteria();

        // Act
        var result = await _repository.SearchFirstPageAsync(criteria, pageSize: 100);

        // Assert: 古い順（ASC）で返ること — 最新ログが画面の下に表示される
        result.Items.Should().HaveCount(3);
        result.Items[0].Timestamp.Should().Be(new DateTime(2024, 1, 10, 9, 0, 0));
        result.Items[1].Timestamp.Should().Be(new DateTime(2024, 1, 15, 12, 0, 0));
        result.Items[2].Timestamp.Should().Be(new DateTime(2024, 1, 20, 14, 0, 0));
    }

    #endregion

    #region keyset pagination テスト（Issue #1479）

    [Fact]
    public async Task SearchFirstPageAsync_ReturnsFirstNItems_OrderedByTimestampIdAsc()
    {
        // Arrange: 25 件挿入（タイムスタンプは挿入順に1秒ずつ進める）
        for (int i = 0; i < 25; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        // Act
        var page = await _repository.SearchFirstPageAsync(new OperationLogSearchCriteria(), pageSize: 10);

        // Assert
        page.Items.Should().HaveCount(10);
        page.TotalCount.Should().Be(25);
        page.HasPrevious.Should().BeFalse();
        page.HasNext.Should().BeTrue();
        page.FirstCursor.Should().NotBeNull();
        page.LastCursor.Should().NotBeNull();
        page.Items[0].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 0));
        page.Items[9].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 9));
    }

    [Fact]
    public async Task SearchFirstPageAsync_EmptyTable_ReturnsEmptyPage()
    {
        // Act
        var page = await _repository.SearchFirstPageAsync(new OperationLogSearchCriteria(), pageSize: 10);

        // Assert
        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.HasPrevious.Should().BeFalse();
        page.HasNext.Should().BeFalse();
        page.FirstCursor.Should().BeNull();
        page.LastCursor.Should().BeNull();
    }

    [Fact]
    public async Task SearchFirstPageAsync_LessThanPageSize_HasNextFalse()
    {
        // Arrange: 3 件のみ
        for (int i = 0; i < 3; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        // Act
        var page = await _repository.SearchFirstPageAsync(new OperationLogSearchCriteria(), pageSize: 10);

        // Assert
        page.Items.Should().HaveCount(3);
        page.HasNext.Should().BeFalse();
        page.HasPrevious.Should().BeFalse();
    }

    [Fact]
    public async Task SearchNextPageAsync_NavigatesForwardCorrectly()
    {
        // Arrange: 25 件挿入
        for (int i = 0; i < 25; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        var criteria = new OperationLogSearchCriteria();

        // Act
        var page1 = await _repository.SearchFirstPageAsync(criteria, pageSize: 10);
        var page2 = await _repository.SearchNextPageAsync(criteria, page1.LastCursor, pageSize: 10);
        var page3 = await _repository.SearchNextPageAsync(criteria, page2.LastCursor, pageSize: 10);

        // Assert
        page2.Items.Should().HaveCount(10);
        page2.Items[0].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 10));
        page2.HasPrevious.Should().BeTrue();
        page2.HasNext.Should().BeTrue();

        page3.Items.Should().HaveCount(5);
        page3.Items[0].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 20));
        page3.HasPrevious.Should().BeTrue();
        page3.HasNext.Should().BeFalse();
    }

    [Fact]
    public async Task SearchNextPageAsync_HandlesTimestampTies_ByIdOrder()
    {
        // Arrange: 同一 timestamp で 5 件、その後 5 件挿入。pageSize=3 で境界がタイ上にかかる。
        var sameTs = new DateTime(2024, 1, 1, 12, 0, 0);
        for (int i = 0; i < 5; i++)
        {
            await _repository.InsertAsync(CreateTestLog(timestamp: sameTs));
        }
        for (int i = 0; i < 5; i++)
        {
            await _repository.InsertAsync(CreateTestLog(timestamp: sameTs.AddSeconds(i + 1)));
        }

        var criteria = new OperationLogSearchCriteria();

        // Act
        var page1 = await _repository.SearchFirstPageAsync(criteria, pageSize: 3);
        var page2 = await _repository.SearchNextPageAsync(criteria, page1.LastCursor, pageSize: 3);

        // Assert: id ASC で連続している
        page1.Items.Should().HaveCount(3);
        page2.Items.Should().HaveCount(3);
        page1.Items[2].Id.Should().BeLessThan(page2.Items[0].Id);
        page1.Items.All(l => l.Timestamp == sameTs).Should().BeTrue();
        // 4 件目と 5 件目は同タイムスタンプ、6 件目から先は別タイムスタンプ
        page2.Items[0].Timestamp.Should().Be(sameTs);
        page2.Items[1].Timestamp.Should().Be(sameTs);
        page2.Items[2].Timestamp.Should().Be(sameTs.AddSeconds(1));
    }

    [Fact]
    public async Task SearchPreviousPageAsync_NavigatesBackwardCorrectly()
    {
        // Arrange
        for (int i = 0; i < 25; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        var criteria = new OperationLogSearchCriteria();

        // Act: First → Next → Next → Previous で page2 と一致するか
        var page1 = await _repository.SearchFirstPageAsync(criteria, pageSize: 10);
        var page2 = await _repository.SearchNextPageAsync(criteria, page1.LastCursor, pageSize: 10);
        var page3 = await _repository.SearchNextPageAsync(criteria, page2.LastCursor, pageSize: 10);
        var backToPage2 = await _repository.SearchPreviousPageAsync(criteria, page3.FirstCursor, pageSize: 10);

        // Assert
        backToPage2.Items.Should().HaveCount(10);
        backToPage2.Items.Select(l => l.Id).Should().Equal(page2.Items.Select(l => l.Id));
        backToPage2.HasPrevious.Should().BeTrue();
        backToPage2.HasNext.Should().BeTrue();
    }

    [Fact]
    public async Task SearchPreviousPageAsync_AtBeginning_HasPreviousFalse()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        var criteria = new OperationLogSearchCriteria();

        // Act: page2 から前ページ（=page1相当）に戻る
        var page1 = await _repository.SearchFirstPageAsync(criteria, pageSize: 10);
        var page2 = await _repository.SearchNextPageAsync(criteria, page1.LastCursor, pageSize: 10);
        var backToFirst = await _repository.SearchPreviousPageAsync(criteria, page2.FirstCursor, pageSize: 10);

        // Assert: 戻った先頭ページにはさらに前のページが無い
        backToFirst.Items.Should().HaveCount(10);
        backToFirst.HasPrevious.Should().BeFalse();
        backToFirst.HasNext.Should().BeTrue();
        backToFirst.Items.Select(l => l.Id).Should().Equal(page1.Items.Select(l => l.Id));
    }

    [Fact]
    public async Task SearchLastPageAsync_ReturnsLastNItems()
    {
        // Arrange
        for (int i = 0; i < 25; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        // Act
        var page = await _repository.SearchLastPageAsync(new OperationLogSearchCriteria(), pageSize: 10);

        // Assert: 末尾 5 件は端数で、最終 5 件のみ取得（25 % 10 = 5）。末尾ページは page3 相当の 5 件。
        page.Items.Should().HaveCount(5);
        page.Items[0].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 20));
        page.Items[4].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 24));
        page.HasNext.Should().BeFalse();
        page.HasPrevious.Should().BeTrue();
    }

    [Fact]
    public async Task SearchLastPageAsync_ExactBoundary_ReturnsFullPage()
    {
        // Arrange: 20 件挿入で pageSize=10 だと最終ページは丁度 10 件
        for (int i = 0; i < 20; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        // Act
        var page = await _repository.SearchLastPageAsync(new OperationLogSearchCriteria(), pageSize: 10);

        // Assert
        page.Items.Should().HaveCount(10);
        page.Items[0].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 10));
        page.Items[9].Timestamp.Should().Be(new DateTime(2024, 1, 1, 0, 0, 19));
        page.HasNext.Should().BeFalse();
        page.HasPrevious.Should().BeTrue();
    }

    [Fact]
    public async Task SearchLastPageAsync_SinglePage_NoPrevNoNext()
    {
        // Arrange: 5 件だけ
        for (int i = 0; i < 5; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        // Act
        var page = await _repository.SearchLastPageAsync(new OperationLogSearchCriteria(), pageSize: 10);

        // Assert
        page.Items.Should().HaveCount(5);
        page.HasPrevious.Should().BeFalse();
        page.HasNext.Should().BeFalse();
    }

    [Fact]
    public async Task SequentialNavigation_FirstToLast_ReachesAllRows()
    {
        // Arrange: 27 件（端数）
        for (int i = 0; i < 27; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        var criteria = new OperationLogSearchCriteria();

        // Act: First から HasNext=false になるまで進む
        var collected = new List<OperationLog>();
        var current = await _repository.SearchFirstPageAsync(criteria, pageSize: 10);
        collected.AddRange(current.Items);
        while (current.HasNext)
        {
            current = await _repository.SearchNextPageAsync(criteria, current.LastCursor, pageSize: 10);
            collected.AddRange(current.Items);
        }

        // Assert: 全件揃い、id がユニーク
        collected.Should().HaveCount(27);
        collected.Select(l => l.Id).Distinct().Should().HaveCount(27);
        collected.Should().BeInAscendingOrder(l => l.Timestamp);
    }

    [Fact]
    public async Task SearchLastPageAsync_BackwardNavigation_ReachesFirst()
    {
        // Arrange: 23 件
        for (int i = 0; i < 23; i++)
        {
            await _repository.InsertAsync(CreateTestLog(
                timestamp: new DateTime(2024, 1, 1, 0, 0, 0).AddSeconds(i)));
        }

        var criteria = new OperationLogSearchCriteria();

        // Act: Last から HasPrevious=false になるまで遡る
        var collected = new List<OperationLog>();
        var current = await _repository.SearchLastPageAsync(criteria, pageSize: 10);
        collected.InsertRange(0, current.Items);
        while (current.HasPrevious)
        {
            current = await _repository.SearchPreviousPageAsync(criteria, current.FirstCursor, pageSize: 10);
            collected.InsertRange(0, current.Items);
        }

        // Assert
        collected.Should().HaveCount(23);
        collected.Should().BeInAscendingOrder(l => l.Timestamp);
    }

    [Fact]
    public async Task SearchNextPageAsync_NullCursor_Throws()
    {
        // Act
        Func<Task> act = () => _repository.SearchNextPageAsync(new OperationLogSearchCriteria(), afterCursor: null, pageSize: 10);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SearchFirstPageAsync_InvalidPageSize_Throws()
    {
        // Act
        Func<Task> act = () => _repository.SearchFirstPageAsync(new OperationLogSearchCriteria(), pageSize: 0);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
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
