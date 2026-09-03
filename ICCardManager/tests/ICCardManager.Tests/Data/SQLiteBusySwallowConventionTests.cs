using FluentAssertions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ICCardManager.Tests.Data;

/// <summary>
/// Issue #1951: リポジトリの <c>catch (SQLiteException) { return false; }</c> が
/// SQLITE_BUSY / SQLITE_LOCKED まで <c>bool</c> へ畳み、
/// <c>DbContext.ExecuteWithRetryAsync</c> のリトライ（ResultCode で判定）を
/// 無効化していた欠陥の静的検査。
/// </summary>
/// <remarks>
/// <para>
/// 個別の挙動テスト（<c>RepositoryInsertRetryTests</c>）は経路の追加に追随できない
/// （<c>.claude/rules/error-messages.md</c> #1764）。この family は
/// #1753 → #1808 → #1944 と「戻り値・例外の握りつぶし」として 3 度再発しており、
/// 人手の grep では次の経路で必ず取りこぼす。
/// </para>
/// <para>
/// 検査は<b>対で</b>表明する:
/// ①禁止された形（フィルタ無しで <c>return false;</c> へ畳む catch）が無いこと、
/// ②正しい形（<c>IsTransientLockError</c> を否定するフィルタ）が実際に使われていること。
/// ①だけだと、catch ごと消して例外を素通しにした実装や、
/// 走査対象が 0 件へ縮んだ状態でも緑になる。
/// </para>
/// <para>
/// 走査対象は <c>Data/Repositories</c> 配下の全 <c>.cs</c> から導出する
/// （ファイル名で列挙しない。<c>.claude/rules/development-conventions.md</c> #1786）。
/// </para>
/// </remarks>
public class SQLiteBusySwallowConventionTests
{
    /// <summary>
    /// リトライ判定に届かない握りつぶし（フィルタ無しの catch が false を返す形）が無いこと
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void リポジトリはSQLiteExceptionをフィルタ無しでfalseへ畳まないこと()
    {
        var violations = new List<string>();

        foreach (var (path, source) in EnumerateRepositorySources())
        {
            foreach (var (filter, body) in ExtractSQLiteCatchBlocks(source))
            {
                if (!SwallowsToFalse(body))
                {
                    continue;
                }

                if (!ExcludesTransientLockError(filter))
                {
                    violations.Add($"{Path.GetFileName(path)}: catch ({filter}) が false へ畳んでいる");
                }
            }
        }

        violations.Should().BeEmpty(
            "SQLITE_BUSY / SQLITE_LOCKED を false へ畳むと DbContext.ExecuteWithRetryAsync の " +
            "リトライ（ResultCode で判定）に例外が届かず、共有モードの一過性のロック競合が " +
            "恒久的な失敗として職員に報告される。畳むなら DbContext.IsTransientLockError を " +
            "否定する例外フィルタを添えること（Issue #1951）");
    }

    /// <summary>
    /// 正しい形（一過性ロックを除外するフィルタ）が実際に使われていること
    /// </summary>
    /// <remarks>
    /// 上の検査は「禁止された形の不在」しか見ないため、catch を丸ごと消した実装や
    /// 走査対象が空になった状態でも緑になる。実在を対で固定する。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void 一過性ロックを除外する例外フィルタが実在すること()
    {
        var sources = EnumerateRepositorySources().ToList();

        sources.Should().NotBeEmpty("Data/Repositories 配下のソースを走査できていること");

        var guarded = sources
            .SelectMany(s => ExtractSQLiteCatchBlocks(s.Source))
            .Count(c => SwallowsToFalse(c.Body) && ExcludesTransientLockError(c.Filter));

        guarded.Should().BeGreaterOrEqualTo(2,
            "CardRepository / StaffRepository の InsertAsyncInternal がこの形で書かれていること");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（実データが変わっても空振りしない）
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void 検査ロジックが違反と適合を区別できること()
    {
        const string violating = @"
            try { X(); }
            catch (SQLiteException)
            {
                return false;
            }";

        const string compliant = @"
            try { X(); }
            catch (SQLiteException ex) when (!DbContext.IsTransientLockError(ex))
            {
                // 制約違反など、もう一度実行しても直らない失敗だけを畳む
                return false;
            }";

        // 極性が反転した形（一過性ロック「だけ」を false へ畳む）＝本 Issue の欠陥そのもの。
        // 語の存在だけで判定すると、この形が適合として素通りする。
        const string inverted = @"
            try { X(); }
            catch (SQLiteException ex) when (DbContext.IsTransientLockError(ex))
            {
                return false;
            }";

        const string notSwallowing = @"
            try { X(); }
            catch (SQLiteException)
            {
                // 付随情報が取れないだけ。値は返さない
            }";

        ExtractSQLiteCatchBlocks(violating)
            .Should().ContainSingle().Which.Filter.Should().BeNull();
        ExtractSQLiteCatchBlocks(violating).Single().Body.Should().Contain("return false");

        var compliantBlock = ExtractSQLiteCatchBlocks(compliant).Should().ContainSingle().Subject;
        SwallowsToFalse(compliantBlock.Body).Should().BeTrue();
        ExcludesTransientLockError(compliantBlock.Filter).Should().BeTrue();

        var invertedBlock = ExtractSQLiteCatchBlocks(inverted).Should().ContainSingle().Subject;
        SwallowsToFalse(invertedBlock.Body).Should().BeTrue();
        ExcludesTransientLockError(invertedBlock.Filter).Should().BeFalse(
            "極性が反転したフィルタ（一過性ロックだけを false へ畳む）は適合ではなく違反であること");

        SwallowsToFalse(ExtractSQLiteCatchBlocks(notSwallowing).Single().Body).Should().BeFalse();
    }

    #region ヘルパー

    // 抽出と判定は Issue #2001 の検査と共有する（SQLiteCatchBlockInspection）。
    // 同じ構文を 2 か所で解釈すると、片方だけが実装のリファクタに追随する（#1763）。
    private static IEnumerable<(string Path, string Source)> EnumerateRepositorySources()
        => SQLiteCatchBlockInspection.EnumerateProductionSources("Data", "Repositories");

    private static IReadOnlyList<(string Filter, string Body)> ExtractSQLiteCatchBlocks(string codeOnlySource)
        => SQLiteCatchBlockInspection.ExtractSQLiteCatchBlocks(codeOnlySource);

    private static bool SwallowsToFalse(string body)
        => SQLiteCatchBlockInspection.SwallowsToFalse(body);

    private static bool ExcludesTransientLockError(string filter)
        => SQLiteCatchBlockInspection.ExcludesTransientLockError(filter);

    #endregion
}
