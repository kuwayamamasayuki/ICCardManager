using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
/// カード／職員 CSV インポートの<b>トランザクション内の catch</b>（Issue #1282 / #1745 / #1991）が、
/// 失敗を「ロールバックより先に」「1 回だけ」記録し、「記録済みの印」を付けて再スローすることを保証する。
/// </summary>
/// <remarks>
/// <para>
/// Issue #2106: 旧版は「Error レベルで該当型の例外が 1 回以上記録されたこと」しか見ておらず、
/// 内側の catch を丸ごと消しても、外側の共通ハンドラー（<c>LogImportFailure</c>）が同じ型の例外を
/// Error で記録するため緑のままだった。<c>RawExceptionMessageExposureTests</c> の
/// 「技術的詳細をログへ残すこと」と実質同じ内容でもあった。
/// </para>
/// <para>
/// ここでは内側の catch にしか無い性質を表明する。
/// <list type="bullet">
/// <item>記録の文言がトランザクション内の局面（「〇〇CSVインポートのトランザクション中に…」）を名乗る
///   ― 外側は操作名（「カードCSVの取り込み」）しか名乗らない（内側の catch を消すと赤）</item>
/// <item>記録した時点でトランザクションがまだ巻き戻っていない（ログをロールバックの後ろへ回すと赤。#1745）</item>
/// <item>Error の記録は 1 件だけ（「記録済みの印」を付け忘れると外側が再度記録して赤。#1991）</item>
/// <item>SQLite の失敗は元の <see cref="SQLiteException"/> を記録し、<see cref="DatabaseException"/> へ包んで
///   整備済みの文言で報告する（ラップを外すと赤）</item>
/// </list>
/// 外側の共通ハンドラーの振る舞い（トランザクション前の失敗を記録する・想定内は Warning）は
/// <c>RawExceptionMessageExposureTests</c> が担う。
/// </para>
/// </remarks>
public class CsvImportServiceExceptionLoggingTests : IDisposable
{
    private readonly string _testDirectory;
    private readonly SQLiteConnection _connection;
    private readonly SQLiteTransaction _transaction;
    private readonly Mock<DbContext> _dbContextMock;
    private readonly Mock<ICardRepository> _cardRepositoryMock;
    private readonly Mock<IStaffRepository> _staffRepositoryMock;
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock;
    private readonly Mock<IValidationService> _validationServiceMock;
    private readonly Mock<ICacheService> _cacheServiceMock;
    /// <summary>Issue #1955: 摘要の再生成が参照する部署種別の供給元。</summary>
    private readonly Mock<ISettingsRepository> _settingsRepositoryMock;
    private readonly TransactionObservingLogger _logger;

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
        _settingsRepositoryMock = new Mock<ISettingsRepository>();
        _settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync())
            .ReturnsAsync(new AppSettings());

        _validationServiceMock.Setup(x => x.ValidateCardIdm(It.IsAny<string>()))
            .Returns(ValidationResult.Success());
        _validationServiceMock.Setup(x => x.ValidateStaffIdm(It.IsAny<string>()))
            .Returns(ValidationResult.Success());

        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();

        var lease = new ConnectionLease(_connection, () => { });
        _transaction = _connection.BeginTransaction();
        var scope = new ICCardManager.Data.TransactionScope(lease, _transaction);
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(scope);

        _logger = new TransactionObservingLogger(_transaction);
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
            _settingsRepositoryMock.Object,
            _logger);
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
    /// トランザクション内（INSERT）で失敗させ、取込を実行する。
    /// </summary>
    /// <param name="target">"card" または "staff"</param>
    /// <param name="thrown">INSERT が投げる例外</param>
    private async System.Threading.Tasks.Task<CsvImportResult> ImportFailingInsideTransactionAsync(
        string target, Exception thrown)
    {
        var service = CreateService();
        if (target == "card")
        {
            _cardRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync((IcCard?)null);
            _cardRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()))
                .ThrowsAsync(thrown);
            return await service.ImportCardsAsync(CreateCardsCsv(), false);
        }

        _staffRepositoryMock.Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(thrown);
        return await service.ImportStaffAsync(CreateStaffCsv(), false);
    }

    /// <summary>
    /// トランザクション内の失敗は、内側の catch が「局面を名乗って」「ロールバックより先に」
    /// 「1 回だけ」Error で記録する（Issue #1282 / #1745 / #1991）。
    /// </summary>
    [Theory]
    [InlineData("card", "カードCSVインポートのトランザクション中に SQLite エラーが発生しロールバック", true)]
    [InlineData("card", "カードCSVインポートのトランザクション中に想定外の例外が発生しロールバック", false)]
    [InlineData("staff", "職員CSVインポートのトランザクション中に SQLite エラーが発生しロールバック", true)]
    [InlineData("staff", "職員CSVインポートのトランザクション中に想定外の例外が発生しロールバック", false)]
    public async System.Threading.Tasks.Task トランザクション内の失敗は内側のcatchがロールバックより先に1回だけ記録すること(
        string target, string expectedOperation, bool sqliteFailure)
    {
        // Arrange
        Exception thrown = sqliteFailure
            ? new SQLiteException("simulated SQLite failure")
            : new InvalidOperationException("simulated generic failure");

        // Act
        var result = await ImportFailingInsideTransactionAsync(target, thrown);

        // Assert
        result.Success.Should().BeFalse();

        var errors = _logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errors.Should().ContainSingle(
            "内側の catch が付けた「記録済みの印」で外側は記録しない（#1991）。実際: " + _logger.FormatEntries());

        var entry = errors[0];
        entry.Message.Should().Be($"CSV import failed: {expectedOperation}",
            "外側の共通ハンドラー（操作名のみ）ではなく、内側の catch が局面を名乗って記録すること");
        entry.Exception.Should().BeSameAs(thrown, "包む前の元の例外を記録すること");
        entry.TransactionActiveAtLogTime.Should().BeTrue(
            "ログはロールバックより先に書くこと（#1745。後ろへ回すとロールバックの失敗で痕跡ごと失われ得る）");

        TransactionObservingLogger.IsActive(_transaction).Should().BeFalse(
            "記録の後にロールバックされていること（観測手段が常に true を返していないことの対）");

        if (sqliteFailure)
        {
            // SQLiteException は DatabaseException へ包んで再スローし、整備済みの文言で報告する（#1282）
            result.ErrorMessage.Should().Be(DatabaseException.QueryFailed().UserFriendlyMessage);
        }
    }

    /// <summary>
    /// 記録の時点のトランザクション状態も併せて記録するロガー。
    /// </summary>
    /// <remarks>
    /// <see cref="SQLiteTransaction.Connection"/> はコミット／ロールバックで <c>null</c> になる。
    /// ログ出力の瞬間にまだ接続を持っていれば、ロールバックより先に記録したと分かる。
    /// </remarks>
    private sealed class TransactionObservingLogger : ILogger<CsvImportService>
    {
        private readonly SQLiteTransaction _transaction;
        private readonly List<Entry> _entries = new();
        private readonly object _sync = new();

        public TransactionObservingLogger(SQLiteTransaction transaction)
        {
            _transaction = transaction;
        }

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_sync) { return _entries.ToList(); } }
        }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => new NullScope();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = new Entry(logLevel, formatter(state, exception), exception, IsActive(_transaction));
            lock (_sync)
            {
                _entries.Add(entry);
            }
        }

        /// <summary>
        /// トランザクションがまだ巻き戻し・確定されていないか。
        /// ロールバック後は <see cref="SQLiteTransaction.Connection"/> が <c>null</c>、
        /// スコープの破棄後は <see cref="ObjectDisposedException"/> になる（どちらも「終わった」）。
        /// </summary>
        public static bool IsActive(SQLiteTransaction transaction)
        {
            try
            {
                return transaction.Connection != null;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public string FormatEntries() => string.Join(
            " / ",
            Entries.Select(e => $"[{e.Level}] {e.Message} ({e.Exception?.GetType().Name}, tx={(e.TransactionActiveAtLogTime ? "active" : "ended")})"));

        public sealed record Entry(LogLevel Level, string Message, Exception? Exception, bool TransactionActiveAtLogTime);

        private sealed class NullScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
