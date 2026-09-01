using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// 失敗結果の <c>ErrorMessage</c> に生の <c>ex.Message</c> を出さず、技術的詳細はログへ残すこと
/// （Issue #1991 / #1614 / #1817）を挙動として表明する。
/// </summary>
/// <remarks>
/// <para>
/// 静的検査（<c>ImportErrorMessageExposureConventionTests</c>）は「その形が書かれていないこと」しか
/// 見ない。ここでは<b>実際に返る文言</b>と<b>実際に残るログ</b>を対で固定する。
/// </para>
/// <para>
/// <b>ログを対で見る理由。</b> 是正前の <c>CsvExportService</c> はロガーを持たず、
/// ユーザー向け <c>ErrorMessage</c> へ代入された生の <c>ex.Message</c> が
/// 技術的詳細の<b>唯一の出口</b>だった。文言だけ差し替えると、失敗の原因がどこにも残らない
/// （<c>error-messages.md</c> #1817）。ログの表明が無いと、その退行を検出できない。
/// </para>
/// </remarks>
public class RawExceptionMessageExposureTests : IDisposable
{
    /// <summary>
    /// SQLite の生メッセージ。共有モードで他 PC がロックを保持しているときに実際に出る英文で、
    /// 是正前はこれがそのままモーダルへ出ていた。
    /// </summary>
    private const string RawSqliteMessage = "database is locked";

    private readonly Mock<ICardRepository> _cardRepositoryMock = new();
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<ILedgerRepository> _ledgerRepositoryMock = new();
    private readonly RecordingLogger<CsvExportService> _logger = new();

    private CsvExportService CreateExportService() => new(
        _cardRepositoryMock.Object,
        _staffRepositoryMock.Object,
        _ledgerRepositoryMock.Object,
        _logger);

    private static string TempCsvPath() =>
        Path.Combine(Path.GetTempPath(), $"iccard_1991_{Guid.NewGuid():N}.csv");

    [Fact]
    public async Task エクスポート失敗時_生の例外メッセージをユーザーへ出さないこと()
    {
        _cardRepositoryMock
            .Setup(x => x.GetAllAsync())
            .ThrowsAsync(new SQLiteException(SQLiteErrorCode.Busy, RawSqliteMessage));

        var result = await CreateExportService().ExportCardsAsync(TempCsvPath());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotContain(RawSqliteMessage,
            "SQLite の英語メッセージは職員には解読不能で、内部実装の露出にもなる（#1614）");
        result.ErrorMessage.Should().Contain("カード一覧のエクスポート",
            "「何が」失敗したかを操作名で述べること");
        result.ErrorMessage.Should().EndWith("してください。",
            "行動指示で終わること（error-messages.md の 3 要素）");
    }

