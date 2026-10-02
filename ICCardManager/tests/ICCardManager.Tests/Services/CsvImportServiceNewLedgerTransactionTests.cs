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
using ICCardManager.Infrastructure.Security;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Services.Import.Builders;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #2176: 利用履歴詳細（明細）CSV の取り込みで、利用履歴 ID が空欄の行から利用履歴を新しく作るとき、
/// 1 グループ（カード IDm＋日付）ぶんの「台帳の行の INSERT＋明細の INSERT」が
/// 1 つのトランザクションで確定することのテスト。
/// </summary>
/// <remarks>
/// <para>
/// 旧実装はどちらも autocommit で、明細の INSERT が失敗すると<b>明細を持たない台帳の行だけ</b>が残った。
/// チャージ境界で 1 日が複数のセグメントに分かれると、前のセグメントは確定したまま後のセグメントで失敗し得た。
/// </para>
/// <para>
/// 巻き戻ったかは「モックが呼ばれたか」では観測できないため、実 <see cref="LedgerRepository"/> へ
/// 委譲しつつ明細の INSERT にだけ失敗を注入し、<b>DB に実際に残った台帳の行と明細</b>を数えて表明する
/// （<see cref="CsvImportServiceLedgerDetailTransactionTests"/>（#2155）と同じ手法）。
/// tx なしオーバーロードも実リポジトリへ委譲し、失敗も両方へ注入する — そうしないと tx なし経路
/// （＝旧実装の形）へ退行したとき失敗が注入されずに成功し、巻き戻りの表明が意味を失う（testing.md #1745）。
/// </para>
/// </remarks>
public class CsvImportServiceNewLedgerTransactionTests : IDisposable
{
    private const string TestCardIdm = "0123456789ABCDEF";

    /// <summary>UTF-8 with BOM（<c>ReadCsvFileAsync</c> の文字コード判別を通すため）。</summary>
    private static readonly Encoding CsvEncoding = new UTF8Encoding(true);

    private const string Header =
        "利用履歴ID,利用日時,カードIDm,管理番号,乗車駅,降車駅,バス停,金額,残額,チャージ,ポイント還元,バス利用,グループID";

    /// <summary>
    /// 2024/01/15: 利用 → チャージ → 利用。チャージ境界で 3 つのセグメント（＝台帳の行 3 つ）に分かれる。
    /// </summary>
    private static readonly string[] ThreeSegmentDayLines =
    {
        $",2024-01-15 08:00:00,{TestCardIdm},001,博多,天神,,260,740,0,0,0,",
        $",2024-01-15 09:00:00,{TestCardIdm},001,,,,-3000,3740,1,0,0,",
        $",2024-01-15 10:00:00,{TestCardIdm},001,天神,博多,,260,3480,0,0,0,",
    };

    /// <summary>2024/01/16: 利用 1 件（セグメント 1 つ）。</summary>
    private static readonly string NextDayLine =
        $",2024-01-16 08:00:00,{TestCardIdm},001,博多,中洲川端,,210,3270,0,0,0,";

    private static readonly DateTime FirstDay = new DateTime(2024, 1, 15);

    private readonly string _testDirectory;
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _realLedgerRepository;

    public CsvImportServiceNewLedgerTransactionTests()
    {
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CsvImportNewLedgerTx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);

        _dbContext = TestDbContextFactory.Create();
        _realLedgerRepository = new LedgerRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region ヘルパー

    /// <summary>
    /// 明細の INSERT の差し替え。実際の INSERT を行ったあとで呼ばれ、戻り値を差し替える（例外も投げられる）。
    /// 引数は（明細、実際の INSERT の結果）。
    /// </summary>
    private delegate bool DetailInsertBehavior(IReadOnlyList<LedgerDetail> details, bool inserted);

    private sealed class Recorded
    {
        public List<SQLiteTransaction> LedgerInsertTransactions { get; } = new();
        public List<SQLiteTransaction> DetailInsertTransactions { get; } = new();
    }

