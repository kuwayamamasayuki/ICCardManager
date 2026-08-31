using System;
using System.Collections.Generic;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1975: DI シングルトンの <see cref="SummaryGenerator"/> が、設定画面（F5）での
/// 部署種別の変更を<b>再起動なしで</b>反映することの回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// DI シングルトンは<b>起動時</b>の <c>settings.DepartmentType</c> を保持したまま固定されており、
/// これを注入で受ける摘要再生成の 6 経路（履歴統合 <c>LedgerMergeService</c> /
/// 履歴分割 <c>LedgerSplitService</c> 2 か所 / 返却時の台帳生成 <c>LendingService</c> 3 か所 /
/// 明細編集 <c>LedgerDetailViewModel</c>）は、企業会計部局へ変更したあとも
/// 「役務費によりチャージ」を 6 年保存の台帳へ書き込み、物品出納簿にそのまま印字していた。
/// </para>
/// <para>
/// 6 経路はいずれも <c>_summaryGenerator.Generate(...)</c> を通るため、本テストは
/// <see cref="SummaryGenerator.Generate"/> / <see cref="SummaryGenerator.GenerateByDate"/> の
/// 2 つの入口で表明する（経路ごとのテストは経路が増えたときに追随できない）。
/// </para>
/// <para>
/// テストは<b>既定と異なる部署種別へ変更してから</b>呼ぶ（既定のままだと修正前のコードでも
/// 緑になる。<c>development-conventions.md</c> #1818）。対の表明として
/// 「変更前の部署種別で作られた摘要が変わらないこと」「他の設定を巻き込まないこと」も置く。
/// </para>
/// <para>
/// 静的状態（<see cref="SummaryGenerator.Configure"/> /
/// <see cref="SummaryGenerator.ApplyTransferStationGroups"/>）へ触れるため
/// <see cref="SummaryGeneratorCollection"/> に属する。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class SummaryGeneratorDepartmentTypeHotSwapTests : IDisposable
{
    private const string MayorOfficeCharge = "役務費によりチャージ";
    private const string EnterpriseCharge = "旅費によりチャージ";

    public SummaryGeneratorDepartmentTypeHotSwapTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 差し替えが <see cref="SummaryGenerator.Generate"/>（履歴統合・分割・明細編集の入口）へ効くこと。
    /// </summary>
    [Fact]
    public void 部署種別の差し替えがGenerateのチャージ摘要へ反映されること()
    {
        // Arrange: 起動時に市長事務部局で組み立てられた DI シングルトンを模す
        var generator = new SummaryGenerator(DepartmentType.MayorOffice);
        var before = generator.Generate(CreateChargeOnlyDetails());

        // Act: 管理者が F5 で企業会計部局へ変更し、保存に成功した
        generator.ApplyDepartmentType(DepartmentType.EnterpriseAccount);
        var after = generator.Generate(CreateChargeOnlyDetails());

        // Assert
        before.Should().Be(MayorOfficeCharge, "変更前は従来どおりであること");
        after.Should().Be(EnterpriseCharge,
            "再起動なしで新しい部署種別が反映されること（Issue #1975）");
    }

    /// <summary>
    /// 差し替えが <see cref="SummaryGenerator.GenerateByDate"/>（返却時の台帳生成の入口）へ効くこと。
    /// </summary>
    /// <remarks>
    /// <c>Generate</c> と <c>GenerateByDate</c> はチャージ摘要を別々の地点で作る。
    /// 片方だけを直す退行を検出するため、入口ごとに表明する。
    /// </remarks>
    [Fact]
    public void 部署種別の差し替えがGenerateByDateのチャージ摘要へ反映されること()
    {
        // Arrange
        var generator = new SummaryGenerator(DepartmentType.MayorOffice);
        var before = generator.GenerateByDate(CreateChargeOnlyDetails());

        // Act
        generator.ApplyDepartmentType(DepartmentType.EnterpriseAccount);
        var after = generator.GenerateByDate(CreateChargeOnlyDetails());

        // Assert
        before.Should().ContainSingle().Which.Summary.Should().Be(MayorOfficeCharge);
        after.Should().ContainSingle().Which.Summary.Should().Be(EnterpriseCharge);
    }

    /// <summary>
    /// 対のテスト: 差し替えていない生成器は従来どおり動くこと（既定へ倒す退行の検出）。
    /// </summary>
    /// <remarks>
    /// これが無いと、部署種別を無視して常に企業会計部局の文言を返す実装でも
    /// 上の 2 件が緑になる。
    /// </remarks>
    [Fact]
    public void 差し替えていない生成器は組み立て時の部署種別を保つこと()
    {
        var mayorOffice = new SummaryGenerator(DepartmentType.MayorOffice);
        var enterprise = new SummaryGenerator(DepartmentType.EnterpriseAccount);

        mayorOffice.Generate(CreateChargeOnlyDetails()).Should().Be(MayorOfficeCharge);
        enterprise.Generate(CreateChargeOnlyDetails()).Should().Be(EnterpriseCharge);
    }

    /// <summary>
    /// 差し替えは<b>そのインスタンス</b>にだけ効き、他の生成器を巻き込まないこと。
    /// </summary>
    /// <remarks>
    /// 部署種別を静的状態にすると、明細 CSV 取込（<c>CsvImportService</c>）や
    /// バス停名入力（<c>BusStopInputViewModel</c>）が設定から組み立てた生成器
    /// （Issue #1955）まで巻き込まれ、DB を読み直した意味が無くなる。
    /// </remarks>
    [Fact]
    public void 差し替えが他の生成器を巻き込まないこと()
    {
        // Arrange: DI シングルトンと、設定を読み直して組み立てた別の生成器
        var singleton = new SummaryGenerator(DepartmentType.MayorOffice);
        var freshlyBuilt = new SummaryGenerator(DepartmentType.MayorOffice);

        // Act
        singleton.ApplyDepartmentType(DepartmentType.EnterpriseAccount);

        // Assert
        singleton.Generate(CreateChargeOnlyDetails()).Should().Be(EnterpriseCharge);
        freshlyBuilt.Generate(CreateChargeOnlyDetails()).Should().Be(MayorOfficeCharge);
    }

    /// <summary>
    /// 対のテスト: 差し替えが部署種別以外の設定（同一視グループ・摘要テキスト）を保持すること。
    /// </summary>
    /// <remarks>
    /// 世代（<c>SummaryGenerationContext</c>）へ部署種別を畳み込む実装にしたため、
    /// 差し替えが世代を作り直して他の項目を既定値へ落とす退行が起こり得る
    /// （<c>development-conventions.md</c>「UPDATE の SET 句は、その経路で本当に編集する列に限る」）。
    /// </remarks>
    [Fact]
    public void 差し替えが部署種別以外の設定を保持すること()
    {
        // Arrange
        var options = new OrganizationOptions();
        options.SummaryText.BusLabel = "乗合自動車";
        SummaryGenerator.Configure(options);
        SummaryGenerator.ApplyTransferStationGroups(new[] { new[] { "天神", "西鉄福岡(天神)" } });

        var generator = new SummaryGenerator(DepartmentType.MayorOffice);

        // Act
        generator.ApplyDepartmentType(DepartmentType.EnterpriseAccount);

        // Assert
        SummaryGenerator.BusLabel.Should().Be("乗合自動車");
        SummaryGenerator.GetTransferStationGroups().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new[] { "天神", "西鉄福岡(天神)" });
        generator.CaptureContext().AreTransferStations("天神", "西鉄福岡(天神)").Should().BeTrue();
    }

    /// <summary>
    /// 対のテスト: 同一視グループの差し替えが部署種別を巻き戻さないこと。
    /// </summary>
    /// <remarks>
    /// <c>ApplyTransferStationGroups</c> は静的な世代を作り直す。部署種別を静的な世代側に
    /// 持たせる設計にすると、システム管理画面（F6）でグループを保存しただけで
    /// チャージ摘要が既定（市長事務部局）へ戻る。現在の実装は部署種別を
    /// <b>インスタンス</b>の単一の情報源として持ち、生成の入口で世代へ畳み込むため
    /// この巻き戻しは構造的に起こらない。本テストはその不変条件を固定する。
    /// </remarks>
    [Fact]
    public void 同一視グループの差し替えが部署種別を巻き戻さないこと()
    {
        // Arrange
        var generator = new SummaryGenerator(DepartmentType.EnterpriseAccount);

        // Act: 管理者がシステム管理画面（F6）で同一視グループを保存した
        SummaryGenerator.ApplyTransferStationGroups(new[] { new[] { "天神", "西鉄福岡(天神)" } });

        // Assert
        generator.Generate(CreateChargeOnlyDetails()).Should().Be(EnterpriseCharge);
    }

    /// <summary>
    /// 生成の開始後に部署種別が差し替わっても、その生成は開始時点の世代だけを見ること
    /// （同じ摘要の中で 2 つの部署種別の文言が混ざらないこと。Issue #1919）。
    /// </summary>
    /// <remarks>
    /// 割り込みは固定時間の待機やスレッドの競争ではなく、<c>CaptureContext</c> を
    /// override して<b>捕捉の直後に確実に差し替える</b>形で再現する。
    /// </remarks>
    [Fact]
    public void 生成中に部署種別が差し替わっても捕捉済みの世代で一貫すること()
    {
        // Arrange: 管理者が［保存］を押した瞬間に職員がカードをタッチした状況
        var generator = new DepartmentSwappingGenerator(
            DepartmentType.MayorOffice, DepartmentType.EnterpriseAccount);

        // Act: 同一日に 2 回チャージした明細（1 回の生成で複数のチャージ摘要を作る）
        var summaries = generator.GenerateByDate(CreateTwoChargesOnSameDay());

        // Assert
        generator.SwapPerformed.Should().BeTrue("差し替えを割り込ませられていること");
        summaries.Should().HaveCount(2);
        summaries.Should().OnlyContain(s => s.Summary == MayorOfficeCharge,
            "1 回の生成の中で新旧の文言が混ざらないこと（Issue #1919）");
    }

    /// <summary>チャージのみの明細（ICカード履歴は新しい順）</summary>
    private static List<LedgerDetail> CreateChargeOnlyDetails() => new()
    {
        CreateCharge(new DateTime(2026, 4, 1), 3000, 5000)
    };

    /// <summary>同一日に 2 回チャージした明細（新しい順）</summary>
    private static List<LedgerDetail> CreateTwoChargesOnSameDay() => new()
    {
        CreateCharge(new DateTime(2026, 4, 1), 3000, 8000),
        CreateCharge(new DateTime(2026, 4, 1), 2000, 5000)
    };

    private static LedgerDetail CreateCharge(DateTime useDate, int amount, int balance) => new()
    {
        UseDate = useDate,
        Amount = -amount,
        Balance = balance,
        IsCharge = true
    };

    /// <summary>
    /// 世代を捕捉した直後に部署種別を差し替えるテスト用ジェネレーター（Issue #1975 / #1919）
    /// </summary>
    private sealed class DepartmentSwappingGenerator : SummaryGenerator
    {
        private readonly DepartmentType _departmentToApply;

        public DepartmentSwappingGenerator(DepartmentType initial, DepartmentType toApply)
            : base(initial)
        {
            _departmentToApply = toApply;
        }

        /// <summary>差し替えを実際に割り込ませたか（空振りしていないことの表明用）</summary>
        public bool SwapPerformed { get; private set; }

        internal override SummaryGenerationContext CaptureContext()
        {
            var captured = base.CaptureContext();
            if (!SwapPerformed)
            {
                SwapPerformed = true;
                ApplyDepartmentType(_departmentToApply);
            }
            return captured;
        }
    }
}