    /// <summary>
    /// 「UI 文言」と「ログ」を対で数える（#1817）。文言だけ直すと、失敗の原因がどこにも残らない。
    /// </summary>
    [Fact]
    public async Task エクスポート失敗時_技術的詳細をログへ残すこと()
    {
        var thrown = new SQLiteException(SQLiteErrorCode.Busy, RawSqliteMessage);
        _cardRepositoryMock.Setup(x => x.GetAllAsync()).ThrowsAsync(thrown);

        await CreateExportService().ExportCardsAsync(TempCsvPath());

        _logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, thrown),
            "捕捉した例外そのものを渡すこと（スタックトレースが残る）。実際: " + _logger.FormatEntries());
    }

    /// <summary>
    /// 対の表明。成功時に文言を出す実装（＝常に失敗扱いする実装）でも緑にならないようにする。
    /// </summary>
    [Fact]
    public async Task エクスポート成功時_エラー文言もログも出さないこと()
    {
        _cardRepositoryMock.Setup(x => x.GetAllAsync()).ReturnsAsync(new List<IcCard>());

        var path = TempCsvPath();
        try
        {
            var result = await CreateExportService().ExportCardsAsync(path);

            result.Success.Should().BeTrue();
            result.ErrorMessage.Should().BeNull();
            _logger.Entries.Where(e => e.Level == LogLevel.Error).Should().BeEmpty(
                "成功時にエラーログを出さないこと。実際: " + _logger.FormatEntries());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// SQLite の失敗は <see cref="ICCardManager.Common.ExceptionMessageFormatter"/> の
    /// <c>SQLiteException</c> 分岐（Issue #1986）で原因が名指しされること。
    /// 「予期しない問題」へ落ちていないことを表明する。
    /// </summary>
    [Fact]
    public async Task エクスポート失敗時_共有モードの競合を名指しできること()
    {
        _cardRepositoryMock
            .Setup(x => x.GetAllAsync())
            .ThrowsAsync(new SQLiteException(SQLiteErrorCode.Locked, RawSqliteMessage));

        var result = await CreateExportService().ExportCardsAsync(TempCsvPath());

        result.ErrorMessage.Should().Contain("データベース");
        result.ErrorMessage.Should().NotContain("予期しない問題",
            "原因を名指しできる例外を汎用分岐へ落とさないこと（#1986）");
    }

    /// <summary>
    /// ファイルが他プログラムに開かれている等の I/O 失敗（取れる行動が「閉じてから再実行」）。
    /// </summary>
    [Fact]
    public async Task エクスポート失敗時_IO例外でもユーザー向け文言になること()
    {
        _cardRepositoryMock
            .Setup(x => x.GetAllAsync())
            .ThrowsAsync(new IOException("The process cannot access the file"));

        var result = await CreateExportService().ExportCardsAsync(TempCsvPath());

        result.ErrorMessage.Should().NotContain("The process cannot access the file");
        result.ErrorMessage.Should().EndWith("してください。");
    }

    // ---- 取込側（CsvImportService.ToUserFacingErrorMessage の default 分岐） ----

    /// <summary>
    /// 取込の <c>default</c> 分岐（<see cref="ICCardManager.Common.Exceptions.AppException"/> でも
    /// ファイル系でもない例外）が、生の <c>ex.Message</c> を返さないこと。
    /// </summary>
    /// <remarks>
    /// 是正前は <c>$"予期しないエラーが発生しました: {ex.Message}"</c> を返しており、
    /// 共有モードのロック競合が「database is locked」という英文でモーダルへ出ていた。
    /// </remarks>
    [Fact]
    public async Task 取込失敗時_生の例外メッセージをユーザーへ出さないこと()
    {
        var (service, cardRepository, _) = CreateImportService();
        cardRepository
            .Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("Sequence contains no matching element"));

        var path = WriteCardCsv();
        try
        {
            var result = await service.ImportCardsAsync(path);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().NotContain("Sequence contains no matching element");
            result.ErrorMessage.Should().Contain("カードCSVの取り込み", "「何が」を経路ごとの操作名で述べること");
            result.ErrorMessage.Should().EndWith("してください。");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 「UI 文言」と「ログ」を対で数える（#1817）。この catch 群は是正前ログを出しておらず、
    /// 生の <c>ex.Message</c> が唯一の出口だった。
    /// </summary>
    [Fact]
    public async Task 取込失敗時_技術的詳細をログへ残すこと()
    {
        var (service, cardRepository, importLogger) = CreateImportService();
        var thrown = new InvalidOperationException("Sequence contains no matching element");
        cardRepository
            .Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(thrown);

        var path = WriteCardCsv();
        try
        {
            await service.ImportCardsAsync(path);

            importLogger.Entries.Should().Contain(
                e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, thrown),
                "捕捉した例外そのものを渡すこと。実際: " + importLogger.FormatEntries());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 対の表明。取り込みが成功する入力でエラー文言・エラーログを出さないこと
    /// （常に失敗扱いする実装でも緑にならないようにする）。
    /// </summary>
    [Fact]
    public async Task 取込成功時_エラー文言もエラーログも出さないこと()
    {
        var (service, cardRepository, importLogger) = CreateImportService();
        cardRepository
            .Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IcCard?)null);
        cardRepository
            .Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<System.Data.SQLite.SQLiteTransaction>()))
            .ReturnsAsync(true);

        var path = WriteCardCsv();
        try
        {
            var result = await service.ImportCardsAsync(path);

            result.ErrorMessage.Should().BeNullOrEmpty(
                "成功時に失敗文言を出さないこと。エラー: "
                + string.Join(" / ", result.Errors.Select(e => e.Message)));
            importLogger.Entries.Where(e => e.Level == LogLevel.Error).Should().BeEmpty(
                "成功時にエラーログを出さないこと。実際: " + importLogger.FormatEntries());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteCardCsv()
    {
        var path = TempCsvPath();
        File.WriteAllLines(path, new[]
        {
            "カードIDm,カード種別,管理番号,備考",
            "0123456789ABCDEF,はやかけん,No.1,"
        });
        return path;
    }

    private (CsvImportService Service, Mock<ICardRepository> CardRepository,
        RecordingLogger<CsvImportService> Logger) CreateImportService()
    {
        var cardRepository = new Mock<ICardRepository>();
        var validationService = new Mock<IValidationService>();
        validationService
            .Setup(x => x.ValidateCardIdm(It.IsAny<string>()))
            .Returns(ValidationResult.Success());

        var settingsRepository = new Mock<ISettingsRepository>();
        settingsRepository.Setup(x => x.GetAppSettingsAsync()).ReturnsAsync(new AppSettings());

        // セマフォを保持しない ConnectionLease / TransactionScope を使う
        // （テスト内で LeaseConnectionAsync が呼ばれてもデッドロックしないように）
        var connection = new SQLiteConnection("Data Source=:memory:");
        connection.Open();
        _disposables.Add(connection);
        var transaction = connection.BeginTransaction();
        _disposables.Add(transaction);
        var scope = new TransactionScope(
            new ConnectionLease(connection, () => { }), transaction);
        _disposables.Add(scope);

        var dbContext = new Mock<DbContext>();
        dbContext
            .Setup(x => x.BeginTransactionAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(scope);

        var logger = new RecordingLogger<CsvImportService>();

        var service = new CsvImportService(
            cardRepository.Object,
            _staffRepositoryMock.Object,
            _ledgerRepositoryMock.Object,
            validationService.Object,
            dbContext.Object,
            new Mock<ICacheService>().Object,
            settingsRepository.Object,
            logger);

        return (service, cardRepository, logger);
    }

    private readonly List<IDisposable> _disposables = new();

    /// <summary>
    /// **二重に記録しない**（#1817）。カード取込のトランザクション内 catch は
    /// 「ロールバックより先に」ログを書いてから再スローする（#1745）ため、
    /// その例外が共通ハンドラーへ届いた時点で痕跡は既に残っている。
    /// </summary>
    [Fact]
    public async Task 取込失敗時_内側で記録済みの例外を二重に記録しないこと()
    {
        var (service, cardRepository, importLogger) = CreateImportService();
        cardRepository
            .Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((IcCard?)null);
        // トランザクションの内側で失敗させる（内側 catch が LogError + 再スロー）
        cardRepository
            .Setup(x => x.InsertAsync(It.IsAny<IcCard>(), It.IsAny<SQLiteTransaction>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var path = WriteCardCsv();
        try
        {
            await service.ImportCardsAsync(path);

            importLogger.Entries
                .Where(e => e.Level == LogLevel.Error)
                .Should().ContainSingle(
                    "同じ例外を 2 度 Error で記録しないこと（#1817）。実際: "
                    + importLogger.FormatEntries());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 対の表明。内側で記録されていない失敗（トランザクションへ入る前）は
    /// 共通ハンドラーが記録すること。これが無いと「常に記録しない」実装でも緑になる。
    /// </summary>
    [Fact]
    public async Task 取込失敗時_内側で記録されていない例外は共通ハンドラーが記録すること()
    {
        var (service, cardRepository, importLogger) = CreateImportService();
        cardRepository
            .Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var path = WriteCardCsv();
        try
        {
            await service.ImportCardsAsync(path);

            importLogger.Entries.Where(e => e.Level == LogLevel.Error).Should().ContainSingle(
                "実際: " + importLogger.FormatEntries());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 想定内の入力の問題（存在しないファイル）は `Warning` に留める（#1716）。
    /// 職員が選び直せば解決する失敗で `Error` を積むと、本当の不具合が埋もれる。
    /// </summary>
    [Fact]
    public async Task 取込失敗時_想定内の入力の問題はWarningに留めること()
    {
        var (service, _, importLogger) = CreateImportService();

        var missing = TempCsvPath();
        var result = await service.ImportCardsAsync(missing);

        result.Success.Should().BeFalse();
        importLogger.Entries.Should().NotBeEmpty("痕跡は残すこと（#1819）");
        importLogger.Entries.Where(e => e.Level == LogLevel.Error).Should().BeEmpty(
            "想定内の入力の問題は Error にしないこと。実際: " + importLogger.FormatEntries());
        importLogger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    /// <summary>
    /// 操作名は経路ごとに具体的であること（#1956 / #1820）。
    /// 既定値付きの引数にすると全経路が同じ汎用名になり、「何が」失敗したのか区別できない。
    /// </summary>
    [Fact]
    public async Task 取込失敗時_経路ごとに異なる操作名を名乗ること()
    {
        var boom = new InvalidOperationException("boom");

        var (cardService, cardRepository, _) = CreateImportService();
        cardRepository
            .Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(boom);
        _staffRepositoryMock
            .Setup(x => x.GetByIdmAsync(It.IsAny<string>(), It.IsAny<bool>()))
            .ThrowsAsync(boom);

        var cardPath = WriteCardCsv();
        var staffPath = WriteStaffCsv();
        try
        {
            var cardResult = await cardService.ImportCardsAsync(cardPath);
            var staffResult = await cardService.ImportStaffAsync(staffPath);

            cardResult.ErrorMessage.Should().Contain("カードCSVの取り込み");
            staffResult.ErrorMessage.Should().Contain("職員CSVの取り込み");
            cardResult.ErrorMessage.Should().NotBe(staffResult.ErrorMessage,
                "経路ごとに「何が」失敗したかを区別できること（#1956 / #1820）");
        }
        finally
        {
            File.Delete(cardPath);
            File.Delete(staffPath);
        }
    }

    private static string WriteStaffCsv()
    {
        var path = TempCsvPath();
        File.WriteAllLines(path, new[]
        {
            "職員IDm,氏名,職員番号,備考",
            "FEDCBA9876543210,博多 花子,12345,"
        });
        return path;
    }

    public void Dispose()
    {
        // 登録の逆順で破棄する。接続を先に破棄すると、その接続に属する
        // SQLiteTransaction の Dispose が ObjectDisposedException になる。
        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            _disposables[i].Dispose();
        }
    }
}