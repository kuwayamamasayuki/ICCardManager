#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentAssertions;
using ICCardManager.Tests.Infrastructure;
using ICCardManager.Views.Helpers;
using Xunit;

namespace ICCardManager.Tests.Views.Helpers;

/// <summary>
/// Issue #2393: 内容に合わせる列（Auto / SizeToCells）を、後から読み込んだ行の内容の幅まで実際に広げる。
/// </summary>
/// <remarks>
/// <para>
/// 欠陥は「必要な幅（DesiredValue）は増えているのに、表示する幅（DisplayValue）が据え置かれる」状態で現れる。
/// 実機（バス停名未入力の一覧・表示スケール 150%）では必要な幅 105.2 に対して表示する幅 100 だった。
/// この状態は単純な DataGrid の組み立てでは安定して再現しないため、列幅の値として直接作って検証する。
/// 実際の画面で末尾が切れないことは UI テスト（<c>DialogButtonOrderAndStaffNameUiTests</c>）が表明する。
/// </para>
/// <para>
/// 「広げる側」と「触らない側」（固定幅・* 列・見出しに合わせる列・既に足りている列）を対で置く。
/// 前者だけだと、すべての列を必要な幅へ書き換える実装でも緑になる。
/// </para>
/// </remarks>
[Collection(StaThreadCollection.Name)]
public class DataGridAutoColumnFitTests
{
    private const double Stale = 100;
    private const double Needed = 105.2;

    [Theory]
    [InlineData(DataGridLengthUnitType.Auto)]
    [InlineData(DataGridLengthUnitType.SizeToCells)]
    public void FitToDesiredWidth_内容に合わせる列は表示する幅を必要な幅まで広げ_種別は保つこと(DataGridLengthUnitType unit)
    {
        StaTestRunner.Run(() =>
        {
            var column = new DataGridTextColumn { Width = new DataGridLength(1, unit, Needed, Stale) };

            var changed = DataGridAutoColumnFit.FitToDesiredWidth(new[] { column });

            changed.Should().BeTrue();
            column.Width.DisplayValue.Should().Be(Needed, "末尾が切れないよう、必要な幅まで広げること");
            column.Width.UnitType.Should().Be(unit, "種別を固定幅に変えると、以後に読み込む行の内容で広がらなくなる");
        });
    }

    [Theory]
    [InlineData(DataGridLengthUnitType.Pixel)]
    [InlineData(DataGridLengthUnitType.Star)]
    [InlineData(DataGridLengthUnitType.SizeToHeader)]
    public void FitToDesiredWidth_内容に合わせない列は触らないこと(DataGridLengthUnitType unit)
    {
        // 固定幅は職員が境界をドラッグして決めた幅でもある。* 列は残りの幅を分け合う列で、広げると他の列を押し出す
        StaTestRunner.Run(() =>
        {
            var original = new DataGridLength(unit == DataGridLengthUnitType.Pixel ? Stale : 1, unit, Needed, Stale);
            var column = new DataGridTextColumn { Width = original };

            var changed = DataGridAutoColumnFit.FitToDesiredWidth(new[] { column });

            changed.Should().BeFalse();
            column.Width.Should().Be(original);
        });
    }

    [Theory]
    [InlineData(Stale)]
    [InlineData(Stale - 10)]
    [InlineData(Stale + 0.005)]
    public void FitToDesiredWidth_必要な幅が表示する幅を上回らなければ触らないこと(double desired)
    {
        // 狭める方向には動かさない（DataGrid の Auto 列と同じ）。誤差程度の差でも書き換えない（レイアウトを繰り返さない）
        StaTestRunner.Run(() =>
        {
            var original = new DataGridLength(1, DataGridLengthUnitType.Auto, desired, Stale);
            var column = new DataGridTextColumn { Width = original };

            DataGridAutoColumnFit.FitToDesiredWidth(new[] { column }).Should().BeFalse();
            column.Width.Should().Be(original);
        });
    }

    [Fact]
    public void FitToDesiredWidth_必要な幅が未計算なら触らないこと()
    {
        StaTestRunner.Run(() =>
        {
            var original = new DataGridLength(1, DataGridLengthUnitType.Auto, double.NaN, Stale);
            var column = new DataGridTextColumn { Width = original };

            DataGridAutoColumnFit.FitToDesiredWidth(new[] { column }).Should().BeFalse();
            column.Width.Should().Be(original);
        });
    }

