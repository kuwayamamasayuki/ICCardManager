using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// <see cref="SettingsRepository.SaveAppSettingsAsync"/> のトランザクション境界・
/// ロールバック・キャッシュ無効化タイミングを検証する単体テスト。
/// Issue #1240: 設定保存の複数キー更新をトランザクションで保護。
/// </summary>
public class SettingsRepositorySaveTransactionTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly Mock<ICacheService> _cacheServiceMock;
    private readonly SettingsRepository _repository;

    public SettingsRepositorySaveTransactionTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _cacheServiceMock = new Mock<ICacheService>();

        // 既存の SettingsRepositoryTests と同じく、キャッシュをバイパスしてファクトリを直接実行
        _cacheServiceMock.Setup(c => c.GetOrCreateAsync(
            It.IsAny<string>(),
            It.IsAny<Func<Task<AppSettings>>>(),
            It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<AppSettings>> factory, TimeSpan expiration) => factory());

        _repository = new SettingsRepository(_dbContext, _cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region ヘルパ

    private static AppSettings CreateValidSettings(FontSizeOption fontSize = FontSizeOption.Medium)
    {
        return new AppSettings
        {
            WarningBalance = 3000,
            FontSize = fontSize,
            BackupPath = @"D:\Backup",
            LastVacuumDate = new DateTime(2026, 4, 1),
            SoundMode = SoundMode.VoiceMale,
            ToastPosition = ToastPosition.TopRight,
            DepartmentType = DepartmentType.EnterpriseAccount,
            SkipBusStopInputOnReturn = true,
            ReportOutputFolder = @"C:\Reports",
        };
    }

    /// <summary>
    /// <see cref="DbContext.BeginTransactionAsync"/> の呼び出し回数を記録する Spy DbContext。
    /// </summary>
    private sealed class TransactionCountingDbContext : DbContext
    {
        public int BeginTransactionCallCount;

        public TransactionCountingDbContext() : base(":memory:")
        {
        }

        public override async Task<TransactionScope> BeginTransactionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref BeginTransactionCallCount);
            return await base.BeginTransactionAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <see cref="DbContext.BeginTransactionAsync"/> を常に例外で失敗させる Spy DbContext。
    /// （非 <see cref="System.Data.SQLite.SQLiteException"/> 系の例外を投げることで、
    /// <see cref="DbContext.ExecuteWithRetryAsync(System.Func{System.Threading.Tasks.Task}, CancellationToken)"/>
    /// のリトライロジックを回避し、例外をそのまま伝播させる。）
    /// </summary>
    private sealed class FailingTransactionDbContext : DbContext
    {
        public int BeginTransactionCallCount;

        public FailingTransactionDbContext() : base(":memory:")
        {
        }

        public override Task<TransactionScope> BeginTransactionAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref BeginTransactionCallCount);
            throw new InvalidOperationException("テスト用: トランザクション開始失敗");
        }
    }

    #endregion

    #region 正常系: 全キー永続化 + キャッシュ無効化

    [Fact]
    public async Task SaveAppSettingsAsync_Success_PersistsAllKeysAtomically()
    {
        // Arrange
        var settings = CreateValidSettings(FontSizeOption.Large);

        // Act
        var result = await _repository.SaveAppSettingsAsync(settings);

        // Assert: 戻り値と個別キーの永続化を確認
        result.Should().BeTrue();

        (await _repository.GetAsync(SettingsRepository.KeyWarningBalance)).Should().Be("3000");
        (await _repository.GetAsync(SettingsRepository.KeyFontSize)).Should().Be("large");
        (await _repository.GetAsync(SettingsRepository.KeyBackupPath)).Should().Be(@"D:\Backup");
        (await _repository.GetAsync(SettingsRepository.KeyLastVacuumDate)).Should().Be("2026-04-01");
        (await _repository.GetAsync(SettingsRepository.KeyDepartmentType)).Should().NotBeNull();
        (await _repository.GetAsync(SettingsRepository.KeyReportOutputFolder)).Should().Be(@"C:\Reports");
        (await _repository.GetAsync(SettingsRepository.KeySkipBusStopInputOnReturn)).Should().Be("true");
    }

    [Fact]
    public async Task SaveAppSettingsAsync_Success_InvalidatesCacheExactlyOnce()
    {
        // Arrange
        var settings = CreateValidSettings();

        // Act
        await _repository.SaveAppSettingsAsync(settings);

        // Assert: キャッシュ無効化は 1 回のみ（各 SetAsync で毎回呼ばれるべきでない）
        _cacheServiceMock.Verify(
            c => c.Invalidate(CacheKeys.AppSettings),
            Times.Once,
            "SaveAppSettingsAsync はトランザクション完了後に一度だけキャッシュを無効化すべき");
    }

    [Fact]
    public async Task SaveAppSettingsAsync_Success_InvalidatesCacheAfterAllKeysPersisted()
    {
        // Arrange: キャッシュ無効化時点で全キーが読み取れることを確認する。
        // Invalidate コールバック内で DB の状態をスナップショットし、全キーが永続化済みであることを保証する。
        var settings = CreateValidSettings(FontSizeOption.ExtraLarge);

        string? warningBalanceAtInvalidation = null;
        string? fontSizeAtInvalidation = null;
        string? reportOutputAtInvalidation = null;

        _cacheServiceMock.Setup(c => c.Invalidate(CacheKeys.AppSettings))
            .Callback(() =>
            {
                // 同じコンテキストで GetAsync してもトランザクションは既に commit 済みのため読める
                warningBalanceAtInvalidation = _repository.GetAsync(SettingsRepository.KeyWarningBalance).GetAwaiter().GetResult();
                fontSizeAtInvalidation = _repository.GetAsync(SettingsRepository.KeyFontSize).GetAwaiter().GetResult();
                reportOutputAtInvalidation = _repository.GetAsync(SettingsRepository.KeyReportOutputFolder).GetAwaiter().GetResult();
            });

        // Act
        await _repository.SaveAppSettingsAsync(settings);

        // Assert: Invalidate 呼び出し時点ですべての設定が読める = commit 済み = 順序が正しい
        warningBalanceAtInvalidation.Should().Be("3000");
        fontSizeAtInvalidation.Should().Be("xlarge");
        reportOutputAtInvalidation.Should().Be(@"C:\Reports");
    }

    [Fact]
    public async Task SaveAppSettingsAsync_Success_StartsExactlyOneTransaction()
    {
        // Arrange: Spy DbContext で BeginTransactionAsync 呼び出し回数を計測
        using var spyContext = new TransactionCountingDbContext();
        spyContext.InitializeDatabase();

        var cacheServiceMock = new Mock<ICacheService>();
        var repository = new SettingsRepository(spyContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        var settings = CreateValidSettings();

        // Act
        var result = await repository.SaveAppSettingsAsync(settings);

        // Assert: 10 以上の設定キーを書いても BeginTransactionAsync は 1 回のみ
        //（各 SetAsync ごとに個別トランザクションを作っているのではなく、全体を 1 トランザクションで囲んでいる）
        result.Should().BeTrue();
        spyContext.BeginTransactionCallCount.Should().Be(
            1,
            "全ての設定キー更新は単一トランザクション内で実行されるべき（Issue #1240 の核心）");
    }

    #endregion

    #region 失敗系: ロールバック + キャッシュ未無効化

    [Fact]
    public async Task SaveAppSettingsAsync_WhenTransactionStartFails_DoesNotInvalidateCache()
    {
        // Arrange: BeginTransactionAsync が失敗する DbContext を注入
        using var failingContext = new FailingTransactionDbContext();
        // InitializeDatabase は LeaseConnection 経由で問題なく動くが、
        // その後の BeginTransactionAsync が例外になるようにする
        failingContext.InitializeDatabase();

        var cacheServiceMock = new Mock<ICacheService>();
        var repository = new SettingsRepository(failingContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        var settings = CreateValidSettings();

        // Act
        Func<Task> act = async () => await repository.SaveAppSettingsAsync(settings);

        // Assert: 例外が伝播し、キャッシュは無効化されない
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*トランザクション開始失敗*");

        cacheServiceMock.Verify(
            c => c.Invalidate(It.IsAny<string>()),
            Times.Never,
            "トランザクション失敗時にキャッシュを無効化してはならない（中途半端な状態が見える原因）");
        failingContext.BeginTransactionCallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SaveAppSettingsAsync_WhenTransactionStartFails_DoesNotPersistAnySettings()
    {
        // Arrange: 既存値を記録
        using var failingContext = new FailingTransactionDbContext();
        failingContext.InitializeDatabase();

        var cacheServiceMock = new Mock<ICacheService>();
        var repository = new SettingsRepository(failingContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));

        // 既存値（InitializeDatabase で設定されるデフォルト）を確認
        var beforeWarningBalance = await repository.GetAsync(SettingsRepository.KeyWarningBalance);
        var beforeFontSize = await repository.GetAsync(SettingsRepository.KeyFontSize);

        var settings = CreateValidSettings(FontSizeOption.ExtraLarge);
        settings.WarningBalance = 99999;  // デフォルト(10000)と異なる値

        // Act
        Func<Task> act = async () => await repository.SaveAppSettingsAsync(settings);

        // Assert: 例外発生後、DB上の値は変化していない（ロールバック挙動）
        await act.Should().ThrowAsync<InvalidOperationException>();

        var afterWarningBalance = await repository.GetAsync(SettingsRepository.KeyWarningBalance);
        var afterFontSize = await repository.GetAsync(SettingsRepository.KeyFontSize);

        afterWarningBalance.Should().Be(
            beforeWarningBalance,
            "トランザクション開始失敗時は一切の書き込みが反映されてはならない");
        afterFontSize.Should().Be(
            beforeFontSize,
            "トランザクション開始失敗時は一切の書き込みが反映されてはならない");
    }

    #endregion

    #region キャッシュ無効化対象のキー検証

    [Fact]
    public async Task SaveAppSettingsAsync_Success_InvalidatesCorrectCacheKey()
    {
        // Arrange
        var settings = CreateValidSettings();

        // Act
        await _repository.SaveAppSettingsAsync(settings);

        // Assert: 無効化される対象は CacheKeys.AppSettings に限定される
        //（他のキャッシュキーを巻き添えで無効化していないことを保証）
        _cacheServiceMock.Verify(c => c.Invalidate(CacheKeys.AppSettings), Times.Once);
        _cacheServiceMock.Verify(
            c => c.Invalidate(It.Is<string>(s => s != CacheKeys.AppSettings)),
            Times.Never,
            "SaveAppSettingsAsync は AppSettings キャッシュのみを無効化すべき");
    }

    #endregion

    #region 再実行でも他セッション値が上書きされる（Idempotency）

    [Fact]
    public async Task SaveAppSettingsAsync_CalledTwice_SecondCallOverwritesFirst()
    {
        // Arrange
        var first = CreateValidSettings(FontSizeOption.Small);
        first.WarningBalance = 1000;

        var second = CreateValidSettings(FontSizeOption.ExtraLarge);
        second.WarningBalance = 9000;

        // Act
        await _repository.SaveAppSettingsAsync(first);
        await _repository.SaveAppSettingsAsync(second);

        // Assert
        var loaded = await _repository.GetAppSettingsAsync();
        loaded.WarningBalance.Should().Be(9000);
        loaded.FontSize.Should().Be(FontSizeOption.ExtraLarge);

        // キャッシュ無効化は 2 回（各 Save につき 1 回）
        _cacheServiceMock.Verify(
            c => c.Invalidate(CacheKeys.AppSettings),
            Times.Exactly(2));
    }

    #endregion
}
