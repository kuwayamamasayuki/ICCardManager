using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Models;
using ICCardManager.Infrastructure.Caching;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #1818: バス停名サジェストが未入力プレースホルダを組織設定から受け取ることの検証。
/// </summary>
/// <remarks>
/// <para>
/// SQL は以前 <c>bus_stops != '★'</c> をハードコードしており、プレースホルダを
/// 「※」等へ変更した組織ではサジェスト候補に未入力の記号が混ざっていた。
/// 永続化層に交通系固有の判断を持ち込まないため（設計書 05 §2a.5）、
/// リポジトリは値を引数で受け取りパラメータバインドする。
/// </para>
/// <para>
/// 静的状態（<c>SummaryGenerator._options</c>）は触らないが、実 DB を使うため
/// 個別のインスタンスを都度破棄する。
/// </para>
/// </remarks>
public class LedgerRepositoryBusStopSuggestionTests : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly LedgerRepository _ledgerRepository;
    private readonly CardRepository _cardRepository;

    private const string CardA = "AAAA000000000001";

    public LedgerRepositoryBusStopSuggestionTests()
    {
        _dbContext = TestDbContextFactory.Create();
        _ledgerRepository = new LedgerRepository(_dbContext);

        var cacheServiceMock = new Mock<ICacheService>();
        cacheServiceMock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(), It.IsAny<Func<Task<IEnumerable<IcCard>>>>(), It.IsAny<TimeSpan>()))
            .Returns((string key, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan expiration) => factory());
        _cardRepository = new CardRepository(
            _dbContext, cacheServiceMock.Object, Options.Create(new CacheOptions()));
    }

    /// <summary>
    /// ledger の外部キー制約を満たすためカードを登録する。
    /// </summary>
    private Task SeedCardAsync()
        => _cardRepository.InsertAsync(new IcCard
        {
            CardIdm = CardA,
            CardType = "はやかけん",
            CardNumber = "A-001",
        });

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// バス明細を 1 件持つ台帳を追加する。
    /// </summary>
    private async Task AddBusLedgerAsync(string busStops, DateTime useDate)
    {
        var ledgerId = await _ledgerRepository.InsertAsync(new Ledger
        {
            CardIdm = CardA,
            Date = useDate,
            Summary = "テスト",
            Income = 0,
            Expense = 200,
            Balance = 1000,
        });

        await _ledgerRepository.InsertDetailsAsync(ledgerId, new[]
        {
            new LedgerDetail
            {
                LedgerId = ledgerId,
                SequenceNumber = 1,
                IsBus = true,
                BusStops = busStops,
                UseDate = useDate,
                Amount = 200,
            },
        });
    }

    [Fact]
    public async Task 渡したプレースホルダの候補を除外すること()
    {
        await SeedCardAsync();
        var today = DateTime.Today;
        await AddBusLedgerAsync("天神～博多", today);
        await AddBusLedgerAsync("※", today);

        var suggestions = (await _ledgerRepository.GetBusStopSuggestionsAsync("※")).ToList();

        suggestions.Select(s => s.BusStops).Should().ContainSingle().Which.Should().Be("天神～博多");
    }

    [Fact]
    public async Task 渡していない記号は候補として残ること()
    {
        // 対のテスト: 除外が広すぎる（既定「★」も常に落とす）実装を検出する。
        // 「★」を実データとして持つ組織が「※」へ移行した場合、移行前に保存された
        // 「★」は正当な既存データではないが、除外の判断は呼び出し元の設定に従う
        await SeedCardAsync();
        var today = DateTime.Today;
        await AddBusLedgerAsync("★", today);

        var suggestions = (await _ledgerRepository.GetBusStopSuggestionsAsync("※")).ToList();

        suggestions.Select(s => s.BusStops).Should().Contain("★");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task プレースホルダ未指定は例外にすること(string placeholder)
    {
        // null／空文字を黙って受けると SQL の `bus_stops != NULL` が全行 NULL 評価になり、
        // 候補ゼロ件（＝オートコンプリートが静かに死ぬ）になる。無言の空結果ではなく
        // その場の失敗として表面化させる
        await SeedCardAsync();
        await AddBusLedgerAsync("天神～博多", DateTime.Today);

        Func<Task> act = () => _ledgerRepository.GetBusStopSuggestionsAsync(placeholder);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("busStopPlaceholder");
    }
}
