using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2156: 職員の登録・更新・削除・復元が、監査ログと 1 トランザクションで確定することを
/// 実 DB（インメモリ SQLite）の行で検証する。
/// </summary>
/// <remarks>
/// 作りは <see cref="CardManagementServiceTests"/> と同じ（職員は <c>bool</c> だけを返し、
/// 重複の例外を持たない）。「欠陥を突く側」と「正当な操作を塞いでいない側」を対で置く。
/// </remarks>
public sealed class StaffManagementServiceTests : IDisposable
{
    private const string TestStaffIdm = "0123456789ABCDEF";
    private const string OperatorIdm = "FFFF000000000009";
    private const string OperatorName = "庶務 担当";

    private readonly DbContext _dbContext;
    private readonly RecordingRetryDelay _retryDelay;
    private readonly StaffRepository _staffRepository;
    private readonly OperationLogRepository _realOperationLogRepository;

    public StaffManagementServiceTests()
    {
        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();
        _retryDelay = RecordingRetryDelay.AttachTo(_dbContext);

        _staffRepository = new StaffRepository(
            _dbContext, CreatePassThroughCacheService(), Options.Create(new CacheOptions()),
            NullLogger<StaffRepository>.Instance);
        _realOperationLogRepository = new OperationLogRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region 欠陥を突く側 — 監査ログの失敗で本処理も巻き戻る

    [Fact]
    public async Task RegisterAsync_監査ログの書き込みに失敗したら_職員も登録されないこと()
    {
        // Arrange
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.RegisterAsync(NewStaff("博多 太郎"));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm, includeDeleted: true)).Should().BeNull();
        (await CountLogsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdateAsync_監査ログの書き込みに失敗したら_職員情報も更新されないこと()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎");
        var before = await _staffRepository.GetByIdmAsync(TestStaffIdm);
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.UpdateAsync(before!, NewStaff("博多 次郎"));

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm))!.Name.Should().Be("博多 太郎");
        (await CountLogsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_監査ログの書き込みに失敗したら_職員も削除されないこと()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎");
        var staff = await _staffRepository.GetByIdmAsync(TestStaffIdm);
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.DeleteAsync(staff!);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm)).Should().NotBeNull();
        (await CountLogsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RestoreAsync_監査ログの書き込みに失敗したら_職員も復元されないこと()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎");
        (await _staffRepository.DeleteAsync(TestStaffIdm)).Should().BeTrue();
        var deleted = await _staffRepository.GetByIdmAsync(TestStaffIdm, includeDeleted: true);
        var service = CreateService(FailingOperationLogRepository());

        // Act
        var act = () => service.RestoreAsync(deleted!);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm, includeDeleted: true))!.IsDeleted.Should().BeTrue();
        (await CountLogsAsync()).Should().Be(0);
    }

    #endregion

    #region 正当な操作を塞いでいない側 — 本処理と監査ログがそろって 1 件ずつ残る

    [Fact]
    public async Task RegisterAsync_通常の登録_職員と監査ログがそろって1件ずつ残ること()
    {
        // Arrange
        var operationLogs = PassThroughOperationLogRepository();
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.RegisterAsync(NewStaff("博多 太郎"));

        // Assert
        result.Should().BeTrue();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm))!.Name.Should().Be("博多 太郎");
        var log = (await ReadLogsAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Insert);
        log.OperatorIdm.Should().Be(OperatorIdm);
        log.OperatorName.Should().Be(OperatorName);
        JsonSerializer.Deserialize<Staff>(log.AfterData!)!.Name.Should().Be("博多 太郎");
        operationLogs.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never,
            "監査ログは本処理と同じトランザクションで書く（tx なしの旧経路を通らない）");
    }

    [Fact]
    public async Task UpdateAsync_通常の更新_更新と監査ログがそろって1件ずつ残ること()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎");
        var before = await _staffRepository.GetByIdmAsync(TestStaffIdm);
        var operationLogs = PassThroughOperationLogRepository();
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.UpdateAsync(before!, NewStaff("博多 次郎"));

        // Assert
        result.Should().BeTrue();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm))!.Name.Should().Be("博多 次郎");
        var log = (await ReadLogsAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Update);
        JsonSerializer.Deserialize<Staff>(log.BeforeData!)!.Name.Should().Be("博多 太郎");
        JsonSerializer.Deserialize<Staff>(log.AfterData!)!.Name.Should().Be("博多 次郎");
        operationLogs.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_通常の削除_削除と監査ログがそろって1件ずつ残ること()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎");
        var staff = await _staffRepository.GetByIdmAsync(TestStaffIdm);
        var operationLogs = PassThroughOperationLogRepository();
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.DeleteAsync(staff!);

        // Assert
        result.Should().BeTrue();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm, includeDeleted: true))!.IsDeleted.Should().BeTrue();
        var log = (await ReadLogsAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Delete);
        JsonSerializer.Deserialize<Staff>(log.BeforeData!)!.Name.Should().Be("博多 太郎");
        operationLogs.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);
    }

    [Fact]
    public async Task RestoreAsync_通常の復元_復元と監査ログがそろって1件ずつ残り_変えていない列は保たれること()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎", number: "S-0042", note: "総務課");
        (await _staffRepository.DeleteAsync(TestStaffIdm)).Should().BeTrue();
        var deleted = await _staffRepository.GetByIdmAsync(TestStaffIdm, includeDeleted: true);
        var operationLogs = PassThroughOperationLogRepository();
        var service = CreateService(operationLogs.Object);

        // Act
        var result = await service.RestoreAsync(deleted!);

        // Assert
        result.Should().BeTrue();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm)).Should().NotBeNull();
        var log = (await ReadLogsAsync()).Should().ContainSingle().Subject;
        log.Action.Should().Be(OperationLogger.Actions.Restore);
        var after = JsonSerializer.Deserialize<Staff>(log.AfterData!)!;
        after.IsDeleted.Should().BeFalse();
        after.DeletedAt.Should().BeNull();
        after.Number.Should().Be("S-0042", "この操作が変えていない列は復元前の値を保つこと");
        after.Note.Should().Be("総務課");
        operationLogs.Verify(r => r.InsertAsync(It.IsAny<OperationLog>()), Times.Never);
    }

    #endregion

    #region 競合 — 従来どおり false を返し、監査ログは書かない

    [Fact]
    public async Task UpdateAsync_編集中に削除されていたら_falseを返し監査ログを書かないこと()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎");
        var before = await _staffRepository.GetByIdmAsync(TestStaffIdm);
        (await _staffRepository.DeleteAsync(TestStaffIdm)).Should().BeTrue();
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.UpdateAsync(before!, NewStaff("博多 次郎"));

        // Assert
        result.Should().BeFalse("影響行数 0 は競合（Issue #1753）。ViewModel が ConcurrencyConflictMessage で案内する");
        (await CountLogsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_先に削除されていたら_falseを返し監査ログを書かないこと()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎");
        var staff = await _staffRepository.GetByIdmAsync(TestStaffIdm);
        (await _staffRepository.DeleteAsync(TestStaffIdm)).Should().BeTrue("他 PC が先に削除した");
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.DeleteAsync(staff!);

        // Assert
        result.Should().BeFalse();
        (await CountLogsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RestoreAsync_先に復元されていたら_falseを返し監査ログを書かないこと()
    {
        // Arrange
        await SeedStaffAsync("博多 太郎");
        (await _staffRepository.DeleteAsync(TestStaffIdm)).Should().BeTrue();
        var deleted = await _staffRepository.GetByIdmAsync(TestStaffIdm, includeDeleted: true);
        (await _staffRepository.RestoreAsync(TestStaffIdm)).Should().BeTrue("他 PC が先に復元した");
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.RestoreAsync(deleted!);

        // Assert
        result.Should().BeFalse();
        (await CountLogsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RegisterAsync_同じ職員証が登録済みなら_falseを返し監査ログを書かないこと()
    {
        // Arrange — 主キー重複は一過性でない失敗なので false へ畳まれる（Issue #1951 / #2001）
        await SeedStaffAsync("博多 太郎");
        var service = CreateService(PassThroughOperationLogRepository().Object);

        // Act
        var result = await service.RegisterAsync(NewStaff("博多 次郎"));

        // Assert
        result.Should().BeFalse();
        (await _staffRepository.GetByIdmAsync(TestStaffIdm))!.Name.Should().Be("博多 太郎");
        (await CountLogsAsync()).Should().Be(0);
        _retryDelay.Delays.Should().BeEmpty("主キー重複は何度やっても失敗するのでリトライしない");
    }

    #endregion

    #region ヘルパー

    private StaffManagementService CreateService(IOperationLogRepository operationLogRepository)
    {
        var operatorContext = new Mock<ICurrentOperatorContext>();
        operatorContext.SetupGet(c => c.HasSession).Returns(true);
        operatorContext.SetupGet(c => c.CurrentIdm).Returns(OperatorIdm);
        operatorContext.SetupGet(c => c.CurrentName).Returns(OperatorName);

        return new StaffManagementService(
            _dbContext,
            _staffRepository,
            new OperationLogger(operationLogRepository, operatorContext.Object),
            NullLogger<StaffManagementService>.Instance);
    }

    private Mock<IOperationLogRepository> PassThroughOperationLogRepository()
    {
        var mock = new Mock<IOperationLogRepository>();
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>()))
            .Returns<OperationLog>(log => _realOperationLogRepository.InsertAsync(log));
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .Returns<OperationLog, SQLiteTransaction>((log, tx) => _realOperationLogRepository.InsertAsync(log, tx));
        return mock;
    }

    private IOperationLogRepository FailingOperationLogRepository()
    {
        var mock = PassThroughOperationLogRepository();
        mock.Setup(r => r.InsertAsync(It.IsAny<OperationLog>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("operation_log への書き込みに失敗（テスト注入）"));
        return mock.Object;
    }

    private static Staff NewStaff(string name, string? number = null, string? note = null) => new()
    {
        StaffIdm = TestStaffIdm,
        Name = name,
        Number = number,
        Note = note,
    };

    private async Task SeedStaffAsync(string name, string? number = null, string? note = null)
    {
        (await _staffRepository.InsertAsync(NewStaff(name, number, note))).Should().BeTrue();
    }

    private async Task<int> CountLogsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operation_log WHERE target_table = 'staff' AND target_id = @idm";
        command.Parameters.AddWithValue("@idm", TestStaffIdm);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<List<OperationLog>> ReadLogsAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = @"SELECT action, operator_idm, operator_name, before_data, after_data
FROM operation_log WHERE target_table = 'staff' AND target_id = @idm";
        command.Parameters.AddWithValue("@idm", TestStaffIdm);
        var logs = new List<OperationLog>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            logs.Add(new OperationLog
            {
                Action = reader.GetString(0),
                OperatorIdm = reader.GetString(1),
                OperatorName = reader.GetString(2),
                BeforeData = reader.IsDBNull(3) ? null : reader.GetString(3),
                AfterData = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }
        return logs;
    }

    private static ICacheService CreatePassThroughCacheService()
    {
        var mock = new Mock<ICacheService>();
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<Staff>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan _) => factory());
        return mock.Object;
    }

    #endregion
}
