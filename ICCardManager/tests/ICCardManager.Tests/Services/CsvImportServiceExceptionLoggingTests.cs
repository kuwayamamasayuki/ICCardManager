using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Common.Exceptions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1282: CsvImportService の catch (SQLiteException) / catch (Exception) 両ブロックで
/// 例外を握りつぶさずに LogError で痕跡を残すことを保証する。
/// </summary>
public class CsvImportServiceExceptionLoggingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly SQLiteConnection _connection;
    private readonly Mock<DbContext> _dbContextMock;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly Mock<ILogger<CsvImportService>> _loggerMock;

    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    public CsvImportServiceExceptionLoggingTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportLog_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _cardRepositoryMock = new Mock<ICardRepository>();
        _staffRepositoryMock = new Mock<IStaffRepository>();
        _ledgerRepositoryMock = new Mock<ILedgerRepository>();
        _validationServiceMock = new Mock<IValidationService>();
        _dbContextMock = new Mock<DbContext>();
        _cacheServiceMock = new Mock<ICacheService>();
        _loggerMock = new Mock<ILogger<CsvImportService>>();

        _validationServiceMock.Setup(x => x.ValidateCardIdm(It.IsAny<string>()))
            .Returns(ValidationResult.Success());
        _validationServiceMock.Setup(x => x.ValidateStaffIdm(It.IsAny<string>()))
            .Returns(ValidationResult.Success());

        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();

        var lease = new ConnectionLease(_connection, () => { });
        var transaction = _connection.BeginTransaction();
        var scope = new ICCardManager.Data.TransactionScope(lease, transaction);
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);
    }

    public void Dispose()
    {
        try { _connection?.Dispose(); } catch { }
        try { if (Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private CsvImportService CreateService()
    {
        return new CsvImportService(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            _validationServiceMock.Object,
            _dbContextMock.Object,
            _cacheServiceMock.Object,
            _loggerMock.Object);
    }

    private string CreateCardsCsv()
    {
        var path = Path.Combine(_testDirectory, $"cards_{Guid.NewGuid():N}.csv");
        var content =
            "カードIDm,カード管理番号,カード種別,備考\n" +
            "0123456789ABCDEF,TEST001,SUGOCA,テスト1\n";
        File.WriteAllText(path, content, CsvEncoding);
        return path;
    }

    private string CreateStaffCsv()
    {
        var path = Path.Combine(_testDirectory, $"staff_{Guid.NewGuid():N}.csv");
        // 最低4列（職員IDm, 氏名, 職員番号, 備考）が必要
        var content =
            "職員IDm,氏名,職員番号,備考\n" +
            "0123456789ABCDEF,山田太郎,E001,テスト\n";
        File.WriteAllText(path, content, CsvEncoding);
        return path;
    }

    /// <summary>
    /// 内部処理で投げられた SQLiteException は Internal の catch(SQLiteException) で
    /// LogError の後に DatabaseException にラップされ、外側の ExecuteImportWithErrorHandlingAsync で
    /// Success=false の結果に変換される。ここでは LogError が呼ばれたこと（無言握りつぶしでない）を確認。
    /// </summary>
    [Fact]
    public async Task ImportCardsAsync_SQLiteException発生時にLogErrorを出すこと()
    {
        // Arrange: InsertAsync 時に SQLiteException を投げるように設定
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new SQLiteException("simulated SQLite failure"));

        var service = CreateService();
        var csvPath = CreateCardsCsv();

        // Act
        var result = await service.ImportCardsAsync(csvPath, false);

        // Assert: 外側の例外ハンドラが Success=false を返し、ログが記録されている
        result.Success.Should().BeFalse();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<SQLiteException>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "Issue #1282: CSV インポート中の SQLite エラーは LogError で記録すべき");
    }

    [Fact]
    public async Task ImportCardsAsync_一般例外発生時にLogErrorを出すこと()
    {
        // Arrange: InsertAsync が一般例外（InvalidOperationException）を投げる
        _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IcCard?)null);
        _cardRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("simulated generic failure"));

        var service = CreateService();
        var csvPath = CreateCardsCsv();

        // Act
        var result = await service.ImportCardsAsync(csvPath, false);

        // Assert
        result.Success.Should().BeFalse();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce,
            "Issue #1282: CSV インポート中の想定外例外も LogError で記録すべき（無言握りつぶし防止）");
    }

    [Fact]
    public async Task ImportStaffAsync_SQLiteException発生時にLogErrorを出すこと()
    {
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new SQLiteException("simulated SQLite failure (staff)"));

        var service = CreateService();
        var csvPath = CreateStaffCsv();

        var result = await service.ImportStaffAsync(csvPath, false);
        result.Success.Should().BeFalse();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<SQLiteException>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ImportStaffAsync_一般例外発生時にLogErrorを出すこと()
    {
        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("simulated generic failure (staff)"));

        var service = CreateService();
        var csvPath = CreateStaffCsv();

        var result = await service.ImportStaffAsync(csvPath, false);
        result.Success.Should().BeFalse();

        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<InvalidOperationException>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.AtLeastOnce);
    }
}
