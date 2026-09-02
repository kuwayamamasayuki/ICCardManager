using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests;

/// <summary>
/// Issue #1997: <c>SettingsRepository.SaveAppSettingsAsync</c> が書き込む設定キーの集合を固定する静的検査。
/// </summary>
/// <remarks>
/// <para>
/// <c>settings</c> テーブルには、画面から編集する設定と、保守処理だけが書く値が同居している。
/// 後者（<c>last_vacuum_date</c> の CAS ロック（#1482）、<c>last_backup_success_at</c> /
/// <c>last_backup_machine</c> / <c>last_vacuum_machine</c> の実施記録（#1689））は、
/// <b>専用の書き込み経路が持つ条件（月ガード・成功時のみ記録）ごとが仕様</b>であり、
/// 素の <c>ON CONFLICT DO UPDATE</c> で一括保存すると条件が失われる。
/// </para>
/// <para>
/// 実際 <c>last_vacuum_date</c> は一括保存に載っており、TTL キャッシュ由来の古い
/// <c>AppSettings</c>（他 PC が CAS を獲得する前の値）を書き戻して当月のロックを巻き戻していた。
/// 挙動テストは「いま載っているキー」ごとの回帰しか固定できないため、<b>集合そのもの</b>を
/// ここで固定する（`.claude/rules/error-messages.md` #1764「個別テストは経路の追加に追随できない」）。
/// 期待値は本番の定数から導出せず<b>リテラルで書く</b>（#1884 / #1940: 本番と期待値が
/// 同時に動くと表明が自己充足する）。
/// </para>
/// </remarks>
public class SettingsMaintenanceKeyConventionTests
{
    private static readonly string RepositoryPath = Path.Combine(
        TestPaths.GetProductionSourceRoot(), "Data", "Repositories", "SettingsRepository.cs");

    private const string SaveSignatureMarker = "Task<bool> SaveAppSettingsAsync(AppSettings settings)";

    /// <summary>
    /// 一括保存が直接書き込む設定キー定数。
    /// 増減させるときは、その値が「画面から編集する設定」であることを確かめること
    /// （ウィンドウ位置は <c>SaveWindowSettingsToDbAsync</c> の内側で書くため、ここには現れない）。
    /// </summary>
    private static readonly string[] ExpectedKeyConstants =
    {
        "KeyWarningBalance",
        "KeyBackupPath",
        "KeyFontSize",
        "KeySoundMode",
        "KeyToastPosition",
        "KeyDepartmentType",
        "KeySkipBusStopInputOnReturn",
        "KeySkipCompanionCountInputOnReturn",
        "KeyReportOutputFolder",
    };

    /// <summary>
    /// 専用経路だけが書いてよい保守用のキー（生の文字列でも書かせない）。
    /// </summary>
    private static readonly string[] MaintenanceKeyLiterals =
    {
        "last_vacuum_date",
        "last_vacuum_machine",
        "last_backup_success_at",
        "last_backup_machine",
    };

    [Fact]
    public void 一括保存が書き込む設定キーは画面から編集する設定だけであること()
    {
        var body = ExtractSaveBody();

        ExtractKeyConstants(body).Should().BeEquivalentTo(
            ExpectedKeyConstants,
            "保守用のキーを足すと専用経路の条件（月ガード・成功時のみ記録）が失われ、" +
            "抜くと画面で編集した設定が保存されなくなる（Issue #1997）");
    }

    [Fact]
    public void 一括保存は保守用のキーを生の文字列でも書かないこと()
    {
        var body = ExtractSaveBody();

        foreach (var literal in MaintenanceKeyLiterals)
        {
            body.Should().NotContain(
                literal,
                $"{literal} は専用の書き込み経路だけが更新する（Issue #1482 / #1689 / #1997）");
        }
    }

    /// <summary>
    /// 対の表明: CAS 経路は <c>last_vacuum_date</c> を<b>実際に書いている</b>こと。
    /// これが無いと、書き込みを両方から消した実装でも上の 2 件は緑になる。
    /// </summary>
    [Fact]
    public void CAS経路はlast_vacuum_dateを書いていること()
    {
        var body = TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            File.ReadAllText(RepositoryPath),
            "Task<bool> TryAcquireMonthlyVacuumLockAsync(DateTime today)");

        body.Should().Contain("KeyLastVacuumDate");
        body.Should().Contain(
            "substr(settings.value, 1, 7)",
            "月ガード付きの UPSERT であること（この条件こそが一括保存へ載せられない理由）");
    }

    /// <summary>
    /// 検査ロジック自体をサンプル入力で固定する（実データが変わっても空振りしないようにする、Issue #1786）。
    /// </summary>
    [Fact]
    public void 抽出はキー定数だけを返すこと()
    {
        const string sample = @"
            success &= await SetAsync(KeyWarningBalance, ""1"", scope);
            success &= await SetAsync(SettingsRepository.KeyBackupPath, ""x"", scope);
            _cacheService.Invalidate(CacheKeys.AppSettings);
            var keyed = KeyLastVacuumDate;
";

        ExtractKeyConstants(sample).Should().BeEquivalentTo(
            new[] { "KeyWarningBalance", "KeyBackupPath", "KeyLastVacuumDate" },
            "CacheKeys のような別クラス名を Key… として拾わないこと");
    }

    private static string ExtractSaveBody()
    {
        return TestSourceInspection.ExtractMethodBodyPreservingLiterals(
            File.ReadAllText(RepositoryPath), SaveSignatureMarker);
    }

    private static IReadOnlyList<string> ExtractKeyConstants(string body)
    {
        return Regex.Matches(body, @"(?<![A-Za-z0-9_])Key[A-Z][A-Za-z0-9_]*")
            .Cast<Match>()
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
