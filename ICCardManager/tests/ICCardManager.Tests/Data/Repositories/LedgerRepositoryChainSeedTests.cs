using FluentAssertions;
using ICCardManager.Common;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Data;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1999: 残高チェーンの開始点（シード）自体を id 順で取っていた欠陥の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 前日に Issue #837 の同日統合（チャージ行を新規 INSERT・利用は古い id の行を UPDATE）があり、
/// かつ当日が Issue #1004 の循環形状だと、id 順で取ったシードが前日の**中間残高**になり、
/// それが当日の循環の中間残高にたまたま一致してそのまま採用される。チェーンが回転した状態で
/// 確定するため、除外法にも id 順フォールバックにも落ちず、**確定的に誤った最終残高**が返る。
/// </para>
/// <para>
/// テストは「欠陥を突く側」（前日 id 逆転 × 当日循環）と「正当な既存挙動を塞いでいない側」
/// （前日が単一行の循環日／遡り上限に達しても従来挙動へ戻るだけ）を対で置く。前者だけだと
/// シードを常に null にする実装でも緑になる（当日の循環は解決できなくなるが、
/// この形状に限れば id 順フォールバックが偶然正解を返す並びがあるため）。
/// </para>
/// </remarks>
public class LedgerRepositoryChainSeedTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _repository;

    private const string TestCardIdm = "0102030405060708";

    public LedgerRepositoryChainSeedTests()
    {
        _dbContext = TestDbContextFactory.Create();
        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
                It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());

        _repository = new LedgerRepository(_dbContext);
        var cardRepository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()), NullLogger<CardRepository>.Instance);

        cardRepository.InsertAsync(new IcCard
        {
            CardIdm = TestCardIdm,
            CardType = "はやかけん",
            CardNumber = "H001"
        }).Wait();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 前日に id 逆転（Issue #837）があり当日が循環（Issue #1004）でも、当日の最終残高を正しく返すこと。
    /// </summary>
    [Fact]
    public async Task GetLatestLedgerAsync_前日のid逆転と当日の残高循環が重なっても最終残高を誤らないこと()
    {
        // Arrange
        // 前日 3/9（Issue #837 の同日統合形状）: 時系列は チャージ(1,000→2,000) → 利用(2,000→1,790)。
        // 利用行は古い id の行を UPDATE するため id が小さく、チャージ行が後から INSERT されて id が大きい。
        // したがって id 順の最終行はチャージ行（2,000円 = 中間残高）で、真の最終残高は 1,790円。
        await InsertAsync(new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 210, balance: 1790);
        await InsertAsync(new DateTime(2026, 3, 9), "チャージ", income: 1000, balance: 2000);

        // 当日 3/10（Issue #1004 の循環形状）: 時系列は チャージ(1,790→2,000) → 利用(2,000→1,790)。
        // 当日の行だけでは開始点を特定できないため、前日の最終残高 1,790 がシードとして要る。
        await InsertAsync(new DateTime(2026, 3, 10), "チャージ", income: 210, balance: 2000);
        await InsertAsync(new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 210, balance: 1790);

        // Act
        var result = await _repository.GetLatestLedgerAsync(TestCardIdm);

        // Assert
        // 誤ったシード 2,000 で解決すると利用行が先に選ばれ、チェーンが回転して 2,000円になる
        result.Should().NotBeNull();
        result!.Balance.Should().Be(
            1790,
            "シードは前日の id 順最終行ではなく、前日をチェーン解決した最終残高であるべき");
    }

    /// <summary>
    /// 前日が単一行の通常のデータでは、従来どおり前日残高をシードに循環日を解決できること。
    /// </summary>
    /// <remarks>
    /// 「シードの取得方法を変えた」ことで、遡り自体が壊れていないことを対で表明する。
    /// </remarks>
    [Fact]
    public async Task GetLatestLedgerAsync_前日が単一行なら従来どおり循環日を解決できること()
    {
        // Arrange
        await InsertAsync(new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 260, balance: 1696);
        await InsertAsync(new DateTime(2026, 3, 10), "ポイント還元", income: 240, balance: 1696);
        await InsertAsync(new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 240, balance: 1456);

        // Act
        var result = await _repository.GetLatestLedgerAsync(TestCardIdm);

        // Assert - 時系列は 利用(1,696→1,456) → 還元(1,456→1,696)
        result.Should().NotBeNull();
        result!.Balance.Should().Be(1696);
    }

    /// <summary>
    /// 前日より前にレコードが無い場合でも、シード無しで従来どおり解決できること。
    /// </summary>
    [Fact]
    public async Task GetLatestLedgerAsync_前日が存在しなくても例外にならないこと()
    {
        // Arrange - 新規購入で開始点が確定する日のみ
        await InsertAsync(new DateTime(2026, 3, 10), "新規購入", income: 2000, balance: 2000);
        await InsertAsync(new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 210, balance: 1790);

        // Act
        var result = await _repository.GetLatestLedgerAsync(TestCardIdm);

        // Assert
        result.Should().NotBeNull();
        result!.Balance.Should().Be(1790);
    }

    /// <summary>
    /// シードを必要とする日が 2 日続く場合、更に前の日まで遡って古い順に解決すること。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 遡りが 1 日で止まる形（前日がシードを必要としない）だけを固定すると、
    /// <b>複数日をつなぐ処理そのものが検査されない</b>。実際、この表明が無いと
    /// ①解決ループを新しい日から回す（シードの伝播方向を逆にする）②上限を 1 日にする、
    /// のどちらの変異でも全テストが緑になる（コードレビューで検出）。
    /// </para>
    /// <para>
    /// 形状は 3 日とも実在するもので構成する。3/8 は id 逆転（Issue #837）、3/9 と 3/10 は
    /// 循環（Issue #1004）。3/8 は開始点が一意に決まるのでそこで遡りが止まる。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task GetLatestLedgerAsync_シードを要する日が連続しても古い日から順に解決すること()
    {
        // Arrange
        // 3/8（Issue #837 の id 逆転）: 時系列は チャージ(1,000→2,000) → 利用(2,000→1,790)。
        // 利用行の id が小さい。開始点はチャージ行（balance_before = 1,000）で一意に決まるためシード不要。
        await InsertAsync(new DateTime(2026, 3, 8), "鉄道（博多～天神）", expense: 210, balance: 1790);
        await InsertAsync(new DateTime(2026, 3, 8), "チャージ", income: 1000, balance: 2000);

        // 3/9（循環）: 時系列は チャージ(1,790→2,000) → 利用(2,000→1,790)。利用行の id が小さい。
        // シード 1,790 なら最終 1,790、シード無しなら id 順フォールバックで最終 2,000 と分かれる。
        await InsertAsync(new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 210, balance: 1790);
        await InsertAsync(new DateTime(2026, 3, 9), "チャージ", income: 210, balance: 2000);

        // 3/10（循環）: 同上。シード 1,790 なら最終 1,790、シード 2,000 なら最終 2,000。
        await InsertAsync(new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 210, balance: 1790);
        await InsertAsync(new DateTime(2026, 3, 10), "チャージ", income: 210, balance: 2000);

        // Act
        var result = await _repository.GetLatestLedgerAsync(TestCardIdm);

        // Assert
        // 3/8 → 1,790 を 3/9 のシードに、3/9 → 1,790 を 3/10 のシードにして初めて 1,790 になる。
        // 上限 1 日だと 3/9 がシード無しで 2,000 になり、3/10 も 2,000 を返す。
        result.Should().NotBeNull();
        result!.Balance.Should().Be(
            1790, "シードを要する日が続くときは、確定できる日まで遡って古い順に解決すべき");
    }

    /// <summary>
    /// 開始点を確定できない日が遡り上限を超えて連続しても、打ち切って結果を返すこと（無限に遡らない）。
    /// </summary>
    /// <remarks>
    /// 上限で打ち切った場合はシード無しで解決するため、従来挙動（除外法・id 順フォールバック）へ戻る。
    /// ここでは「値が返ること」と「上限を超えた分の日付までは遡っていないこと」を表明する。
    /// </remarks>
    [Fact]
    public async Task GetLatestLedgerAsync_曖昧な日が上限を超えて連続しても打ち切ること()
    {
        // Arrange - 循環する日（+240 の還元と -240 の利用）を上限段数 + 2 日ぶん連続させる
        var lastDay = new DateTime(2026, 3, 20);
        var ambiguousDays = AppConstants.MaxBalanceChainSeedLookbackDays + 2;

        for (var i = ambiguousDays - 1; i >= 0; i--)
        {
            var day = lastDay.AddDays(-i);
            await InsertAsync(day, "ポイント還元", income: 240, balance: 1696);
            await InsertAsync(day, "鉄道（博多～天神）", expense: 240, balance: 1456);
        }

        // Act
        var result = await _repository.GetLatestLedgerAsync(TestCardIdm);

        // Assert - 打ち切っても結果は返る（従来挙動と同じく、当日の行のどちらかが最終になる）
        result.Should().NotBeNull();
        result!.Date.Date.Should().Be(lastDay);
        result.Balance.Should().BeOneOf(1696, 1456);
    }

    /// <summary>
    /// 貸出中レコードを除外する集計（Issue #1770）では、シードの母集団からも除外されること。
    /// </summary>
    /// <remarks>
    /// 新しいシード取得クエリでも本体クエリと母集団を揃えることの表明。除外を落とすと、
    /// 前日の貸出中プレースホルダの残高がシードになり、循環日の解決が変わる。
    /// </remarks>
    [Fact]
    public async Task GetBalancesBeforeAsync_貸出中レコードをシードの母集団から除外すること()
    {
        // Arrange
        // 前日 3/9: 実績は 1,790円。同日に貸出中プレースホルダ（残高 2,000円）が残っている
        await InsertAsync(new DateTime(2026, 3, 9), "鉄道（博多～天神）", expense: 210, balance: 1790);
        await InsertAsync(new DateTime(2026, 3, 9), "（貸出中）", balance: 2000, isLentRecord: true);

        // 当日 3/10: 循環形状。シードが 1,790 なら チャージ → 利用 で最終 1,790
        await InsertAsync(new DateTime(2026, 3, 10), "チャージ", income: 210, balance: 2000);
        await InsertAsync(new DateTime(2026, 3, 10), "鉄道（博多～天神）", expense: 210, balance: 1790);

        // Act
        var result = await _repository.GetBalancesBeforeAsync(new DateTime(2026, 3, 11));

        // Assert
        result.Should().ContainKey(TestCardIdm);
        result[TestCardIdm].Should().Be(
            1790, "貸出中レコードを除外する集計では、シードも同じ母集団で取るべき");
    }

    private async Task InsertAsync(
        DateTime date, string summary, int balance, int income = 0, int expense = 0, bool isLentRecord = false)
    {
        await _repository.InsertAsync(new Ledger
        {
            CardIdm = TestCardIdm,
            Date = date,
            Summary = summary,
            Income = income,
            Expense = expense,
            Balance = balance,
            IsLentRecord = isLentRecord
        });
    }
}
