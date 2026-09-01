using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// <see cref="DbContext"/> が生の <see cref="SQLiteConnection"/> を外へ出さないことを固定する規約テスト（Issue #1988）
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DbContext"/> はプロセス全体で <see cref="SQLiteConnection"/> を 1 本しか持たず、
/// SQLite のトランザクションは<b>接続単位</b>である。生の接続を握った経路が発行する文は、
/// セマフォ（<c>_semaphore</c>）・トランザクション計数（<c>_activeTransactionCount</c>）・
/// 保守トランザクションのゲート（<c>_maintenanceTransactionGate</c>、Issue #1984）・
/// 進行中リースの計数（<c>_activeAsyncLeaseCount</c>、Issue #1809）のいずれも通らないため、
/// 他フローのトランザクションへ暗黙参加してそのロールバックで消えたり、
/// リストア中（<c>SuspendConnections</c>）に使用中の接続を閉じられたりする。
/// </para>
/// <para>
/// <b>メソッド名では数えない。</b> Issue #1984 は非推奨の <c>BeginTransaction()</c> を削除したが、
/// <c>GetConnection().BeginTransaction()</c> は削除したメソッドの本体そのもので、同じ抜け道が
/// 別の綴りで残っていた（`.claude/rules/development-conventions.md`「数える単位は『その口が返すもの』
/// まで遡る」／#1843「ガードは綴りではなく資源で書く」）。したがって本検査は
/// <b>「<see cref="SQLiteConnection"/> を外へ出す公開面が存在しないこと」</b>を
/// リフレクションで数える。<c>GetConnection</c> という名前が復活しなくても、
/// 別名のメソッド・プロパティ・フィールドで同じ資源を公開すれば赤くなる。
/// </para>
/// <para>
/// 接続を受け取る側（<c>ConfigureJournalMode(SQLiteConnection)</c> 等の引数）は対象外。
/// 資源を<b>外へ出す</b>ことがリークであって、リースから得た接続を渡すのは正当な使い方である。
/// </para>
/// </remarks>
public class DbContextConnectionAccessConventionTests
{
    /// <summary>
    /// 公開面（public / protected / internal）を走査するためのフラグ。
    /// private は「外へ出していない」ので対象外（<c>GetConnectionInternal</c> はここに該当する）。
    /// </summary>
    private const BindingFlags Surface =
        BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// <see cref="DbContext"/> の公開面に <see cref="SQLiteConnection"/> を返すメンバーが無いこと。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void DbContext_公開面から生のSQLiteConnectionを取得できないこと()
    {
        var violations = FindConnectionExposingMembers().ToList();

        violations.Should().BeEmpty(
            "生の SQLiteConnection を返す公開面は、セマフォ・トランザクション計数・保守ゲート・" +
            "進行中リース計数のいずれも通らない取得口になる（Issue #1988）。" +
            "接続は LeaseConnectionAsync() / LeaseConnection() から、" +
            "トランザクションは BeginTransactionAsync() から取ること。違反: " +
            string.Join(", ", violations));
    }

    /// <summary>
    /// 対の表明: 正規の取得手段が実在すること。
    /// </summary>
    /// <remarks>
    /// 上のテストだけだと、接続を取る手段を丸ごと消した実装でも緑になる
    /// （`.claude/rules/error-messages.md` #1764「禁止された形の不在」と「正しい形の存在」を対で表明する）。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void DbContext_正規の取得手段が実在すること()
    {
        var type = typeof(DbContext);

        type.GetMethod(nameof(DbContext.LeaseConnectionAsync))
            .Should().NotBeNull("非同期経路の唯一の接続取得手段");
        type.GetMethod(nameof(DbContext.LeaseConnection))
            .Should().NotBeNull("同期経路の唯一の接続取得手段");
        type.GetMethod(nameof(DbContext.BeginTransactionAsync))
            .Should().NotBeNull("トランザクションの唯一の取得手段");

        // いずれもリース／スコープ（IDisposable）で返し、解放が計数の減算に対応していること
        typeof(ConnectionLease).Should().Implement<IDisposable>();
        typeof(TransactionScope).Should().Implement<IDisposable>();
    }

