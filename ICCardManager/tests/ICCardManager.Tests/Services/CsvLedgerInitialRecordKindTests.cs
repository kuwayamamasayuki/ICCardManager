#nullable enable
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Services.Import;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2371: 利用履歴 CSV の取り込みでの導入行の種別（<c>ledger.initial_record_kind</c>）の扱い。
/// </summary>
/// <remarks>
/// <para>
/// 利用履歴 CSV は種別の列を持たない（#2307「フラグを運べない形式」）。導入行を出力から除くことはできない（台帳の行そのもの）ので、
/// 取り込む側で扱いを決める: 既存の行の更新では DB の種別を引き継ぎ（摘要を書き換えても導入行のまま）、
/// ID の無い新しい行は、摘要が導入行の文言でも導入行にしない（導入行はカード登録でだけ作る。手入力の行は履歴の「追加」も
/// CSV も導入行にしない — ユーザー判断）。ID が台帳に無い行は #2324 で拒否されるので、別の DB へ導入行を移す経路は無い。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class CsvLedgerInitialRecordKindTests : IDisposable
{
    private const string CardIdm = "0123456789ABCDEF";
    private const string CsvHeader = "ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考,同行者数";

    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private readonly string _testDirectory;
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<DbContext> _dbContextMock = new();
    private readonly SQLiteConnection _connection;
    private readonly CsvImportService _importService;
    private readonly List<Ledger> _inserted = new();
    private readonly List<Ledger> _updated = new();

    public CsvLedgerInitialRecordKindTests()
    {
        SummaryGenerator.ResetToDefaults();

        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvLedgerInitialRecordKindTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(new List<IcCard> { new IcCard { CardIdm = CardIdm, CardType = "はやかけん", CardNumber = "001" } });
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction?>()))
            .ReturnsAsync((Ledger l, SQLiteTransaction? _) =>
            {
                _inserted.Add(l);
                return 100 + _inserted.Count;
            });
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction?>()))
            .ReturnsAsync((Ledger l, SQLiteTransaction? _) =>
            {
                _updated.Add(l);
                return true;
            });

        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(() => new TransactionScope(new ConnectionLease(_connection, () => { }), _connection.BeginTransaction()));

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _importService = new CsvImportService(
            _cardRepositoryMock.Object,
            new Mock<IStaffRepository>().Object,
            _ledgerRepositoryMock.Object,
            new Mock<IValidationService>().Object,
            _dbContextMock.Object,
            new Mock<ICacheService>().Object,
            settingsRepositoryMock.Object,
            new OperationLogger(new Mock<IOperationLogRepository>().Object, Mock.Of<ICurrentOperatorContext>()),
            NullLogger<CsvImportService>.Instance);
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 一時ディレクトリの削除失敗はテスト結果に影響しない
        }

        GC.SuppressFinalize(this);
    }

    private async Task<string> WriteCsvAsync(string name, params string[] rows)
    {
        var path = Path.Combine(_testDirectory, name);
        await Task.Run(() => File.WriteAllText(path, string.Join("\r\n", new[] { CsvHeader }.Concat(rows)), CsvEncoding));
        return path;
    }

    /// <summary>
    /// ID の無い新しい行は、摘要が導入行の文言でも導入行にしない（種別を持たせない）。
    /// </summary>
    [Fact]
    public async Task 新しい行は摘要が導入行の文言でも導入行にしないこと()
    {
        var path = await WriteCsvAsync("new_rows.csv",
            ",2026-03-01 00:00:00,0123456789ABCDEF,001,2月から繰越,,,5000,,,",
            ",2026-03-05 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,210,4790,山田太郎,,");

        var result = await _importService.ImportLedgersAsync(path);

        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        _inserted.Should().HaveCount(2);
        _inserted.Should().OnlyContain(l => l.InitialRecordKind == null, "導入行はカード登録でだけ作る");
    }

    /// <summary>
    /// 文言を変えた組織でも、変更前の文言（既定）・変更後の文言（現在）のどちらの新しい行も導入行にしない。
    /// </summary>
    [Fact]
    public async Task 文言を変えた後も既定の文言と現在の文言の新しい行を導入行にしないこと()
    {
        var options = new OrganizationOptions();
        options.SummaryText.CarryoverFromPreviousYear = "前年より持越";
        SummaryGenerator.Configure(options);
        var path = await WriteCsvAsync("changed_wording.csv",
            ",2026-04-01 00:00:00,0123456789ABCDEF,001,前年度より繰越,5000,,5000,,,",
            ",2027-04-01 00:00:00,0123456789ABCDEF,001,前年より持越,,,5000,,,");

        var result = await _importService.ImportLedgersAsync(path);

        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        _inserted.Should().HaveCount(2).And.OnlyContain(l => l.InitialRecordKind == null);
    }

    /// <summary>
    /// 既存の行の更新では DB の種別を引き継ぐ。摘要を導入行でない文言へ書き換えても導入行のまま、
    /// 導入行の文言へ書き換えた通常の行は導入行にならない（種別は登録時に決まる）。
    /// </summary>
    [Theory]
    [InlineData(InitialRecordKind.MidYearCarryover, "移行時の繰越")]
    [InlineData(null, "3月から繰越")]
    public async Task 既存の行の更新ではDBの種別を引き継ぐこと(InitialRecordKind? existingKind, string newSummary)
    {
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(new Ledger
        {
            Id = 1,
            CardIdm = CardIdm,
            Date = new DateTime(2026, 4, 1),
            Summary = existingKind.HasValue ? "3月から繰越" : "鉄道（博多～天神）",
            Balance = 5000,
            InitialRecordKind = existingKind,
        });
        var path = await WriteCsvAsync("update.csv",
            $"1,2026-04-01 00:00:00,0123456789ABCDEF,001,{newSummary},,,5000,,,");

        var result = await _importService.ImportLedgersAsync(path);

        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        _inserted.Should().BeEmpty();
        _updated.Should().ContainSingle().Which.InitialRecordKind.Should().Be(existingKind);
    }
}
