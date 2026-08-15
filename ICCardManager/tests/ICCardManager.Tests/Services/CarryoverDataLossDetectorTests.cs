using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Services;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// 繰越情報消失検出サービスの単体テスト（Issue #1758）
/// </summary>
/// <remarks>
/// <para>
/// Issue #1726 以前の <c>UPDATE ic_card</c> は SET 句に繰越4項目
/// （<see cref="IcCard.StartingPageNumber"/> / <see cref="IcCard.CarryoverIncomeTotal"/> /
/// <see cref="IcCard.CarryoverExpenseTotal"/> / <see cref="IcCard.CarryoverFiscalYear"/>）を含んでいたため、
/// 備考の誤字を直して保存しただけで紙出納簿移行カード（Issue #510 / #1215）の値が既定値へ落ちた。
/// </para>
/// <para>
/// 現在の値だけを見ても「既定値なのは元々そうだから」なのか「消失したから」なのかを区別できない
/// （特に <c>StartingPageNumber = 1</c> は正当な値でもある）。そこで <c>operation_log</c> の
/// BeforeData / AfterData を突き合わせ、**既定値へ落ちた瞬間**を証拠として検出する。
/// </para>
/// </remarks>
public class CarryoverDataLossDetectorTests
{
    private const string TargetIdm = "0123456789ABCDEF";

    /// <summary>
    /// 紙出納簿から移行したカード（繰越情報を持つ健全な状態）
    /// </summary>
    private static IcCard MigratedCard(string idm = TargetIdm, string cardNumber = "001") => new IcCard
    {
        CardIdm = idm,
        CardType = "はやかけん",
        CardNumber = cardNumber,
        Note = "移行カード",
        StartingPageNumber = 7,
        CarryoverIncomeTotal = 45000,
        CarryoverExpenseTotal = 37500,
        CarryoverFiscalYear = 2025
    };

    /// <summary>
    /// 繰越情報が既定値へ落ちた状態（Issue #1726 以前の UPDATE 後）
    /// </summary>
    private static IcCard DamagedCard(string idm = TargetIdm, string cardNumber = "001")
    {
        var card = MigratedCard(idm, cardNumber);
        card.StartingPageNumber = 1;
        card.CarryoverIncomeTotal = 0;
        card.CarryoverExpenseTotal = 0;
        card.CarryoverFiscalYear = null;
        return card;
    }

