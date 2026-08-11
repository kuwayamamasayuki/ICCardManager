using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// OperationLogDisplayNames（操作ログ表示名の Single Source of Truth、Issue #1787）の単体テスト。
/// </summary>
/// <remarks>
/// 表示名マッピングが画面側（OperationLogSearchViewModel）と Excel 側（OperationLogExcelExportService）の
/// 計6箇所に分散していた頃、操作種別の追加（Issue #1302 の IMPORT/EXPORT/BACKUP 等）に表示側が追随できず
/// 英大文字のまま表示され、絞り込みコンボにも選択肢が現れなかった。
/// 定数リフレクションで「OperationLogger.Actions / Tables の全定数が表示名を持つこと」を走査し、
/// 種別を追加したのに表示名を定義し忘れる回帰を検出する（.claude/rules/testing.md の
/// 「列挙型の全値を走査するテスト」と同じ形。定数クラスのため Enum.GetValues ではなくリフレクションを使う）。
/// </remarks>
public class OperationLogDisplayNamesTests
{
    /// <summary>
    /// 定数クラス（Actions / Tables）の public const string 値を列挙する。
    /// </summary>
    private static IReadOnlyList<string> GetConstantValues(Type constantsHolder)
    {
        return constantsHolder
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
    }

    [Fact]
    public void 定数抽出_Actionsから9定数が列挙されること()
    {
        // 抽出ロジック自体の空振り検出（対象がゼロ件でも他のテストが緑になるのを防ぐ）。
        // 定数を追加したらこの期待値も更新する（追加の事実を明示させるための固定値）。
        GetConstantValues(typeof(OperationLogger.Actions)).Should().HaveCount(9);
    }

    [Fact]
    public void 定数抽出_Tablesから5定数が列挙されること()
    {
        GetConstantValues(typeof(OperationLogger.Tables)).Should().HaveCount(5);
    }

    [Fact]
    public void 全Actions定数に日本語表示名が定義されていること()
    {
        foreach (var action in GetConstantValues(typeof(OperationLogger.Actions)))
        {
            var displayName = OperationLogDisplayNames.GetActionDisplayName(action);
            displayName.Should().NotBeNullOrEmpty(
                $"操作種別 {action} には表示名が必要です");
            displayName.Should().NotBe(action,
                $"操作種別 {action} が英大文字のまま（未マッピング）で表示されてはいけません");
        }
    }

    [Fact]
    public void 全Tables定数に日本語表示名が定義されていること()
    {
        foreach (var table in GetConstantValues(typeof(OperationLogger.Tables)))
        {
            var displayName = OperationLogDisplayNames.GetTableDisplayName(table);
            displayName.Should().NotBeNullOrEmpty(
                $"対象テーブル {table} には表示名が必要です");
            displayName.Should().NotBe(table,
                $"対象テーブル {table} が物理名のまま（未マッピング）で表示されてはいけません");
        }
    }

    [Fact]
    public void ActionEntriesが全Actions定数を過不足なく列挙すること()
    {
        // 絞り込みコンボの選択肢はこのリストから生成される。漏れると「その操作種別で絞り込めない」に直結する
        OperationLogDisplayNames.ActionEntries.Select(e => e.Key)
            .Should().BeEquivalentTo(GetConstantValues(typeof(OperationLogger.Actions)));
    }

    [Fact]
    public void TableEntriesが全Tables定数を過不足なく列挙すること()
    {
        OperationLogDisplayNames.TableEntries.Select(e => e.Key)
            .Should().BeEquivalentTo(GetConstantValues(typeof(OperationLogger.Tables)));
    }

    [Fact]
    public void ActionEntriesの表示名がGetActionDisplayNameと一致すること()
    {
        // コンボの表示名と一覧の表示名が食い違うと「絞り込んだのに別の名前で表示される」状態になる
        foreach (var entry in OperationLogDisplayNames.ActionEntries)
        {
            entry.Value.Should().Be(OperationLogDisplayNames.GetActionDisplayName(entry.Key));
        }
    }

    [Fact]
    public void TableEntriesの表示名がGetTableDisplayNameと一致すること()
    {
        foreach (var entry in OperationLogDisplayNames.TableEntries)
        {
            entry.Value.Should().Be(OperationLogDisplayNames.GetTableDisplayName(entry.Key));
        }
    }

    [Theory]
    [InlineData("INSERT", "登録")]
    [InlineData("UPDATE", "更新")]
    [InlineData("DELETE", "削除")]
    [InlineData("RESTORE", "復元")]
    [InlineData("MERGE", "統合")]
    [InlineData("SPLIT", "分割")]
    [InlineData("IMPORT", "インポート")]
    [InlineData("EXPORT", "エクスポート")]
    [InlineData("BACKUP", "バックアップ")]
    public void GetActionDisplayName_全9種別を正しく変換すること(string action, string expected)
    {
        OperationLogDisplayNames.GetActionDisplayName(action).Should().Be(expected);
    }

    [Theory]
    [InlineData("staff", "職員")]
    [InlineData("ic_card", "交通系ICカード")]
    [InlineData("ledger", "利用履歴")]
    [InlineData("ledger_detail", "利用明細")]
    [InlineData("database", "データベース")]
    public void GetTableDisplayName_全5テーブルを正しく変換すること(string table, string expected)
    {
        OperationLogDisplayNames.GetTableDisplayName(table).Should().Be(expected);
    }

    [Fact]
    public void GetActionDisplayName_未知の値はそのまま返すこと()
    {
        // 旧バージョンの DB に未知の操作種別が残っていても表示自体は壊さない（生値フォールバック）
        OperationLogDisplayNames.GetActionDisplayName("UNKNOWN").Should().Be("UNKNOWN");
    }

    [Fact]
    public void GetActionDisplayName_nullは空文字を返すこと()
    {
        OperationLogDisplayNames.GetActionDisplayName(null).Should().Be("");
    }

    [Fact]
    public void GetTableDisplayName_未知のテーブルはそのまま返すこと()
    {
        OperationLogDisplayNames.GetTableDisplayName("unknown_table").Should().Be("unknown_table");
    }

    [Fact]
    public void GetTableDisplayName_nullは空文字を返すこと()
    {
        OperationLogDisplayNames.GetTableDisplayName(null).Should().Be("");
    }
}
