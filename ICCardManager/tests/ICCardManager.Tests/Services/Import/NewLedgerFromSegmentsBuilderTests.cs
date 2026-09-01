using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using System.Data.SQLite;
using ICCardManager.Common.Exceptions;
using ICCardManager.Infrastructure.Security;
using ICCardManager.Services.Import.Builders;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services.Import;

/// <summary>
/// <see cref="NewLedgerFromSegmentsBuilder"/> の単体テスト（Issue #1284 Task 8）。
/// 利用履歴 ID 空欄の詳細行から segment 分割を伴って新規 Ledger を作成する責務を検証する。
/// </summary>
public class NewLedgerFromSegmentsBuilderTests
{
    private const string CardIdm = "0102030405060708";

    private static LedgerDetail Usage(DateTime useDate, int amount, int balance) =>
        new LedgerDetail
        {
            UseDate = useDate,
            Amount = amount,
            Balance = balance,
            EntryStation = "博多",
            ExitStation = "天神",
            IsCharge = false,
            IsPointRedemption = false,
            IsBus = false
        };

    [Fact]
    public async Task BuildAndInsertAsync_EmptyDetails_ReturnsZero()
    {
        // Arrange - 空リスト
        var repoMock = new Mock<ILedgerRepository>();
        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), NullLogger.Instance);
        var errors = new List<CsvImportError>();

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 1, 15),
            new List<(int LineNumber, LedgerDetail Detail)>(),
            errors);

        // Assert
        count.Should().Be(0);
        errors.Should().BeEmpty();
        repoMock.Verify(r => r.InsertAsync(It.IsAny<Ledger>()), Times.Never);
        repoMock.Verify(
            r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()),
            Times.Never);
    }

    [Fact]
    public async Task BuildAndInsertAsync_SingleUsageSegment_CreatesOneLedger()
    {
        // Arrange - 通常利用 1 件
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(100);
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), NullLogger.Instance);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 3, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 3, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 5, Detail: detail) },
            errors);

        // Assert
        count.Should().Be(1);
        errors.Should().BeEmpty();
        repoMock.Verify(r => r.InsertAsync(It.Is<Ledger>(
            l => l.CardIdm == CardIdm && l.Date == new DateTime(2024, 3, 1))),
            Times.Once);
        repoMock.Verify(
            r => r.InsertDetailsAsync(100, It.IsAny<IEnumerable<LedgerDetail>>()),
            Times.Once);
    }

    /// <summary>
    /// 明細は「新しい順」で <c>InsertDetailsAsync</c> へ渡されること
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #1913: <c>SplitAtChargeBoundaries</c> は時系列昇順（古い→新しい）で返し、
    /// <c>InsertDetailsAsync</c> は渡された順にそのまま INSERT する。昇順のまま渡すと
    /// <c>LedgerDetail.SequenceNumber</c> の規約（FeliCa 互換で<b>小さい rowid ＝ 新しい</b>）が
    /// 反転し、以後の摘要再生成でブロック順が逆になる。
    /// </para>
    /// <para>
    /// <c>LendingService</c> の同型の挿入（1367 行付近）が既に <c>Reverse()</c> しているのに、
    /// CSV から新規 Ledger を作るこの経路だけが取り残されていた。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BuildAndInsertAsync_明細は新しい順でInsertDetailsAsyncへ渡されること()
    {
        // Arrange - 同一日の利用 3 件（時系列昇順。残高は減っていく）
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(300);

        List<LedgerDetail> insertedDetails = null;
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .Callback<int, IEnumerable<LedgerDetail>>((_, details) => insertedDetails = details.ToList())
            .ReturnsAsync(true);

        var useDate = new DateTime(2024, 3, 1);
        var first = Usage(useDate, amount: 260, balance: 9740);
        first.EntryStation = "博多";
        first.ExitStation = "天神";
        var second = Usage(useDate, amount: 210, balance: 9530);
        second.EntryStation = "薬院";
        second.ExitStation = "大橋";
        var third = Usage(useDate, amount: 230, balance: 9300);
        third.EntryStation = "姪浜";
        third.ExitStation = "西新";

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), NullLogger.Instance);
        var errors = new List<CsvImportError>();

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            useDate,
            new List<(int LineNumber, LedgerDetail Detail)>
            {
                (LineNumber: 2, Detail: first),
                (LineNumber: 3, Detail: second),
                (LineNumber: 4, Detail: third)
            },
            errors);

        // Assert
        count.Should().Be(3);
        errors.Should().BeEmpty();
        insertedDetails.Should().NotBeNull();
        insertedDetails.Select(d => d.EntryStation).Should().Equal(
            new[] { "姪浜", "薬院", "博多" },
            "先に INSERT した明細ほど小さい rowid になるため、最新の明細から渡すこと（Issue #1913）");
    }

    [Fact]
    public async Task BuildAndInsertAsync_GroupDateMinValue_UsesDetailUseDate()
    {
        // Arrange - groupDate が MinValue のときは detail.UseDate（最古）を Ledger.Date に採用
        var repoMock = new Mock<ILedgerRepository>();
        Ledger insertedLedger = null;
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .Callback<Ledger>(l => insertedLedger = l)
            .ReturnsAsync(200);
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), NullLogger.Instance);
        var errors = new List<CsvImportError>();

        var earlier = Usage(new DateTime(2024, 5, 10, 8, 0, 0), amount: 260, balance: 9740);
        var later = Usage(new DateTime(2024, 5, 10, 18, 0, 0), amount: 260, balance: 9480);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            DateTime.MinValue, // 明示的に未指定
            new List<(int LineNumber, LedgerDetail Detail)>
            {
                (LineNumber: 10, Detail: earlier),
                (LineNumber: 11, Detail: later)
            },
            errors);

        // Assert
        count.Should().Be(2);
        errors.Should().BeEmpty();
        insertedLedger.Should().NotBeNull();
        insertedLedger.Date.Should().Be(new DateTime(2024, 5, 10, 8, 0, 0)); // 最古の UseDate
    }

    [Fact]
    public async Task BuildAndInsertAsync_InsertDetailsFails_AddsError()
    {
        // Arrange - InsertDetailsAsync が false を返す
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(300);
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(false);

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), NullLogger.Instance);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 6, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 6, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 20, Detail: detail) },
            errors);

        // Assert
        count.Should().Be(0);
        errors.Should().ContainSingle();
        errors[0].LineNumber.Should().Be(20);
        // Issue #1986: IDm はマスクを通す。「マスク済みを含む」と「生を含まない」を対で表明する
        // （前者だけだと IDm を丸ごと落とした実装でも緑になる）。
        errors[0].Message.Should().Contain(IdmMasker.Mask(CardIdm));
        errors[0].Message.Should().NotContain(CardIdm, "生の IDm を画面へ出さないこと（#1852）");
        // 3 要素（何が／なぜ／どうすれば）を満たし、行動指示で終わること。
        errors[0].Message.Should().Contain("登録できませんでした");
        errors[0].Message.Should().EndWith("取り込んでください。");
        // Issue #1986（コードレビューで検出）: この分岐は InsertAsync がコミット済みの状態で
        // 到達し、明細を持たない台帳の行が残る。そのまま再取込すると CSV の利用履歴 ID は
        // 空欄のままなので 2 つ目の台帳が作られ、6 年保存の台帳が二重計上になる。
        // 「そのまま取り込み直せ」と読める案内を出さないことを表明する。
        errors[0].Message.Should().Contain("二重に登録される");
        errors[0].Message.Should().Contain("不要な行を削除");
        // 原因を断定しない（台帳 ID はこの取込がミリ秒前に採番したもので、他 PC の競合ではない）
        errors[0].Message.Should().NotContain("他のパソコン");
        // Data は突き合わせ用の内部キーであり、画面にもログにも出ないため生のまま保持する。
        errors[0].Data.Should().Be(CardIdm);
    }

    [Fact]
    public async Task BuildAndInsertAsync_RepositoryThrows_AddsError()
    {
        // Arrange - InsertAsync が例外を投げる
        var repoMock = new Mock<ILedgerRepository>();
        var boomMessage = "DB connection lost";
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new InvalidOperationException(boomMessage));

        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), NullLogger.Instance);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 7, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 7, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 30, Detail: detail) },
            errors);

        // Assert
        count.Should().Be(0);
        errors.Should().ContainSingle();
        errors[0].LineNumber.Should().Be(30);
        errors[0].Message.Should().Contain(IdmMasker.Mask(CardIdm));
        errors[0].Message.Should().NotContain(CardIdm, "生の IDm を画面へ出さないこと（#1852）");
        errors[0].Message.Should().NotContain(
            boomMessage, "生の ex.Message を画面へ出さないこと（#1614）");
        // ExceptionMessageFormatter.ToUserMessage が組み立てる 3 要素の文言であること。
        errors[0].Message.Should().Contain("利用履歴の自動作成に失敗しました。");
        errors[0].Message.Should().EndWith("してください。");
        errors[0].Data.Should().Be(CardIdm);
    }

    /// <summary>
    /// Issue #1986（コードレビューで検出）: <c>ToUserMessage</c> は <c>AppException</c> のとき
    /// <c>operation</c> を無視して <c>UserFriendlyMessage</c> をそのまま返す。
    /// 「カード … の」で始めると文にならない連結になるため、両方の分岐で文として成立することを表明する。
    /// </summary>
    [Fact]
    public async Task BuildAndInsertAsync_AppExceptionThrown_ComposesReadableSentence()
    {
        // Arrange
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(DatabaseException.QueryFailed("ledger insert"));

        var builder = new NewLedgerFromSegmentsBuilder(
            repoMock.Object, new SummaryGenerator(), NullLogger.Instance);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 9, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 9, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 50, Detail: detail) },
            errors);

        // Assert - AppException の整備済み文言が「について、」で自然につながること
        errors.Should().ContainSingle();
        errors[0].Message.Should().StartWith($"カード {IdmMasker.Mask(CardIdm)} について、");
        errors[0].Message.Should().Contain("データの操作中にエラーが発生しました。");
        // 「カード … の データの操作中に…」という壊れた連結にならないこと
        errors[0].Message.Should().NotContain($"{IdmMasker.Mask(CardIdm)} のデータの操作中");
    }

    /// <summary>
    /// Issue #1986（コードレビューで検出）: <c>SQLiteException</c>（共有モードの Busy / Locked、
    /// UNC 断）は最も起こりやすい失敗だが、<c>ToUserMessage</c> の対応表に無く
    /// 「予期しない問題が発生しました」へ落ちていた。原因を名指しできること。
    /// </summary>
    [Fact]
    public async Task BuildAndInsertAsync_SQLiteExceptionThrown_NamesDatabaseCause()
    {
        // Arrange
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>()))
            .ThrowsAsync(new SQLiteException(SQLiteErrorCode.Busy, "database is locked"));

        var builder = new NewLedgerFromSegmentsBuilder(
            repoMock.Object, new SummaryGenerator(), NullLogger.Instance);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 10, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 10, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 60, Detail: detail) },
            errors);

        // Assert
        errors.Should().ContainSingle();
        errors[0].Message.Should().Contain("データベースの読み書きができませんでした。");
        errors[0].Message.Should().NotContain(
            "予期しない問題", "SQLite の失敗は既定分岐へ落とさない（原因を名指しする）");
        errors[0].Message.Should().NotContain("SQLite", "技術用語を職員へ出さない");
    }

    /// <summary>
    /// Issue #1986: 文言から生の <c>ex.Message</c> を外したため、技術的詳細の出口はログだけになる。
    /// 「UI 文言」と「ログ」を対で数える（<c>error-messages.md</c> #1817）— 文言を差し替えただけで
    /// 出口がゼロになっていないことを表明する。
    /// </summary>
    [Fact]
    public async Task BuildAndInsertAsync_RepositoryThrows_LogsExceptionWithMaskedIdm()
    {
        // Arrange
        var repoMock = new Mock<ILedgerRepository>();
        var boomMessage = "DB connection lost";
        var boom = new InvalidOperationException(boomMessage);
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ThrowsAsync(boom);

        var logger = new RecordingLogger<NewLedgerFromSegmentsBuilderTests>();
        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), logger);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 7, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 7, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 30, Detail: detail) },
            errors);

        // Assert - 例外オブジェクトごと記録され、調査に使える値（行番号）が載っていること
        var entry = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Error, logger.FormatEntries()).Subject;
        entry.Exception.Should().BeSameAs(boom, "スタックトレースが残ること");
        entry.Message.Should().Contain("30", "行番号が載っていること");

        // ログにも生の IDm を出さない（#1852）
        entry.Message.Should().Contain(IdmMasker.Mask(CardIdm));
        entry.Message.Should().NotContain(CardIdm);
    }

    /// <summary>
    /// 明細挿入の失敗（影響行数 0）も、痕跡がログに残ること。
    /// </summary>
    [Fact]
    public async Task BuildAndInsertAsync_InsertDetailsFails_LogsWithMaskedIdm()
    {
        // Arrange
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(300);
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(false);

        var logger = new RecordingLogger<NewLedgerFromSegmentsBuilderTests>();
        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), logger);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 6, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 6, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 20, Detail: detail) },
            errors);

        // Assert
        var entry = logger.Entries.Should().ContainSingle(
            e => e.Level == LogLevel.Error, logger.FormatEntries()).Subject;
        entry.Message.Should().Contain(IdmMasker.Mask(CardIdm));
        entry.Message.Should().NotContain(CardIdm);
        entry.Message.Should().Contain("20", "行番号が載っていること");
    }

    /// <summary>
    /// 正常系ではエラーログを出さないこと（「常に出す」実装へ緩めても通ってしまうのを防ぐ）。
    /// </summary>
    [Fact]
    public async Task BuildAndInsertAsync_Success_DoesNotLogError()
    {
        // Arrange
        var repoMock = new Mock<ILedgerRepository>();
        repoMock.Setup(r => r.InsertAsync(It.IsAny<Ledger>())).ReturnsAsync(400);
        repoMock.Setup(r => r.InsertDetailsAsync(It.IsAny<int>(), It.IsAny<IEnumerable<LedgerDetail>>()))
            .ReturnsAsync(true);

        var logger = new RecordingLogger<NewLedgerFromSegmentsBuilderTests>();
        var builder = new NewLedgerFromSegmentsBuilder(repoMock.Object, new SummaryGenerator(), logger);
        var errors = new List<CsvImportError>();
        var detail = Usage(new DateTime(2024, 8, 1, 8, 0, 0), amount: 260, balance: 9740);

        // Act
        var count = await builder.BuildAndInsertAsync(
            CardIdm,
            new DateTime(2024, 8, 1),
            new List<(int LineNumber, LedgerDetail Detail)> { (LineNumber: 40, Detail: detail) },
            errors);

        // Assert
        count.Should().Be(1);
        errors.Should().BeEmpty();
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error, logger.FormatEntries());
    }
}
