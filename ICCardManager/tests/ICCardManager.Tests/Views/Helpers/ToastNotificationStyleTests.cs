using System;
using System.Linq;
using FluentAssertions;
using ICCardManager.Views;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2141: トースト通知の種類ごとの見た目・表示時間と、自動では消えない通知の結線を固定する。
/// </summary>
/// <remarks>
/// <para>
/// 警告（<see cref="ToastType.Warning"/>）は貸出のブラシ（<c>Lending*</c>）を流用しており、
/// 「返却は記録済み・再タッチしないでください」が貸出と同じ暖色で 3 秒で消えていた。
/// <c>Lending*</c> は貸出のシグナル専用で、返却フローに使わない（#2079）。
/// </para>
/// <para>
/// 窓（<see cref="ToastNotificationWindow"/>）は STA 依存で xUnit から生成できないため、
/// 見た目と表示時間は純関数で、窓への結線はソーステキストの静的検査で固定する。
/// </para>
/// </remarks>
public class ToastNotificationStyleTests
{
    private static ToastType[] AllTypes => Enum.GetValues(typeof(ToastType)).Cast<ToastType>().ToArray();

    [Fact]
    public void 警告は警告専用のブラシで表示すること()
    {
        ToastNotificationWindow.GetBackgroundKey(ToastType.Warning).Should().Be("WarningBackgroundBrush");
        ToastNotificationWindow.GetBorderKey(ToastType.Warning).Should().Be("WarningBorderBrush");
        ToastNotificationWindow.GetTitleForegroundKey(ToastType.Warning).Should().Be("WarningForegroundBrush");
    }

    [Fact]
    public void 貸出のブラシは貸出の通知にだけ使うこと()
    {
        // 種類を足したときに Lending* を流用する形が戻らないよう、全種類を走査する（#1739「列挙型の全値で固定する」）
        foreach (var type in AllTypes.Where(t => t != ToastType.Lend))
        {
            new[]
            {
                ToastNotificationWindow.GetBackgroundKey(type),
                ToastNotificationWindow.GetBorderKey(type),
                ToastNotificationWindow.GetTitleForegroundKey(type),
            }.Should().NotContain(k => k.StartsWith("Lending"), $"{type} は貸出のシグナルではない（#2079）");
        }

        // 対の表明: 貸出の通知は貸出の色のまま（全種類から Lending* を外す実装を落とす）
        ToastNotificationWindow.GetBackgroundKey(ToastType.Lend).Should().Be("LendingBackgroundBrush");
    }

    [Fact]
    public void 全種類に見た目が定義されていること()
    {
        foreach (var type in AllTypes)
        {
            ToastNotificationWindow.GetIconText(type).Should().NotBeNullOrEmpty();
            ToastNotificationWindow.GetBackgroundKey(type).Should().EndWith("Brush");
            ToastNotificationWindow.GetBorderKey(type).Should().EndWith("Brush");
            ToastNotificationWindow.GetTitleForegroundKey(type).Should().EndWith("Brush");
        }
    }

    [Fact]
    public void 警告は貸出返却の通知より長く表示すること()
    {
        ToastNotificationWindow.GetDisplayDuration(ToastType.Warning).Should().Be(TimeSpan.FromSeconds(10));
        ToastNotificationWindow.GetDisplayDuration(ToastType.Lend).Should().Be(TimeSpan.FromSeconds(3),
            "対の表明: 貸出・返却の通知まで長くして職員の操作を塞がない");
        ToastNotificationWindow.GetDisplayDuration(ToastType.Return).Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void 自動では消えない通知の案内がキーでも閉じられることを述べること()
    {
        // 閉じる手段を増やしたら、それを述べる文言も直す（#2077 / #2078）
        ToastNotificationWindow.PersistentCloseHint.Should().Be("クリックまたは Esc キーで閉じる");
    }

    [Fact]
    public void 自動では消えない通知を置き場へ置いて1枚に差し替えること()
    {
        var source = ProductionSourceFiles.CSharp
            .Single(f => f.Name == "ToastNotificationWindow.xaml.cs").CodeOnly;

        source.Should().Contain("PersistentSlot.Put(toast)",
            "autoClose: false の通知を置き場へ置かないと、同じ画面隅へ重ねて積み上がる");
        source.Should().Contain("PersistentSlot.Dismiss()",
            "次の職員証タッチ・Esc で閉じる入口");
        source.Should().Contain("PersistentSlot.Release(this)",
            "クリックで閉じた通知が置き場に残ると、次の差し替えで閉じ済みの窓を閉じようとする");
    }

    [Fact]
    public void 記録済みの案内は警告の見た目で自動では消さないこと()
    {
        var source = ProductionSourceFiles.CSharp
            .Single(f => f.Name == "ToastNotificationService.cs").CodeOnly;

        source.Should().MatchRegex(
            @"ShowRecordedNotice\([^)]*\)\s*\{\s*ToastNotificationWindow\.Show\(ToastType\.Warning,[^;]*autoClose:\s*false\)",
            "再タッチを止める指示が 3 秒で消えると、見逃した職員の再タッチが逆の操作として記録される");
        source.Should().MatchRegex(
            @"ShowWarning\([^)]*\)\s*\{\s*ToastNotificationWindow\.Show\(ToastType\.Warning,\s*title,\s*message\)",
            "対の表明: 一般の警告は自動で消える（すべての警告を残すと同じ画面隅に重なる）");
    }
}