    [Fact]
    public void FitToDesiredWidth_最大幅を超えては広げないこと()
    {
        StaTestRunner.Run(() =>
        {
            var column = new DataGridTextColumn
            {
                MaxWidth = 103,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto, Needed, Stale),
            };

            DataGridAutoColumnFit.FitToDesiredWidth(new[] { column }).Should().BeTrue();
            column.Width.DisplayValue.Should().Be(103);
        });
    }

    [Fact]
    public void FitToDesiredWidth_複数の列のうち足りない列だけを広げること()
    {
        StaTestRunner.Run(() =>
        {
            var fixedColumn = new DataGridTextColumn { Width = new DataGridLength(80) };
            var enough = new DataGridTextColumn { Width = new DataGridLength(1, DataGridLengthUnitType.Auto, 90, Stale) };
            var shortColumn = new DataGridTextColumn { Width = new DataGridLength(1, DataGridLengthUnitType.Auto, Needed, Stale) };

            DataGridAutoColumnFit.FitToDesiredWidth(new DataGridColumn[] { fixedColumn, enough, shortColumn }).Should().BeTrue();

            fixedColumn.Width.Value.Should().Be(80);
            enough.Width.DisplayValue.Should().Be(Stale);
            shortColumn.Width.DisplayValue.Should().Be(Needed);
        });
    }

    [Fact]
    public void ScheduleFit_引き上げはレイアウトより後の優先度で予約すること()
    {
        // LoadingRow の時点ではセルをまだ測っていない（必要な幅が更新されるのはこの後のレイアウト＝Render 優先度）。
        // Render 以上で予約すると、更新前の必要な幅で判定して何もしないまま終わる
        StaTestRunner.Run(() =>
        {
            var grid = new DataGrid();
            DispatcherPriority? priority = null;
            grid.Dispatcher.Hooks.OperationPosted += (_, e) => priority = e.Operation.Priority;

            DataGridAutoColumnFit.ScheduleFit(grid);

            priority.Should().NotBeNull("引き上げを予約すること");
            ((int)priority!.Value).Should().BeLessThan((int)DispatcherPriority.Render, "レイアウトが済んだ後に実行すること");
        });
    }

    [Fact]
    public void ScheduleFit_予約した処理は一覧の列を渡して引き上げを1回呼ぶこと()
    {
        // 予約の投稿と優先度だけを見ると、予約した処理の中で引き上げを呼ばない実装でも緑になる
        StaTestRunner.Run(() =>
        {
            var grid = new DataGrid();
            var calls = new List<IEnumerable<DataGridColumn>>();

            DataGridAutoColumnFit.ScheduleFit(grid, calls.Add);
            DataGridAutoColumnFit.ScheduleFit(grid, calls.Add);
            calls.Should().BeEmpty("セルを測り終える前（予約した時点）には引き上げないこと");

            Pump();
            calls.Should().ContainSingle("同じレイアウトでの予約は 1 回の引き上げにまとめること")
                .Which.Should().BeSameAs(grid.Columns, "この一覧の列を引き上げること");
        });
    }

    [Fact]
    public void ScheduleFit_同じ一覧への予約は処理されるまで1回にまとめること()
    {
        // 行は 1 回のレイアウトで何件も読み込まれる（LoadingRow は行ごと）。予約を重ねない
        StaTestRunner.Run(() =>
        {
            var grid = new DataGrid();
            DataGridAutoColumnFit.ScheduleFit(grid);
            var posted = 0;
            grid.Dispatcher.Hooks.OperationPosted += (_, _) => posted++;

            DataGridAutoColumnFit.ScheduleFit(grid);
            DataGridAutoColumnFit.ScheduleFit(grid);
            posted.Should().Be(0, "予約済みの間は新たに予約しないこと");

            Pump();

            // Pump 自身も投稿するので、数え直してから確かめる。処理後に予約できないと、
            // 並べ替え・スクロールで後から読み込んだ行では二度と広がらない
            posted = 0;
            DataGridAutoColumnFit.ScheduleFit(grid);
            posted.Should().Be(1, "処理された後は、次の読み込みでまた予約すること");
        });
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void IsEnabled_有効な一覧だけが行の読み込みで引き上げを予約すること(bool enabled, int expectedPosts)
    {
        // 行の読み込み（LoadingRow）との結線。購読を外した・付け忘れた実装は、有効な側が 0 回になって赤
        StaTestRunner.Run(() =>
        {
            var grid = new LoadingRowRaisingDataGrid();
            DataGridAutoColumnFit.SetIsEnabled(grid, enabled);
            var posted = 0;
            grid.Dispatcher.Hooks.OperationPosted += (_, _) => posted++;

            grid.RaiseLoadingRow();

            posted.Should().Be(expectedPosts);
        });
    }

    [Fact]
    public void IsEnabled_有効から無効へ戻すと行の読み込みで予約しないこと()
    {
        StaTestRunner.Run(() =>
        {
            var grid = new LoadingRowRaisingDataGrid();
            DataGridAutoColumnFit.SetIsEnabled(grid, true);
            DataGridAutoColumnFit.SetIsEnabled(grid, false);
            var posted = 0;
            grid.Dispatcher.Hooks.OperationPosted += (_, _) => posted++;

            grid.RaiseLoadingRow();

            posted.Should().Be(0, "無効にしたら購読を外すこと");
        });
    }

    [Fact]
    public void バス停名未入力の一覧で有効にしていること()
    {
        var xaml = XamlElementInspection.StripXmlComments(File.ReadAllText(
            ViewSourceLocator.Resolve(Path.Combine("Views", "Dialogs", "IncompleteBusStopDialog.xaml"))));

        var grid = XamlElementInspection.EnumerateElements(xaml, "DataGrid").Single();
        XamlElementInspection.GetAttribute(grid.StartTag, "helpers:DataGridAutoColumnFit.IsEnabled")
            .Should().Be("True", "利用者の列（Auto）は、後から読み込む「外N名」の幅まで広げる必要がある（Issue #2393）");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>行の読み込み（<see cref="DataGrid.LoadingRow"/>）をテストから起こすための DataGrid</summary>
    private sealed class LoadingRowRaisingDataGrid : DataGrid
    {
        public void RaiseLoadingRow() => OnLoadingRow(new DataGridRowEventArgs(new DataGridRow()));
    }
}