    /// <summary>
    /// 実 <see cref="LedgerRepository"/> に委譲する <see cref="CsvImportService"/> を組み立てる。
    /// </summary>
    private (CsvImportService Service, Mock<ILedgerRepository> LedgerRepositoryMock, Recorded Recorded) CreateService(
        DetailInsertBehavior? detailInsertBehavior = null)
    {
        var recorded = new Recorded();
        var ledgerRepositoryMock = new Mock<ILedgerRepository>();

        ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()))
            .Returns<Ledger, SQLiteTransaction>((ledger, transaction) =>
            {
                recorded.LedgerInsertTransactions.Add(transaction);
                return _realLedgerRepository.InsertAsync(ledger, transaction);
            });
        ledgerRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Returns<Ledger>(ledger => _realLedgerRepository.InsertAsync(ledger));

        async Task<bool> RunDetailInsert(int id, IEnumerable<LedgerDetail> details, SQLiteTransaction? transaction)
        {
            var list = details.ToList();
            var inserted = transaction != null
                ? await _realLedgerRepository.InsertDetailsAsync(id, list, transaction)
                : await _realLedgerRepository.InsertDetailsAsync(id, list);
            // 差し替えは実際の INSERT を行ったうえで戻り値だけを変える（INSERT が DB に届いていなければ
            // 巻き戻ったかを観測できない。testing.md「ロールバックは書き込みが残らないことで観測する」）
            return detailInsertBehavior != null ? detailInsertBehavior(list, inserted) : inserted;
        }

        ledgerRepositoryMock
            .Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>(), It.IsAny<SQLiteTransaction>()))
            .Returns<int, IEnumerable<LedgerDetail>, SQLiteTransaction>((id, details, transaction) =>
            {
                recorded.DetailInsertTransactions.Add(transaction);
                return RunDetailInsert(id, details, transaction);
            });
        ledgerRepositoryMock
            .Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Returns<int, IEnumerable<LedgerDetail>>((id, details) => RunDetailInsert(id, details, null));

        var cardRepositoryMock = new Mock<ICardRepository>();
        cardRepositoryMock.Setup(x => x.GetByIdmAsync(TestCardIdm, true))
            .ReturnsAsync(new IcCard { CardIdm = TestCardIdm, CardType = "はやかけん", CardNumber = "001" });

        var settingsRepositoryMock = new Mock<ISettingsRepository>();
        settingsRepositoryMock.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        var service = new CsvImportService(
            cardRepositoryMock.Object,
            new Mock<IStaffRepository>().Object,
            ledgerRepositoryMock.Object,
            new Mock<IValidationService>().Object,
            _dbContext,
            new Mock<ICacheService>().Object,
            settingsRepositoryMock.Object,
            NullLogger<CsvImportService>.Instance);

        return (service, ledgerRepositoryMock, recorded);
    }

    /// <summary>台帳の外部キー（ic_card）を満たすカードを投入する。</summary>
    private async Task SeedCardAsync()
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText =
            "INSERT OR IGNORE INTO ic_card (card_idm, card_type, card_number) VALUES (@idm, 'はやかけん', '001')";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>指定日の台帳の行と、それらに属する明細の件数を DB から数える。</summary>
    private async Task<(int Ledgers, int Details)> CountAsync(DateTime day)
    {
        using var lease = await _dbContext.LeaseConnectionAsync();
        using var command = lease.Connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT COUNT(*) FROM ledger WHERE card_idm = @idm AND date >= @from AND date < @to), " +
            "(SELECT COUNT(*) FROM ledger_detail d JOIN ledger l ON l.id = d.ledger_id " +
            " WHERE l.card_idm = @idm AND l.date >= @from AND l.date < @to)";
        command.Parameters.AddWithValue("@idm", TestCardIdm);
        command.Parameters.AddWithValue("@from", day.ToString("yyyy-MM-dd 00:00:00", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@to", day.AddDays(1).ToString("yyyy-MM-dd 00:00:00", System.Globalization.CultureInfo.InvariantCulture));
        using var reader = await command.ExecuteReaderAsync();
        reader.Read().Should().BeTrue();
        return (Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)));
    }

    private string WriteCsv(params string[] lines)
    {
        var path = Path.Combine(_testDirectory, $"details_{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, Header + "\n" + string.Join("\n", lines) + "\n", CsvEncoding);
        return path;
    }

    private static bool IsOn(IReadOnlyList<LedgerDetail> details, DateTime day) =>
        details.Any(d => d.UseDate?.Date == day.Date);

    #endregion

    /// <summary>
    /// 欠陥を突く側: 明細の INSERT が例外になったら、その日の台帳の行も残らないこと。
    /// </summary>
    /// <remarks>旧実装では明細を持たない台帳の行（1 件目のセグメント）が確定して残った。</remarks>
    [Fact]
    public async Task 明細のINSERTが例外_その日の台帳の行も残らないこと()
    {
        // Arrange
        await SeedCardAsync();
        var csvPath = WriteCsv(NextDayLine);
        var (service, ledgerRepositoryMock, _) = CreateService(
            (_, __) => throw new InvalidOperationException("injected detail insert failure"));

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Once,
            "前提: 台帳の行を INSERT したあとで明細の INSERT が失敗していること");
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.LineNumber.Should().Be(2);
        (await CountAsync(FirstDay.AddDays(1))).Should().Be((0, 0),
            "台帳の行と明細は 1 つのトランザクションで巻き戻る（Issue #2176）");
    }

    /// <summary>
    /// 欠陥を突く側: 明細の INSERT が 0 行（<c>false</c>）でも、台帳の行を巻き戻し、何も登録していないと案内すること。
    /// </summary>
    [Fact]
    public async Task 明細のINSERTが0行_台帳の行も残らず日付を名指しして案内すること()
    {
        // Arrange
        await SeedCardAsync();
        var csvPath = WriteCsv(NextDayLine);
        var (service, _, _) = CreateService((_, __) => false);

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        result.Errors.Should().ContainSingle().Which.Message.Should().Be(
            NewLedgerFromSegmentsBuilder.BuildNotRegisteredMessage(IdmMasker.Mask(TestCardIdm), FirstDay.AddDays(1), reason: null));
        (await CountAsync(FirstDay.AddDays(1))).Should().Be((0, 0),
            "「台帳の行だけが残る」状態を作らない（#1986 の文言が前提にしていた状態。Issue #2176）");
    }

    /// <summary>
    /// 欠陥を突く側: 複数のセグメントに分かれる日で後のセグメントの明細が失敗したら、
    /// 前のセグメントも含めてその日の行はすべて巻き戻ること（単位はセグメントではなくグループ）。
    /// </summary>
    [Fact]
    public async Task 複数セグメントの日_後のセグメントの明細が失敗するとその日の行はすべて巻き戻ること()
    {
        // Arrange: 3 つ目のセグメント（10:00 の利用）の明細だけを失敗させる
        await SeedCardAsync();
        var csvPath = WriteCsv(ThreeSegmentDayLines);
        var (service, ledgerRepositoryMock, _) = CreateService(
            (details, inserted) => details.Any(d => d.UseDate?.Hour == 10) ? false : inserted);

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>(), It.IsAny<SQLiteTransaction>()), Times.Exactly(3),
            "前提: チャージ境界で 3 つのセグメントに分かれ、前の 2 つは INSERT まで進んでいること");
        result.Success.Should().BeFalse();
        result.ImportedCount.Should().Be(0);
        (await CountAsync(FirstDay)).Should().Be((0, 0),
            "1 日の一部のセグメントだけが入った状態を作らない（取り込み直すと二重になる）");
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 1 つの日の失敗は、ほかの日（グループ）を巻き戻さないこと。
    /// </summary>
    [Fact]
    public async Task 一つの日の失敗は他の日の取り込みを巻き戻さないこと()
    {
        // Arrange: 1/15 の明細だけを失敗させる
        await SeedCardAsync();
        var csvPath = WriteCsv(ThreeSegmentDayLines.Concat(new[] { NextDayLine }).ToArray());
        var (service, _, _) = CreateService(
            (details, inserted) => IsOn(details, FirstDay) ? throw new InvalidOperationException("injected") : inserted);

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Errors.Should().ContainSingle("失敗したのは 1/15 のグループだけ").Which.LineNumber.Should().Be(2);
        result.ImportedCount.Should().Be(1, "1/16 の 1 行は取り込まれる");
        (await CountAsync(FirstDay)).Should().Be((0, 0));
        (await CountAsync(FirstDay.AddDays(1))).Should().Be((1, 1));
    }

    /// <summary>
    /// 正当な挙動を塞いでいない側: 通常の取り込みでは、台帳の行と明細がともに確定し、
    /// 書き込みは同じグループの同じトランザクションを通ること。
    /// </summary>
    /// <remarks>
    /// 対の表明。これが無いと「常に巻き戻す」実装でも上の表明は緑になる。
    /// tx は非 null かつ同一参照であることを見る — null を渡しても DB 側の暗黙参加で原子性が保たれてしまい、
    /// null のまま渡す退行を検出できないため（testing.md #2103）。
    /// </remarks>
    [Fact]
    public async Task 通常の取り込み_台帳の行と明細がともに確定し同じトランザクションを通ること()
    {
        // Arrange
        await SeedCardAsync();
        var csvPath = WriteCsv(ThreeSegmentDayLines);
        var (service, ledgerRepositoryMock, recorded) = CreateService();

        // Act
        var result = await service.ImportLedgerDetailsAsync(csvPath);

        // Assert
        result.Success.Should().BeTrue(string.Join(" / ", result.Errors.Select(e => e.Message)));
        result.ImportedCount.Should().Be(3);
        (await CountAsync(FirstDay)).Should().Be((3, 3), "3 つのセグメントそれぞれの台帳の行と明細が確定する");

        var transactions = recorded.LedgerInsertTransactions.Concat(recorded.DetailInsertTransactions).ToList();
        transactions.Should().HaveCount(6);
        transactions.Should().OnlyContain(t => t != null, "tx を明示的に渡す（db-write-conventions.md の「①」）");
        transactions.Distinct().Should().ContainSingle("1 日ぶんの書き込みは 1 つのトランザクションで確定する");

        ledgerRepositoryMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never,
            "tx なし（autocommit）の INSERT を使わない");
        ledgerRepositoryMock.Verify(
            r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()), Times.Never,
            "tx なし（autocommit）の INSERT を使わない");
    }
}
