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
/// Issue #2307: 貸出中レコードが利用履歴 CSV を往復して、通常の行として台帳へ入らないことを固定する。
/// </summary>
/// <remarks>
/// <para>
/// 修正前は、貸出中に出力した CSV を返却後（貸出中レコードは物理削除済み）に取り込むと、ID の行が DB に無いので
/// 新規として追加され、<c>is_lent_record = 0</c> の「（貸出中）」・0円／0円の行が物品出納簿へ印字された。
/// 出力でフラグにより除く側と、取り込み（プレビュー・取り込みの両方）で拒否する側の二重化を、
/// 「欠陥を突く側」と「正当な往復を塞いでいない側」の対で表明する。
/// </para>
/// <para>
/// 組織設定（貸出中の摘要）を変えるテストがあるため <see cref="SummaryGeneratorCollection"/> に属させ、Dispose で既定値へ戻す。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class CsvLedgerLentRecordRoundTripTests : IDisposable
{
    private const string CardIdm = "0123456789ABCDEF";
    private const string CsvHeader = "ID,日時,カードIDm,管理番号,摘要,受入金額,払出金額,残額,利用者,備考,同行者数";

    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private readonly string _testDirectory;
    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly Mock<DbContext> _dbContextMock = new();
    private readonly SQLiteConnection _connection;
    private readonly CsvExportService _exportService;
    private readonly CsvImportService _importService;
    private readonly List<Ledger> _inserted = new();

    public CsvLedgerLentRecordRoundTripTests()
    {
        SummaryGenerator.ResetToDefaults();

        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvLedgerLentRecordRoundTripTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _cardRepositoryMock.Setup(x => x.GetAllIncludingDeletedAsync())
            .ReturnsAsync(new List<IcCard> { new IcCard { CardIdm = CardIdm, CardType = "はやかけん", CardNumber = "001" } });
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>());
        _ledgerRepositoryMock.Setup(x => x.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction?>()))
            .ReturnsAsync((Ledger l, SQLiteTransaction? _) =>
            {
                _inserted.Add(l);
                return 100 + _inserted.Count;
            });

        _connection = new SQLiteConnection("Data Source=:memory:");
        _connection.Open();
        _dbContextMock.Setup(x => x.BeginTransactionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(() => new TransactionScope(new ConnectionLease(_connection, () => { }), _connection.BeginTransaction()));

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        _exportService = new CsvExportService(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            NullLogger<CsvExportService>.Instance);

        _importService = new CsvImportService(
            _cardRepositoryMock.Object,
            _staffRepositoryMock.Object,
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

    private static Ledger Usage(int id, DateTime date, int expense, int balance) => new()
    {
        Id = id,
        CardIdm = CardIdm,
        Date = date,
        Summary = "鉄道（博多～天神）",
        Expense = expense,
        Balance = balance,
        StaffName = "山田太郎"
    };

    private static Ledger LentPlaceholder(int id, DateTime date, int balance, string summary = "（貸出中）") => new()
    {
        Id = id,
        CardIdm = CardIdm,
        Date = date,
        Summary = summary,
        Balance = balance,
        StaffName = "山田太郎",
        LenderIdm = "STAFF00000000001",
        LentAt = date,
        IsLentRecord = true
    };

    private async Task<string> WriteCsvAsync(string name, params string[] rows)
    {
        var path = Path.Combine(_testDirectory, name);
        await Task.Run(() => File.WriteAllText(path, string.Join("\r\n", new[] { CsvHeader }.Concat(rows)), CsvEncoding));
        return path;
    }

    // ===== 出力 =====

    [Fact]
    public async Task ExportLedgersAsync_貸出中レコードは出力せず通常の行だけを出力すること()
    {
        var ledgers = new List<Ledger>
        {
            Usage(1, new DateTime(2026, 10, 1), 260, 9740),
            LentPlaceholder(2, new DateTime(2026, 10, 2), 9740),
        };
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        var path = Path.Combine(_testDirectory, "export.csv");

        var result = await _exportService.ExportLedgersAsync(path, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

        result.Success.Should().BeTrue();
        result.ExportedCount.Should().Be(1, "出力件数も、実際に書いた行の数（貸出中レコードを除いた数）");
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        lines.Should().Equal(
            CsvHeader,
            "1,2026-10-01 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,山田太郎,,");
    }

    /// <summary>
    /// 除外はフラグで行う（#2044）。摘要が現在の設定と違う貸出中レコード（設定を変える前に貸し出した）も除き、
    /// 摘要が「（貸出中）」でもフラグの立っていない行は除かない（フラグではなく摘要で判定する実装を赤にする）。
    /// </summary>
    [Fact]
    public async Task ExportLedgersAsync_除外は摘要ではなく貸出中フラグで判定すること()
    {
        var ledgers = new List<Ledger>
        {
            LentPlaceholder(1, new DateTime(2026, 10, 1), 10000, summary: "（旧文言の貸出中）"),
            new Ledger { Id = 2, CardIdm = CardIdm, Date = new DateTime(2026, 10, 2), Summary = "（貸出中）", Balance = 10000, IsLentRecord = false },
        };
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(ledgers);
        var path = Path.Combine(_testDirectory, "export_flag.csv");

        var result = await _exportService.ExportLedgersAsync(path, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));

        result.Success.Should().BeTrue();
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        lines.Should().HaveCount(2);
        lines[1].Should().StartWith("2,");
    }

    // ===== 往復（受け入れ条件） =====

    /// <summary>
    /// 受け入れ条件: 貸出中に出力した CSV を返却後（貸出中レコードは削除済み）に取り込んでも、「（貸出中）」の行が台帳に入らない。
    /// 対: 同じ CSV の通常の行は従来どおり取り込まれる。
    /// </summary>
    [Fact]
    public async Task 貸出中に出力したCSVを返却後に取り込んでも通常の行だけが追加されること()
    {
        _ledgerRepositoryMock.Setup(x => x.GetByDateRangeAsync(null, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(new List<Ledger>
            {
                Usage(1, new DateTime(2026, 10, 1), 260, 9740),
                LentPlaceholder(2, new DateTime(2026, 10, 2), 9740),
            });
        var path = Path.Combine(_testDirectory, "roundtrip.csv");
        (await _exportService.ExportLedgersAsync(path, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31))).Success.Should().BeTrue();

        // 返却後の DB: 通常の行は台帳に残り、貸出中レコードは返却で削除されて無い。
        // Issue #2324: 以前は「どちらの ID の行も無い（通常の行は別の DB へ取り込む想定）」として通常の行が追加されることを
        // 表明していたが、ID が台帳に無い行は削除・統合した行として拒否するようになったため、実際の返却後の DB の形にした
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(Usage(1, new DateTime(2026, 10, 1), 260, 9740));

        var preview = await _importService.PreviewLedgersAsync(path);
        var result = await _importService.ImportLedgersAsync(path);

        preview.IsValid.Should().BeTrue();
        preview.Items.Should().ContainSingle("貸出中の行は CSV に出ない").Which.Name.Should().Be("鉄道（博多～天神）");
        preview.SkipCount.Should().Be(1, "通常の行は台帳の行と全項目一致");
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(1);
        _inserted.Should().BeEmpty("「（貸出中）」の行は台帳に入らない");
    }

    // ===== 取り込みでの拒否（修正前に出力した CSV・手で作った CSV） =====

    [Fact]
    public async Task ImportLedgersAsync_返却済みの貸出中レコードを写した行は取り込まずエラーにすること()
    {
        var path = await WriteCsvAsync("lent_import.csv",
            "1,2026-10-01 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,山田太郎,,",
            "2,2026-10-02 00:00:00,0123456789ABCDEF,001,（貸出中）,,,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Ledger?)null);
        // Issue #2324: 通常の行（ID 1）は台帳に残っている（ID が台帳に無い行は別の理由で拒否されるため）
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(Usage(1, new DateTime(2026, 10, 1), 260, 9740));

        var result = await _importService.ImportLedgersAsync(path);

        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        _inserted.Should().BeEmpty("貸出中の行は台帳に入れない");
        var error = result.Errors.Should().ContainSingle().Subject;
        error.LineNumber.Should().Be(3);
        error.Message.Should().Contain("「（貸出中）」").And.Contain("取り込めません").And.EndWith("ください。");
    }

    [Fact]
    public async Task PreviewLedgersAsync_返却済みの貸出中レコードを写した行をエラーとして示すこと()
    {
        var path = await WriteCsvAsync("lent_preview.csv",
            "1,2026-10-01 00:00:00,0123456789ABCDEF,001,鉄道（博多～天神）,,260,9740,山田太郎,,",
            "2,2026-10-02 00:00:00,0123456789ABCDEF,001,（貸出中）,,,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Ledger?)null);
        // Issue #2324: 通常の行（ID 1）は台帳に残っている（ID が台帳に無い行は別の理由で拒否されるため）
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(Usage(1, new DateTime(2026, 10, 1), 260, 9740));

        var preview = await _importService.PreviewLedgersAsync(path);

        preview.IsValid.Should().BeFalse();
        preview.Errors.Should().ContainSingle().Which.LineNumber.Should().Be(3);
        preview.Items.Should().ContainSingle("貸出中の行はプレビューの一覧に並べない").Which.Name.Should().Be("鉄道（博多～天神）");
    }

    [Fact]
    public async Task ImportLedgersAsync_ID列が空欄の貸出中の行も取り込まないこと()
    {
        var path = await WriteCsvAsync("lent_no_id.csv",
            ",2026-10-02 00:00:00,0123456789ABCDEF,001,（貸出中）,,,9740,山田太郎,,");

        var result = await _importService.ImportLedgersAsync(path);

        result.Success.Should().BeFalse();
        _inserted.Should().BeEmpty();
    }

    /// <summary>
    /// 設定（貸出中の摘要）を変えた後でも、現在の文言の行と、変える前の既定の文言で出力した CSV の行の両方を拒否する。
    /// </summary>
    [Theory]
    [InlineData("貸出中（未返却）")]
    [InlineData("（貸出中）")]
    public async Task ImportLedgersAsync_設定を変えた後も現在と既定の両方の文言の貸出中の行を拒否すること(string summaryInCsv)
    {
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryText = new SummaryTextOptions { LendingSummary = "貸出中（未返却）" }
        });
        var path = await WriteCsvAsync("lent_custom.csv",
            $"2,2026-10-02 00:00:00,0123456789ABCDEF,001,{summaryInCsv},,,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Ledger?)null);

        var preview = await _importService.PreviewLedgersAsync(path);
        var result = await _importService.ImportLedgersAsync(path);

        preview.IsValid.Should().BeFalse();
        result.Success.Should().BeFalse();
        _inserted.Should().BeEmpty();
    }

    /// <summary>
    /// 対: ID が指す行がまだ貸出中レコード（返却前）なら、その行のスキップとして扱い、拒否しない。
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_返却前の貸出中レコードそのものを指す同じ内容の行はスキップすること()
    {
        var path = await WriteCsvAsync("lent_still_lent.csv",
            "2,2026-10-02 00:00:00,0123456789ABCDEF,001,（貸出中）,,,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(2)).ReturnsAsync(LentPlaceholder(2, new DateTime(2026, 10, 2), 9740));

        var preview = await _importService.PreviewLedgersAsync(path);
        var result = await _importService.ImportLedgersAsync(path);

        preview.IsValid.Should().BeTrue();
        preview.SkipCount.Should().Be(1);
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(1);
        _inserted.Should().BeEmpty();
    }

    // ===== 判定（LedgerCsvLentRowPolicy） =====

    [Theory]
    [InlineData("（貸出中）", true)]
    [InlineData(" （貸出中） ", true)]
    [InlineData("鉄道（博多～天神）", false)]
    [InlineData("貸出中", false)]
    // 一部に含むだけの摘要は貸出中の行ではない（部分一致で判定する実装を赤にする）
    [InlineData("鉄道（博多～天神）（貸出中）", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BuildError_既定の設定で貸出中の摘要の行だけを拒否すること(string? summary, bool expected)
    {
        (LedgerCsvLentRowPolicy.BuildError(2, summary, existingLedger: null, leavesLedgerUnchanged: false) != null).Should().Be(expected);
    }

    /// <summary>
    /// 設定値の前後に空白があっても、CSV 側と同じく空白を除いて照合する（レビュー 2 回目で検出）。
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_設定値の前後に空白があっても貸出中の行を拒否すること()
    {
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryText = new SummaryTextOptions { LendingSummary = " 貸出中（未返却） " }
        });
        var path = await WriteCsvAsync("lent_spaced_setting.csv",
            "2,2026-10-02 00:00:00,0123456789ABCDEF,001,貸出中（未返却）,,,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Ledger?)null);

        var result = await _importService.ImportLedgersAsync(path);

        result.Success.Should().BeFalse();
        _inserted.Should().BeEmpty();
    }

    /// <summary>
    /// 対: 設定（貸出中の摘要）を変えた状態でも、通常の摘要の行は従来どおり取り込める。
    /// </summary>
    [Theory]
    [InlineData("鉄道（博多～天神）")]
    [InlineData("貸出中")]
    [InlineData("鉄道（博多～天神）（貸出中）")]
    public async Task ImportLedgersAsync_設定を変えた状態でも通常の摘要の行は取り込めること(string summaryInCsv)
    {
        SummaryGenerator.Configure(new OrganizationOptions
        {
            SummaryText = new SummaryTextOptions { LendingSummary = "貸出中（未返却）" }
        });
        var path = await WriteCsvAsync("normal_custom.csv",
            // Issue #2324: ID の無い行（新しい履歴）として取り込む。ID が台帳に無い行は削除・統合した行として拒否するため
            $",2026-10-01 00:00:00,0123456789ABCDEF,001,{summaryInCsv},,260,9740,山田太郎,,");

        var preview = await _importService.PreviewLedgersAsync(path);
        var result = await _importService.ImportLedgersAsync(path);

        preview.IsValid.Should().BeTrue();
        result.Success.Should().BeTrue();
        _inserted.Should().ContainSingle().Which.Summary.Should().Be(summaryInCsv);
    }

    [Fact]
    public void BuildError_貸出中でない既存の行を指す貸出中の摘要の行は拒否すること()
    {
        var normal = Usage(5, new DateTime(2026, 10, 1), 260, 9740);

        var error = LedgerCsvLentRowPolicy.BuildError(4, "（貸出中）", normal, leavesLedgerUnchanged: false);
        error.Should().NotBeNull("通常の行の摘要を「（貸出中）」へ書き換える取り込みも、貸出中ではない「（貸出中）」の行を作る");
        error!.Message.Should().Contain("利用履歴ID 5").And.Contain("元の摘要に戻す").And.EndWith("ください。");
        LedgerCsvLentRowPolicy.BuildError(4, "鉄道（博多～天神）", normal, leavesLedgerUnchanged: false).Should().BeNull();
    }

    // ===== 以前の取り込みで通常の行として混入した「（貸出中）」の行（レビュー 3 回目） =====

    /// <summary>以前のバージョンの取り込みで通常の行として入った「（貸出中）」の行（フラグなし・0円／0円）。</summary>
    private static Ledger ContaminatedRow(int id) => new()
    {
        Id = id,
        CardIdm = CardIdm,
        Date = new DateTime(2026, 10, 2),
        Summary = "（貸出中）",
        Balance = 9740,
        StaffName = "山田太郎",
        IsLentRecord = false
    };

    /// <summary>
    /// 混入した行は今の出力でも CSV に出る。全項目一致する行の往復（以前はスキップ）は止めない。
    /// スキップ指定を外した場合（無変更の更新）も同じ。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImportLedgersAsync_混入した貸出中の行と全項目一致する行は拒否せず往復できること(bool skipExisting)
    {
        var path = await WriteCsvAsync("contaminated_same.csv",
            "7,2026-10-02 00:00:00,0123456789ABCDEF,001,（貸出中）,,,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(7)).ReturnsAsync(() => ContaminatedRow(7));
        _ledgerRepositoryMock.Setup(x => x.UpdateAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction?>())).ReturnsAsync(true);

        var preview = await _importService.PreviewLedgersAsync(path, skipExisting);
        var result = await _importService.ImportLedgersAsync(path, skipExisting);

        preview.IsValid.Should().BeTrue();
        result.Success.Should().BeTrue();
        _inserted.Should().BeEmpty();
        if (skipExisting)
        {
            preview.SkipCount.Should().Be(1);
            result.SkippedCount.Should().Be(1);
        }
    }

    /// <summary>
    /// 対: 混入した行を指して内容が違う行は拒否し、混入した行は返却しても消えないので履歴画面から削除するよう案内する。
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_混入した貸出中の行を指して内容が違う行は履歴画面からの削除を案内して拒否すること()
    {
        var path = await WriteCsvAsync("contaminated_changed.csv",
            "7,2026-10-02 00:00:00,0123456789ABCDEF,001,（貸出中）,,,9740,山田太郎,備考を追加,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(7)).ReturnsAsync(() => ContaminatedRow(7));

        var preview = await _importService.PreviewLedgersAsync(path);
        var result = await _importService.ImportLedgersAsync(path);

        preview.IsValid.Should().BeFalse();
        result.Success.Should().BeFalse();
        var message = result.Errors.Should().ContainSingle().Subject.Message;
        message.Should().Contain("利用履歴ID 7").And.Contain("履歴画面からその行を削除").And.EndWith("ください。");
        message.Should().NotContain("返却すると削除されます", "混入した行は返却しても消えない");
        preview.Errors.Should().ContainSingle().Which.Message.Should().Be(message, "プレビューと取り込みで同じ文言");
    }

    /// <summary>
    /// ID の行が DB に無い（返却で削除された貸出中レコードを写した）行の文言は、CSV からの削除を案内する（混入の文言と取り違えない）。
    /// </summary>
    [Fact]
    public void BuildError_IDの行が無い貸出中の行はCSVからの削除を案内すること()
    {
        var error = LedgerCsvLentRowPolicy.BuildError(3, "（貸出中）", existingLedger: null, leavesLedgerUnchanged: false);

        error.Should().NotBeNull();
        error!.Message.Should().Contain("返却すると削除されます").And.Contain("CSVから削除").And.NotContain("履歴画面");
    }

    /// <summary>
    /// ID の無い行・ID の行が見つからない行でも、重複キーが既存の履歴（混入した「（貸出中）」の行）と一致してスキップになるなら、
    /// 台帳を変えないので拒否しない（レビュー 4 回目）。以前はスキップで成功していた取り込みを止めない。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("50")]
    public async Task ImportLedgersAsync_IDの無い貸出中の行も重複キーが既存の行と一致すればスキップすること(string idText)
    {
        var path = await WriteCsvAsync("lent_dup_key.csv",
            $"{idText},2026-10-02 00:00:00,0123456789ABCDEF,001,（貸出中）,,,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetByIdAsync(It.IsAny<int>())).ReturnsAsync((Ledger?)null);
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>
            {
                (CardIdm, new DateTime(2026, 10, 2), "（貸出中）", 0, 0, 9740)
            });

        var preview = await _importService.PreviewLedgersAsync(path);
        var result = await _importService.ImportLedgersAsync(path);

        preview.IsValid.Should().BeTrue();
        preview.SkipCount.Should().Be(1, "プレビューも取り込みと同じくスキップと表示する（ID の行が DB に無い行も）");
        preview.NewCount.Should().Be(0);
        result.Success.Should().BeTrue();
        result.SkippedCount.Should().Be(1);
        _inserted.Should().BeEmpty();
    }

    /// <summary>
    /// 対: スキップ指定を外すと重複キーの照合をせずに登録するので、同じ行でも拒否する（台帳を変える行）。
    /// </summary>
    [Fact]
    public async Task ImportLedgersAsync_スキップ指定を外すとIDの無い貸出中の行は重複キーが一致しても拒否すること()
    {
        var path = await WriteCsvAsync("lent_dup_key_noskip.csv",
            ",2026-10-02 00:00:00,0123456789ABCDEF,001,（貸出中）,,,9740,山田太郎,,");
        _ledgerRepositoryMock.Setup(x => x.GetExistingLedgerKeysAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new HashSet<(string, DateTime, string, int, int, int)>
            {
                (CardIdm, new DateTime(2026, 10, 2), "（貸出中）", 0, 0, 9740)
            });

        var preview = await _importService.PreviewLedgersAsync(path, skipExisting: false);
        var result = await _importService.ImportLedgersAsync(path, skipExisting: false);

        preview.IsValid.Should().BeFalse();
        result.Success.Should().BeFalse();
        _inserted.Should().BeEmpty();
    }
}
