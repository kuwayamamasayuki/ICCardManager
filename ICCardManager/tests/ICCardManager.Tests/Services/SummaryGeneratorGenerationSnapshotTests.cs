using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using Xunit;

namespace ICCardManager.Tests.Services;

/// <summary>
/// Issue #1919: 1 回の摘要生成が「単一の世代」の同一視グループだけを見ることの回帰テスト。
/// </summary>
/// <remarks>
/// <para>
/// 同一視グループはシステム管理画面から運用中に差し替えられる（Issue #1905）。
/// 摘要生成は <c>ConsolidateRoutes</c>（乗継統合）→ <c>DetectRoundTrips</c>（往復検出）→
/// <c>GetRemainingRoutes</c>（余りの算出）と複数段階で同一視を参照し、後ろ 2 つは
/// <b>同じ同一視関係を見ていることが正しさの前提</b>になっている。差し替えが生成の
/// 途中に挟まると復路が「余り」に残り「A～B 往復、B～C」と重複表示され、
/// その結果が 6 年保存の <c>ledger.summary</c> へ書き込まれる。
/// </para>
/// <para>
/// 割り込みは固定時間の待機やスレッドの競争ではなく、<c>CaptureContext</c> を
/// override して<b>捕捉の直後に確実に差し替える</b>形で再現する（testing.md）。
/// 世代は入口で 1 回だけ捕捉して以降の全段階へ引数で持ち回るため、捕捉直後の差し替えは
/// 「生成のどの段階で差し替わっても結果が変わらない」ことの十分な表明になる。
/// </para>
/// </remarks>
[Collection(SummaryGeneratorCollection.Name)]
public class SummaryGeneratorGenerationSnapshotTests : IDisposable
{
    /// <summary>報告事例（Issue #1905）の同一視グループ</summary>
    private static readonly string[][] TenjinGroups =
    {
        new[] { "天神日銀前", "天神中央郵便局前" }
    };

    public SummaryGeneratorGenerationSnapshotTests()
    {
        SummaryGenerator.ResetToDefaults();
    }

