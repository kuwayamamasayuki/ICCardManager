using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.Caching;
using ICCardManager.Models;
using ICCardManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ICCardManager.Tests.Data.Repositories;

/// <summary>
/// Issue #2001: <see cref="CardRepository"/> / <see cref="StaffRepository"/> の
/// <c>InsertAsync</c> が非一時的な <see cref="SQLiteException"/> を
/// <b>痕跡なく</b> <c>false</c> へ畳んでいた欠陥の回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 欠陥の形: <c>catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex)) { return false; }</c>
/// がログを 1 行も出さないため、ディスク満杯（SQLITE_FULL）・DB 破損（SQLITE_CORRUPT）・
/// 読み取り専用（SQLITE_READONLY）といった<b>運用対応が必要な状態</b>が、
/// 入力ミスと同じ「登録に失敗しました」として職員に報告され、障害調査の手掛かりが残らなかった。
/// </para>
/// <para>
/// 採った方針は Issue の案 B（畳んだまま痕跡だけ残す）。呼び出し元 5 経路
/// （カード登録 ViewModel の 2 箇所・職員登録 ViewModel・CSV インポートの card / staff。
/// ほかに開発用の <c>DebugDataService</c> が戻り値を捨てて呼ぶ）は
/// <c>false</c> を行単位の失敗として扱っており、例外へ変えると
/// 「例外型の変更は、その型を前提にしていた上位の分岐を静かに外す」（#1757）に触れる。
/// </para>
/// <para>
/// <b>この catch は ResultCode で分岐しないため、性質の違う 2 種類の失敗が同じ 1 本を通る。</b>
/// 制約違反（主キー重複）は利用者が入力を直せば解決する想定内の失敗、
/// 読み取り専用・ディスク満杯は管理者の対応を要する障害である。
/// レベルと「どうすれば」を分けないと、日常的に起きる前者が <c>Error</c> を積んで
/// 後者を埋もれさせ、しかも実行できない復旧指示が残る（#1991 / #1817）。
/// 本クラスは<b>両方の経路を別々に</b>固定する。
/// </para>
/// <para>
/// テストは <b>対で</b> 置く:
/// ①「欠陥を突く側」= 障害（読み取り専用）で <c>false</c> になるとき Error が 1 件残ること、
/// ②「レベルの分岐」= 制約違反（主キー重複）は Warning で、復旧指示が入力の側を向くこと、
/// ③「畳む契約を壊していない側」= いずれも戻り値は <c>false</c> のままであること、
/// ④「正当な経路を汚していない側」= 成功した登録では何も記録しないこと、
/// ⑤「整備済みの案内へ倒れる経路を汚していない側」= 管理番号の重複は
/// <see cref="DuplicateCardNumberException"/> へ変換され記録を残さないこと。
/// ①だけだと「無条件に LogError する」実装でも緑になり、②④⑤がそれを塞ぐ。
/// </para>
/// <para>
/// 一過性のロック競合（SQLITE_BUSY / SQLITE_LOCKED）がこの catch を<b>通らない</b>ことは
/// <c>RepositoryInsertRetryTests</c>（Issue #1951）が既に表明している（リトライで成功する／
/// 解けなければ <c>false</c> ではなく例外として報告される）。本クラスでは重複して固定しない。
/// </para>
/// </remarks>
public class RepositoryInsertFailureLoggingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContext _dbContext;
    private readonly RecordingLogger<CardRepository> _cardLogger = new();
    private readonly RecordingLogger<StaffRepository> _staffLogger = new();
    private readonly CardRepository _cardRepository;
    private readonly StaffRepository _staffRepository;

    public RepositoryInsertFailureLoggingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ic_insert_log_{Guid.NewGuid():N}.db");
        _dbContext = new DbContext(_dbPath);
        _dbContext.InitializeDatabase();

        _cardRepository = new CardRepository(
            _dbContext, CreatePassThroughCache(), Options.Create(new CacheOptions()), _cardLogger);
        _staffRepository = new StaffRepository(
            _dbContext, CreatePassThroughCache(), Options.Create(new CacheOptions()), _staffLogger);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        TryDeleteDatabaseFiles();
        GC.SuppressFinalize(this);
    }

    #region 欠陥を突く側: 運用対応が必要な失敗は Error として痕跡が残る

    /// <summary>
    /// カード登録: 書き込めない DB（読み取り専用）で <c>false</c> を返すとき、
    /// 原因（ResultCode と例外そのもの）が Error レベルで記録されること。
    /// </summary>
    /// <remarks>
    /// <c>PRAGMA query_only</c> で SQLITE_READONLY を決定的に再現する。
    /// ディスク満杯（SQLITE_FULL）・DB 破損（SQLITE_CORRUPT）は単体テストから再現できないが、
    /// この catch は ResultCode で分岐しないため<b>同じ 1 本の経路</b>を通る。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task カード登録_書き込めないDBでの失敗はErrorとして記録されること()
    {
        MakeDatabaseReadOnly();

        var success = await _cardRepository.InsertAsync(CreateCard("CARD000000000001", "はやかけん", "1"));

        success.Should().BeFalse(
            "呼び出し元 5 経路は false を行単位の失敗として扱う。痕跡を残すために契約を変えてはならない");

        var errors = _cardLogger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errors.Should().ContainSingle(
            $"畳んだ失敗の唯一の痕跡がログである。実際の記録: {_cardLogger.FormatEntries()}");
        errors[0].Exception.Should().BeOfType<SQLiteException>(
            "原因の切り分けには例外そのもの（スタックトレースを含む）が要る");
        errors[0].Message.Should().Contain("ReadOnly",
            "ディスク満杯・DB 破損・読み取り専用を区別できる唯一の値が ResultCode である");
        errors[0].Message.Should().Contain("交通系ICカードの登録",
            "どの操作が失敗したかを、ユーザー視点の操作名で示すこと");
        errors[0].Message.Should().Contain("空き容量",
            "管理者が取れる行動（空き容量の確保・権限の是正・破損の確認）を案内すること");
    }

    /// <summary>
    /// 職員登録: 同上
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task 職員登録_書き込めないDBでの失敗はErrorとして記録されること()
    {
        MakeDatabaseReadOnly();

        var success = await _staffRepository.InsertAsync(CreateStaff("STAFF00000000001", "博多 花子"));

        success.Should().BeFalse();

        var errors = _staffLogger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errors.Should().ContainSingle(
            $"カード側だけを直すと、同じ欠陥が職員側に残る。実際の記録: {_staffLogger.FormatEntries()}");
        errors[0].Exception.Should().BeOfType<SQLiteException>();
        errors[0].Message.Should().Contain("ReadOnly");
        errors[0].Message.Should().Contain("職員の登録");
    }

    #endregion

    #region レベルの分岐: 利用者の入力の問題は Warning で、行動指示も入力の側を向く

    /// <summary>
    /// カード登録: 主キー（IDm）の重複は Warning で記録され、
    /// 復旧指示がディスク・権限ではなく入力値を指すこと。
    /// </summary>
    /// <remarks>
    /// 同一 IDm を 2 行含む CSV の取り込みや、既存カードの再登録で日常的に起きる。
    /// ここを Error で積むと本物の障害がログの中に埋もれ、
    /// 「ディスクの空き容量を確認してください」という<b>実行できない指示</b>が残る（#1991 / #1817）。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task カード登録_IDm重複はWarningとして記録され復旧指示が入力を指すこと()
    {
        (await _cardRepository.InsertAsync(CreateCard("CARD000000000002", "nimoca", "3")))
            .Should().BeTrue("前提となる 1 件目の登録は成功していること");

        var success = await _cardRepository.InsertAsync(CreateCard("CARD000000000002", "nimoca", "4"));

        success.Should().BeFalse();
        _cardLogger.Entries.Should().NotContain(e => e.Level == LogLevel.Error,
            $"利用者が入力を直せば解決する失敗を障害として積まないこと。実際の記録: {_cardLogger.FormatEntries()}");

        var warnings = _cardLogger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle(
            $"痕跡そのものは残すこと。実際の記録: {_cardLogger.FormatEntries()}");
        warnings[0].Exception.Should().BeOfType<SQLiteException>();
        warnings[0].Message.Should().Contain("Constraint");
        warnings[0].Message.Should().Contain("重複");
        warnings[0].Message.Should().NotContain("空き容量",
            "制約違反に対して実行できない復旧指示を出さないこと");
    }

    /// <summary>
    /// 職員登録: 同上
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task 職員登録_IDm重複はWarningとして記録されること()
    {
        (await _staffRepository.InsertAsync(CreateStaff("STAFF00000000002", "天神 太郎")))
            .Should().BeTrue();

        var success = await _staffRepository.InsertAsync(CreateStaff("STAFF00000000002", "大橋 一郎"));

        success.Should().BeFalse();
        _staffLogger.Entries.Should().NotContain(e => e.Level == LogLevel.Error,
            $"実際の記録: {_staffLogger.FormatEntries()}");
        _staffLogger.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle();
    }

    #endregion

    #region 対の表明: 正当な経路を汚していない

    /// <summary>
    /// 登録が成功した場合は何も記録しないこと。
    /// </summary>
    /// <remarks>
    /// これが無いと、経路を問わず記録する実装でも上のテストが緑になる。
    /// ログが「本当に対応が必要なとき」だけ出ることは、オオカミ少年化を防ぐ前提（#1689）。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task 登録が成功した場合は記録しないこと()
    {
        (await _cardRepository.InsertAsync(CreateCard("CARD000000000003", "SUGOCA", "5"))).Should().BeTrue();
        (await _staffRepository.InsertAsync(CreateStaff("STAFF00000000003", "薬院 三郎"))).Should().BeTrue();

        _cardLogger.Entries.Should().BeEmpty(
            $"成功した登録は障害でも入力ミスでもない。実際の記録: {_cardLogger.FormatEntries()}");
        _staffLogger.Entries.Should().BeEmpty(
            $"実際の記録: {_staffLogger.FormatEntries()}");
    }

    /// <summary>
    /// 管理番号の重複（<see cref="DuplicateCardNumberException"/> へ変換される経路）は、
    /// この catch を通らないため何も記録しないこと。
    /// </summary>
    /// <remarks>
    /// この表明は catch の<b>順序</b>も固定する。
    /// <c>IsDuplicateCardNumberError</c> の catch を非一時的エラーの catch より
    /// 後ろへ動かすと、重複番号が <c>false</c> ＋ 記録へ落ちて赤くなる。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task 管理番号の重複は例外へ変換され記録しないこと()
    {
        await _cardRepository.InsertAsync(CreateCard("CARD000000000004", "はやかけん", "9"));

        var act = async () => await _cardRepository.InsertAsync(CreateCard("CARD000000000005", "はやかけん", "9"));

        await act.Should().ThrowAsync<DuplicateCardNumberException>();
        _cardLogger.Entries.Should().BeEmpty(
            $"整備済みの案内へ倒れる経路はこの catch を通らない。実際の記録: {_cardLogger.FormatEntries()}");
    }

    #endregion

    #region ヘルパー

    /// <summary>
    /// 共有接続を書き込み不可にして SQLITE_READONLY を決定的に再現する。
    /// </summary>
    /// <remarks>
    /// ファイルの属性を変えるとプラットフォームと実行ユーザーの権限に依存するため、
    /// 接続レベルの <c>PRAGMA query_only</c> を使う。
    /// リポジトリは <c>LeaseConnectionAsync</c> で同じ接続を借りるため、この設定が効く。
    /// </remarks>
    private void MakeDatabaseReadOnly()
    {
        using var lease = _dbContext.LeaseConnection();
        using var command = lease.Connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = ON;";
        command.ExecuteNonQuery();
    }

    private static ICacheService CreatePassThroughCache()
    {
        // キャッシュを挟むと結果が DB の状態だけで決まらなくなるため、ファクトリをそのまま実行する
        var mock = new Mock<ICacheService>();
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<IcCard>>>>(),
                It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<IcCard>>> factory, TimeSpan __) => factory());
        mock.Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<IEnumerable<Staff>>>>(),
                It.IsAny<TimeSpan>()))
            .Returns((string _, Func<Task<IEnumerable<Staff>>> factory, TimeSpan __) => factory());
        return mock.Object;
    }

    private static IcCard CreateCard(string idm, string cardType, string cardNumber) => new IcCard
    {
        CardIdm = idm,
        CardType = cardType,
        CardNumber = cardNumber,
        StartingPageNumber = 1
    };

    private static Staff CreateStaff(string idm, string name) => new Staff
    {
        StaffIdm = idm,
        Name = name
    };

    private void TryDeleteDatabaseFiles()
    {
        foreach (var path in new[] { _dbPath, _dbPath + "-journal", _dbPath + "-wal", _dbPath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 後始末の失敗はテスト結果に影響させない
            }
        }
    }

    #endregion
}