    /// <summary>
    /// 本番の <c>OperationLogger.SerializeToJson</c> と同じ設定で JSON 化する。
    /// ハードコードした JSON 文字列ではなく実モデルを通すことで、
    /// プロパティ名の変更が検出側とテストで揃って壊れるようにする。
    /// </summary>
    private static string ToJson(IcCard card) =>
        JsonSerializer.Serialize(card, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    private static OperationLog UpdateLog(
        IcCard before,
        IcCard after,
        DateTime timestamp,
        int id = 1,
        string operatorName = "総務 花子") => new OperationLog
        {
            Id = id,
            Timestamp = timestamp,
            OperatorIdm = "FEDCBA9876543210",
            OperatorName = operatorName,
            TargetTable = OperationLogger.Tables.IcCard,
            TargetId = after.CardIdm,
            Action = OperationLogger.Actions.Update,
            BeforeData = before == null ? null : ToJson(before),
            AfterData = after == null ? null : ToJson(after)
        };

    private static CarryoverDataLossDetector CreateDetector(
        IEnumerable<OperationLog> logs,
        IEnumerable<IcCard> currentCards)
    {
        var logRepository = new Mock<IOperationLogRepository>();
        logRepository
            .Setup(r => r.SearchAllAsync(It.IsAny<OperationLogSearchCriteria>()))
            .ReturnsAsync(logs);

        var cardRepository = new Mock<ICardRepository>();
        cardRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(currentCards);

        return new CarryoverDataLossDetector(logRepository.Object, cardRepository.Object);
    }

    #region 検出される場合

    [Fact]
    public async Task DetectAsync_繰越情報が既定値へ落ちたログがあり現在も既定値_失われた元の値を返すこと()
    {
        var lostAt = new DateTime(2026, 5, 20, 14, 30, 0);
        var detector = CreateDetector(
            logs: new[] { UpdateLog(MigratedCard(), DamagedCard(), lostAt) },
            currentCards: new[] { DamagedCard() });

        var result = await detector.DetectAsync();

        result.Should().HaveCount(1);
        var item = result[0];
        item.CardIdm.Should().Be(TargetIdm);
        item.CardDisplayName.Should().Be(MigratedCard().DisplayName);
        item.LostStartingPageNumber.Should().Be(7);
        item.LostCarryoverIncomeTotal.Should().Be(45000);
        item.LostCarryoverExpenseTotal.Should().Be(37500);
        item.LostCarryoverFiscalYear.Should().Be(2025);
        item.LostAt.Should().Be(lostAt);
        item.OperatorName.Should().Be("総務 花子");
    }

    [Fact]
    public async Task DetectAsync_開始ページ番号だけが失われた場合_その項目のみ非nullで返すこと()
    {
        // 開始ページ番号だけを持つカード（繰越累計は元から 0 / null）は #510 のみを使う運用で実在する。
        // 消失していない項目まで「失われた」と表示すると、復旧依頼の値が誤りになる。
        var before = new IcCard
        {
            CardIdm = TargetIdm,
            CardType = "nimoca",
            CardNumber = "003",
            StartingPageNumber = 12
        };
        var after = new IcCard
        {
            CardIdm = TargetIdm,
            CardType = "nimoca",
            CardNumber = "003",
            StartingPageNumber = 1
        };

        var detector = CreateDetector(
            logs: new[] { UpdateLog(before, after, new DateTime(2026, 4, 1)) },
            currentCards: new[] { after });

        var result = await detector.DetectAsync();

        result.Should().HaveCount(1);
        result[0].LostStartingPageNumber.Should().Be(12);
        result[0].LostCarryoverIncomeTotal.Should().BeNull();
        result[0].LostCarryoverExpenseTotal.Should().BeNull();
        result[0].LostCarryoverFiscalYear.Should().BeNull();
    }

    [Fact]
    public async Task DetectAsync_同一カードが複数回編集された場合_最古の消失ログのBefore値を採ること()
    {
        // 2回目以降の UPDATE では Before も既に既定値へ落ちている。
        // 単純に「最新のログ」を採ると、失われた値として 1 / 0 / 0 / null を提示してしまう。
        var first = UpdateLog(MigratedCard(), DamagedCard(), new DateTime(2026, 5, 20), id: 10);
        var second = UpdateLog(DamagedCard(), DamagedCard(), new DateTime(2026, 6, 1), id: 11);

        var detector = CreateDetector(
            logs: new[] { second, first },   // 取得順が時系列と一致しない場合も想定する
            currentCards: new[] { DamagedCard() });

        var result = await detector.DetectAsync();

        result.Should().HaveCount(1);
        result[0].LostStartingPageNumber.Should().Be(7);
        result[0].LostCarryoverIncomeTotal.Should().Be(45000);
        result[0].LostAt.Should().Be(new DateTime(2026, 5, 20));
    }

    [Fact]
    public async Task DetectAsync_複数カードが被害を受けている場合_すべて返すこと()
    {
        const string secondIdm = "FEDCBA9876543211";
        var detector = CreateDetector(
            logs: new[]
            {
                UpdateLog(MigratedCard(), DamagedCard(), new DateTime(2026, 5, 20), id: 1),
                UpdateLog(MigratedCard(secondIdm), DamagedCard(secondIdm), new DateTime(2026, 5, 21), id: 2)
            },
            currentCards: new[] { DamagedCard(), DamagedCard(secondIdm) });

        var result = await detector.DetectAsync();

        result.Select(i => i.CardIdm).Should().BeEquivalentTo(new[] { TargetIdm, secondIdm });
    }

    [Fact]
    public async Task DetectAsync_消失の発生が古い順に返すこと()
    {
        // 並び順は警告文言に効く: WarningService.FormatCardNames は先頭
        // AppConstants.CarryoverDataLossWarningMaxListedCards 枚だけを名前で列挙し、
        // 残りを「ほか○枚」に畳む。順序が不定だと、どのカードが名指しされるかが不定になる。
        //
        // **カード名の昇順が時系列と逆になる配置にする**。両カードの表示名が同じだと
        // 「名前順に並べ替える」実装へ退行しても順序が変わらず、テストが素通りする
        // （実際に OrderBy(CardDisplayName) を注入して赤になることを確認済み）。
        const string olderIdm = "AAAABBBBCCCC0001";
        const string newerIdm = "AAAABBBBCCCC0002";

        var detector = CreateDetector(
            logs: new[]
            {
                UpdateLog(MigratedCard(newerIdm, "001"), DamagedCard(newerIdm, "001"),
                    new DateTime(2026, 6, 15), id: 20),
                UpdateLog(MigratedCard(olderIdm, "900"), DamagedCard(olderIdm, "900"),
                    new DateTime(2026, 5, 20), id: 21)
            },
            currentCards: new[] { DamagedCard(newerIdm, "001"), DamagedCard(olderIdm, "900") });

        var result = await detector.DetectAsync();

        result.Select(i => i.CardDisplayName).Should().ContainInOrder("はやかけん 900", "はやかけん 001");
        result.Select(i => i.CardIdm).Should().ContainInOrder(olderIdm, newerIdm);
    }

    #endregion

    #region 検出されない場合

    [Fact]
    public async Task DetectAsync_繰越情報を持たないカードの通常編集_検出しないこと()
    {
        // 大多数のカードは新規購入で繰越情報を持たない。備考を直しただけで警告が出てはならない。
        var before = new IcCard { CardIdm = TargetIdm, CardType = "SUGOCA", CardNumber = "010", Note = "旧備考" };
        var after = new IcCard { CardIdm = TargetIdm, CardType = "SUGOCA", CardNumber = "010", Note = "新備考" };

        var detector = CreateDetector(
            logs: new[] { UpdateLog(before, after, new DateTime(2026, 5, 20)) },
            currentCards: new[] { after });

        var result = await detector.DetectAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectAsync_DBを直接修正して復旧済みの場合_検出しないこと()
    {
        // 「復旧したのに警告が消えない」を防ぐ。消失ログは6年間残り続けるため、
        // ログの存在だけを根拠にすると警告は永久に消えない（Issue #1739 の教訓）。
        var detector = CreateDetector(
            logs: new[] { UpdateLog(MigratedCard(), DamagedCard(), new DateTime(2026, 5, 20)) },
            currentCards: new[] { MigratedCard() });   // 直接修正で元の値へ戻っている

        var result = await detector.DetectAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectAsync_論理削除済みカードは検出しないこと()
    {
        // 削除済みカードは帳票の対象外。クリックしても手当ての意味がない警告を残さない。
        var detector = CreateDetector(
            logs: new[] { UpdateLog(MigratedCard(), DamagedCard(), new DateTime(2026, 5, 20)) },
            currentCards: Array.Empty<IcCard>());   // GetAllAsync は is_deleted = 0 のみ返す

        var result = await detector.DetectAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DetectAsync_繰越情報が設定されたUPDATE_検出しないこと()
    {
        // 既定値 → 非既定値の向きは「消失」ではない（将来 Issue #1758 案B の復旧 UI が入ったときの経路）。
        var detector = CreateDetector(
            logs: new[] { UpdateLog(DamagedCard(), MigratedCard(), new DateTime(2026, 5, 20)) },
            currentCards: new[] { MigratedCard() });

        var result = await detector.DetectAsync();

        result.Should().BeEmpty();
    }

    #endregion

    #region 壊れた入力への耐性

    [Fact]
    public async Task DetectAsync_BeforeDataがnullのログ_例外を投げずスキップすること()
    {
        // INSERT / RESTORE のログは BeforeData が null。検索条件を通り抜けても落ちてはならない。
        var detector = CreateDetector(
            logs: new[]
            {
                UpdateLog(null, DamagedCard(), new DateTime(2026, 5, 19), id: 1),
                UpdateLog(MigratedCard(), DamagedCard(), new DateTime(2026, 5, 20), id: 2)
            },
            currentCards: new[] { DamagedCard() });

        var result = await detector.DetectAsync();

        result.Should().HaveCount(1);
        result[0].LostStartingPageNumber.Should().Be(7);
    }

    [Fact]
    public async Task DetectAsync_JSONとして解釈できないログ_例外を投げず他の行を処理すること()
    {
        // 6年分の過去ログには旧バージョンが書いた形式が混ざり得る。1行の破損で全体が止まってはならない。
        var broken = UpdateLog(MigratedCard(), DamagedCard(), new DateTime(2026, 5, 19), id: 1);
        broken.BeforeData = "{壊れたJSON";

        var detector = CreateDetector(
            logs: new[]
            {
                broken,
                UpdateLog(MigratedCard(), DamagedCard(), new DateTime(2026, 5, 20), id: 2)
            },
            currentCards: new[] { DamagedCard() });

        var result = await detector.DetectAsync();

        result.Should().HaveCount(1);
        result[0].LostAt.Should().Be(new DateTime(2026, 5, 20));
    }

    [Fact]
    public async Task DetectAsync_操作ログが1件もない場合_空を返すこと()
    {
        var detector = CreateDetector(
            logs: Array.Empty<OperationLog>(),
            currentCards: new[] { DamagedCard() });

        var result = await detector.DetectAsync();

        result.Should().BeEmpty();
    }

    #endregion

    #region 検索条件

    [Fact]
    public async Task DetectAsync_ICカードのUPDATEログだけを検索すること()
    {
        // 6年分の operation_log 全件を読むと共有モードの SMB 遅延で起動が重くなる。
        // 絞り込みを SQL 側へ渡していることを表明する。
        OperationLogSearchCriteria captured = null;
        var logRepository = new Mock<IOperationLogRepository>();
        logRepository
            .Setup(r => r.SearchAllAsync(It.IsAny<OperationLogSearchCriteria>()))
            .Callback<OperationLogSearchCriteria>(c => captured = c)
            .ReturnsAsync(Array.Empty<OperationLog>());

        var cardRepository = new Mock<ICardRepository>();
        cardRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<IcCard>());

        await new CarryoverDataLossDetector(logRepository.Object, cardRepository.Object).DetectAsync();

        captured.Should().NotBeNull();
        captured.TargetTable.Should().Be(OperationLogger.Tables.IcCard);
        captured.Action.Should().Be(OperationLogger.Actions.Update);
    }

    #endregion
}