    public void Dispose()
    {
        SummaryGenerator.ResetToDefaults();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 生成の開始後にグループが差し替わっても、その生成は開始時点の世代だけを見ること
    /// （復路が「余り」として重複表示にならない）。
    /// </summary>
    [Fact]
    public void 生成中にグループが差し替わっても往復が重複表示にならないこと()
    {
        // Arrange: 報告事例のグループを登録した状態で生成を始め、
        // 生成の開始直後に管理者がグループを空へ保存した状況を再現する
        SummaryGenerator.ApplyTransferStationGroups(TenjinGroups);
        var generator = new GroupSwappingGenerator(Array.Empty<string[]>());

        // Act
        var summary = generator.Generate(CreateBusRoundTripDetails());

        // Assert: 開始時点の世代（グループ登録あり）で一貫して生成される。
        // 途中で世代が変わると DetectRoundTrips と GetRemainingRoutes の突合が壊れ、
        // 「バス（天神日銀前（天神中央郵便局前）～下原中央 往復、下原中央～天神中央郵便局前）」
        // のように復路が重複する
        generator.SwapPerformed.Should().BeTrue("差し替えを割り込ませられていること");
        summary.Should().Be("バス（天神日銀前（天神中央郵便局前）～下原中央 往復）");
    }

    /// <summary>
    /// 対のテスト: 差し替えは「次の生成」には効くこと。
    /// </summary>
    /// <remarks>
    /// これが無いと、<c>ApplyTransferStationGroups</c> を無視する実装
    /// （＝運用中の反映を丸ごと止めた実装。Issue #1905 の退行）でも
    /// 上のテストが緑になる。
    /// </remarks>
    [Fact]
    public void 差し替えは次の生成から反映されること()
    {
        // Arrange
        SummaryGenerator.ApplyTransferStationGroups(TenjinGroups);
        var generator = new SummaryGenerator();

        // Act
        var before = generator.Generate(CreateBusRoundTripDetails());
        SummaryGenerator.ApplyTransferStationGroups(Array.Empty<string[]>());
        var after = generator.Generate(CreateBusRoundTripDetails());

        // Assert: 登録ありは往復、登録なしは従来どおり乗継として目的地が省略される
        before.Should().Be("バス（天神日銀前（天神中央郵便局前）～下原中央 往復）");
        after.Should().Be("バス（天神日銀前～天神中央郵便局前）");
    }

    /// <summary>
    /// 捕捉済みの世代は、その後の差し替えで書き換わらないこと（中間状態の不在）。
    /// </summary>
    /// <remarks>
    /// 旧実装は <c>_options.SummaryRules.TransferStationGroups</c> を<b>その場で書き換えて</b>から
    /// HashSet 版を作り直していたため、①設定は新しいがグループは古い一瞬があり、
    /// ②既に生成を始めていた呼び出しも参照経由で新しい設定を見た。
    /// </remarks>
    [Fact]
    public void 捕捉済みの世代は後の差し替えで書き換わらないこと()
    {
        // Arrange
        SummaryGenerator.ApplyTransferStationGroups(TenjinGroups);
        var captured = new SummaryGenerator().CaptureContext();

        // Act
        SummaryGenerator.ApplyTransferStationGroups(new[] { new[] { "薬院", "大橋" } });

        // Assert: 捕捉済みの世代は設定・同一視判定の双方が旧世代のまま
        captured.AreTransferStations("天神日銀前", "天神中央郵便局前").Should().BeTrue();
        captured.AreTransferStations("薬院", "大橋").Should().BeFalse();
        captured.GetTransferStationGroups().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new[] { "天神日銀前", "天神中央郵便局前" });

        // 現在の世代は新しいグループを見る（差し替え自体は効いている）
        SummaryGenerator.GetTransferStationGroups().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new[] { "薬院", "大橋" });
    }

    /// <summary>
    /// 差し替えが、<see cref="SummaryGenerator.Configure"/> で注入された設定インスタンスを
    /// その場で書き換えないこと。
    /// </summary>
    /// <remarks>
    /// <see cref="OrganizationOptions"/> は DI のシングルトンで、
    /// <c>TransferStationGroupService</c> が「DB に未保存のときの初期値」として参照する。
    /// その場で書き換えると、捕捉済みの世代が参照経由で新しい値を見てしまう
    /// （世代を分けた意味が無くなる）。
    /// </remarks>
    [Fact]
    public void 差し替えが注入済みの設定インスタンスを書き換えないこと()
    {
        // Arrange
        var options = new OrganizationOptions();
        options.SummaryRules.TransferStationGroups = new List<List<string>>
        {
            new() { "天神", "西鉄福岡(天神)" }
        };
        SummaryGenerator.Configure(options);

        // Act
        SummaryGenerator.ApplyTransferStationGroups(TenjinGroups);

        // Assert
        options.SummaryRules.TransferStationGroups.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new[] { "天神", "西鉄福岡(天神)" });
    }

    /// <summary>
    /// 対のテスト: 差し替えはグループ以外の設定（摘要テキスト・生成ルール）を保持すること。
    /// </summary>
    /// <remarks>
    /// 世代を作り直す実装にしたことで、他の項目を既定値へ落とす退行が起こり得る
    /// （development-conventions.md「UPDATE の SET 句は、その経路で本当に編集する列に限る」）。
    /// </remarks>
    [Fact]
    public void 差し替えがグループ以外の設定を保持すること()
    {
        // Arrange
        var options = new OrganizationOptions();
        options.SummaryText.BusLabel = "乗合自動車";
        options.SummaryRules.EnableRoundTripDetection = false;
        SummaryGenerator.Configure(options);

        // Act
        SummaryGenerator.ApplyTransferStationGroups(TenjinGroups);

        // Assert
        SummaryGenerator.BusLabel.Should().Be("乗合自動車");
        var summary = new SummaryGenerator().Generate(CreateBusRoundTripDetails());
        summary.Should().NotContain("往復", "往復検出を無効にした設定が保持されていること");
    }

    /// <summary>
    /// Issue #2035: <see cref="SummaryGenerator.GenerateByDate"/> のポイント還元の摘要も、
    /// 捕捉した世代から作ること。
    /// </summary>
    /// <remarks>
    /// 旧実装はチャージの行だけを世代から引き（<c>ResolveChargeSummary(context)</c>）、
    /// ポイント還元の行は静的な <c>GetPointRedemptionSummary()</c> を呼んでいたため、
    /// 1 回の生成の中で読む先が分かれていた。組織設定の差し替えを捕捉の直後に割り込ませ、
    /// 同じ生成のチャージとポイント還元が同じ世代の文言になることを表明する。
    /// </remarks>
    [Fact]
    public void GenerateByDate_ポイント還元の摘要も捕捉した世代から作ること()
    {
        // Arrange
        SummaryGenerator.Configure(CreateTextOptions("旧チャージ", "旧ポイント還元"));
        var generator = new OptionsSwappingGenerator(CreateTextOptions("新チャージ", "新ポイント還元"));

        // Act
        var summaries = generator.GenerateByDate(CreateChargeAndPointRedemptionDetails());

        // Assert
        generator.SwapPerformed.Should().BeTrue("差し替えを割り込ませられていること");
        summaries.Select(s => s.Summary).Should().BeEquivalentTo(new[] { "旧チャージ", "旧ポイント還元" });
    }

    /// <summary>
    /// 対のテスト: 組織設定の差し替えは次の生成から反映されること。
    /// </summary>
    /// <remarks>
    /// これが無いと、ポイント還元の文言を起動時の値に固定する実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public void GenerateByDate_組織設定の差し替えは次の生成から反映されること()
    {
        // Arrange
        SummaryGenerator.Configure(CreateTextOptions("旧チャージ", "旧ポイント還元"));
        var generator = new OptionsSwappingGenerator(CreateTextOptions("新チャージ", "新ポイント還元"));
        generator.GenerateByDate(CreateChargeAndPointRedemptionDetails());

        // Act
        var summaries = generator.GenerateByDate(CreateChargeAndPointRedemptionDetails());

        // Assert
        summaries.Select(s => s.Summary).Should().BeEquivalentTo(new[] { "新チャージ", "新ポイント還元" });
    }

    /// <summary>
    /// Issue #2035: DI 用コンストラクタで 2 つ目のインスタンスを作っても、
    /// 実行中に反映した同一視グループを起動時の値へ戻さないこと。
    /// </summary>
    /// <remarks>
    /// DI 用コンストラクタは静的な <see cref="SummaryGenerator.Configure"/> を呼ぶ。
    /// 旧実装は同じ設定インスタンスでも毎回世代を作り直したため、2 つ目のインスタンスを作った時点で
    /// システム管理画面（F6）で保存したグループが消えていた（今は Singleton 1 か所だけなので実害は無い）。
    /// </remarks>
    [Fact]
    public void DI用コンストラクタで2つ目を作っても反映済みのグループを戻さないこと()
    {
        // Arrange
        var options = new OrganizationOptions();
        _ = new SummaryGenerator(DepartmentType.MayorOffice, options);
        SummaryGenerator.ApplyTransferStationGroups(TenjinGroups);

        // Act
        _ = new SummaryGenerator(DepartmentType.EnterpriseAccount, options);

        // Assert
        SummaryGenerator.GetTransferStationGroups().Should().ContainSingle()
            .Which.Should().Equal("天神日銀前", "天神中央郵便局前");
    }

    /// <summary>
    /// 対のテスト: 別の設定インスタンスを渡した DI 用コンストラクタは、その設定を反映すること。
    /// </summary>
    /// <remarks>
    /// これが無いと、DI 用コンストラクタが設定を一切反映しない実装でも上のテストが緑になる。
    /// </remarks>
    [Fact]
    public void DI用コンストラクタへ別の設定を渡すとその設定を反映すること()
    {
        // Arrange
        _ = new SummaryGenerator(DepartmentType.MayorOffice, new OrganizationOptions());
        SummaryGenerator.ApplyTransferStationGroups(TenjinGroups);
        var other = new OrganizationOptions();
        other.SummaryText.BusLabel = "乗合自動車";
        other.SummaryRules.TransferStationGroups = new List<List<string>> { new() { "薬院", "薬院大通" } };

        // Act
        _ = new SummaryGenerator(DepartmentType.MayorOffice, other);

        // Assert
        SummaryGenerator.BusLabel.Should().Be("乗合自動車");
        SummaryGenerator.GetTransferStationGroups().Should().ContainSingle()
            .Which.Should().Equal("薬院", "薬院大通");
    }

    private static OrganizationOptions CreateTextOptions(string chargeSummary, string pointRedemption)
    {
        var options = new OrganizationOptions();
        options.SummaryText.ChargeSummaryMayorOffice = chargeSummary;
        options.SummaryText.PointRedemption = pointRedemption;
        return options;
    }

    /// <summary>同じ日のチャージとポイント還元（ICカード履歴は新しい順）</summary>
    private static List<LedgerDetail> CreateChargeAndPointRedemptionDetails() => new()
    {
        new LedgerDetail
        {
            UseDate = new DateTime(2024, 12, 9), Amount = -100, Balance = 2100,
            IsPointRedemption = true, SequenceNumber = 1
        },
        new LedgerDetail
        {
            UseDate = new DateTime(2024, 12, 9), Amount = -1000, Balance = 2000,
            IsCharge = true, SequenceNumber = 2
        },
    };

    /// <summary>
    /// Issue #1905 の報告事例（天神日銀前→下原中央→天神中央郵便局前、ICカード履歴は新しい順）
    /// </summary>
    private static List<LedgerDetail> CreateBusRoundTripDetails() => new()
    {
        CreateBusUsage(4330, "下原中央～天神中央郵便局前"),
        CreateBusUsage(4560, "天神日銀前～下原中央"),
    };

    private static LedgerDetail CreateBusUsage(int balance, string busStops) => new()
    {
        UseDate = new DateTime(2024, 12, 9),
        Amount = 230,
        Balance = balance,
        IsBus = true,
        BusStops = busStops
    };

    /// <summary>
    /// 世代を捕捉した直後にグループを差し替えるテスト用ジェネレーター（Issue #1919）
    /// </summary>
    /// <remarks>
    /// 管理者が［保存］を押した瞬間に職員がカードをタッチした状況を、
    /// スレッドの競争ではなく確定的な順序で再現する。
    /// </remarks>
    private sealed class GroupSwappingGenerator : SummaryGenerator
    {
        private readonly IEnumerable<IEnumerable<string>> _groupsToApply;

        public GroupSwappingGenerator(IEnumerable<IEnumerable<string>> groupsToApply)
        {
            _groupsToApply = groupsToApply;
        }

        /// <summary>差し替えを実際に割り込ませたか（空振りしていないことの表明用）</summary>
        public bool SwapPerformed { get; private set; }

        internal override SummaryGenerationContext CaptureContext()
        {
            var captured = base.CaptureContext();
            if (!SwapPerformed)
            {
                SwapPerformed = true;
                ApplyTransferStationGroups(_groupsToApply);
            }
            return captured;
        }
    }

    /// <summary>
    /// 世代を捕捉した直後に組織設定を差し替えるテスト用ジェネレーター（Issue #2035）
    /// </summary>
    private sealed class OptionsSwappingGenerator : SummaryGenerator
    {
        private readonly OrganizationOptions _optionsToApply;

        public OptionsSwappingGenerator(OrganizationOptions optionsToApply)
        {
            _optionsToApply = optionsToApply;
        }

        /// <summary>差し替えを実際に割り込ませたか（空振りしていないことの表明用）</summary>
        public bool SwapPerformed { get; private set; }

        internal override SummaryGenerationContext CaptureContext()
        {
            var captured = base.CaptureContext();
            if (!SwapPerformed)
            {
                SwapPerformed = true;
                Configure(_optionsToApply);
            }
            return captured;
        }
    }
}