    /// <summary>
    /// 検査ロジックそのものを既知の入力で固定する（実データが変わっても空振りしない）。
    /// </summary>
    /// <remarks>
    /// `.claude/rules/development-conventions.md` #1786:
    /// 「空振り検出を『各対象が非空であること』で書かない。検査ロジック自体を既知のサンプル入力で固定する」。
    /// </remarks>
    [Fact]
    [Trait("Category", "Unit")]
    public void 検査ロジック_接続を公開する形を検出し正当な形は見逃すこと()
    {
        var detected = FindConnectionExposingMembers(typeof(SampleLeakyContext))
            .Select(v => v.Split(' ')[0])
            .ToList();

        detected.Should().BeEquivalentTo(
            new[]
            {
                "PublicMethod", "InternalProperty", "ProtectedField",
                "ReturnsBaseType", "ReturnsInterface", "ReturnsArray", "OutParameter",
            },
            "接続を外へ出す綴りは、戻り値・プロパティ・フィールドに限らない。" +
            "基底型（DbConnection）・インターフェース（IDbConnection）・配列・out 引数のいずれも" +
            "同じ共有接続を公開する（#1786「その性質を破れる全経路を列挙する」／#1843）");

        // 正当な形（private の内部取得・接続を受け取る値渡しの引数・別型の戻り値）は検出しないこと
        detected.Should().NotContain("PrivateInternalAccessor");
        detected.Should().NotContain("AcceptsConnection");
        detected.Should().NotContain("LeaseLike");
    }

    /// <summary>
    /// 指定した型の公開面のうち、<see cref="SQLiteConnection"/> を外へ出すメンバーを列挙する。
    /// </summary>
    private static IEnumerable<string> FindConnectionExposingMembers(Type? type = null)
    {
        var target = type ?? typeof(DbContext);

        foreach (var method in target.GetMethods(Surface))
        {
            // プロパティのアクセサは下のプロパティ側で報告する（二重計上しない）
            if (method.IsSpecialName) continue;
            if (method.IsPrivate) continue;
            if (ExposesConnection(method.ReturnType))
            {
                yield return $"{method.Name} (メソッドの戻り値)";
            }

            // out / ref は戻り値と同じく資源を外へ出す（戻り値が void でも成立する）。
            // 値渡しの引数（接続を受け取る ConfigureJournalMode など）は正当なので対象外。
            foreach (var parameter in method.GetParameters())
            {
                if (!parameter.ParameterType.IsByRef) continue;
                if (ExposesConnection(parameter.ParameterType.GetElementType()!))
                {
                    yield return $"{method.Name} (out/ref 引数 {parameter.Name})";
                }
            }
        }

        foreach (var property in target.GetProperties(Surface))
        {
            var getter = property.GetGetMethod(nonPublic: true);
            if (getter == null || getter.IsPrivate) continue;
            if (ExposesConnection(property.PropertyType))
            {
                yield return $"{property.Name} (プロパティ)";
            }
        }

        foreach (var field in target.GetFields(Surface))
        {
            if (field.IsPrivate) continue;
            if (ExposesConnection(field.FieldType))
            {
                yield return $"{field.Name} (フィールド)";
            }
        }
    }

    /// <summary>
    /// 戻り値・フィールドの型が接続そのものを外へ出すか。
    /// </summary>
    /// <remarks>
    /// <c>Task&lt;SQLiteConnection&gt;</c> のように非同期で包んだ形も同じ資源の公開なので数える。
    /// </remarks>
    private static bool ExposesConnection(Type type)
    {
        // 接続の型（SQLiteConnection とその派生、および DbConnection / IDbConnection のような
        // 接続を表す基底・インターフェース）で返せば、外へ出るのは同じ共有接続である。
        // IDbConnection を基準にすることで基底型・インターフェース経由の公開も数えつつ、
        // object / IDisposable のような「接続とは限らない」型は対象外に保つ
        // （リースを IDisposable で返す正当な形を誤検出しない。#1786「誤検出はガードの寿命を縮める」）。
        if (typeof(System.Data.IDbConnection).IsAssignableFrom(type)) return true;

        // 配列・ジェネリック（Task<> / IReadOnlyList<> 等）に包んだ形も同じ資源の公開。
        if (type.IsArray)
        {
            return ExposesConnection(type.GetElementType()!);
        }

        if (type.IsGenericType)
        {
            return type.GetGenericArguments().Any(ExposesConnection);
        }

        return false;
    }

    /// <summary>
    /// 検査ロジックを固定するためのサンプル型（本番コードではない）。
    /// </summary>
    private class SampleLeakyContext
    {
        // 検出されるべき綴り
        public SQLiteConnection PublicMethod() => null!;
        internal SQLiteConnection? InternalProperty => null;
        protected SQLiteConnection? ProtectedField = null;
        public System.Data.Common.DbConnection ReturnsBaseType() => null!;
        public System.Data.IDbConnection ReturnsInterface() => null!;
        public SQLiteConnection[] ReturnsArray() => null!;
        public void OutParameter(out SQLiteConnection connection) => connection = null!;

        // 検出されるべきでない形
        private SQLiteConnection PrivateInternalAccessor() => null!;
        public void AcceptsConnection(SQLiteConnection connection) { _ = connection; }
        public IDisposable LeaseLike() => null!;

        /// <summary>未使用フィールド／メソッドの警告を抑えるための参照。</summary>
        public void TouchAll()
        {
            _ = PrivateInternalAccessor();
            _ = ProtectedField;
        }
    }
}
