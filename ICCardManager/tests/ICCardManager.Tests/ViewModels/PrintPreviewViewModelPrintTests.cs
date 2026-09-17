using System;
using System.Collections.Generic;
using System.Linq;
using System.Printing;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using FluentAssertions;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.ViewModels;
using Moq;
using Xunit;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// 印刷プレビューの「印刷」が、印刷ダイアログで確定した用紙寸法でドキュメントを組み直すことの検証（Issue #2047）
/// </summary>
/// <remarks>
/// 旧実装はプレビュー用の <see cref="FlowDocument"/> の寸法だけを印刷ダイアログの値へ上書きし、
/// 改ページ位置（<c>PrintService.GroupRowsByPage</c>）はプレビュー時の寸法のまま印刷していた。
/// 印刷ダイアログは <c>PrintService.RequestPrint</c> を差し替えて再現する。
/// <see cref="FlowDocument"/> は STA スレッドでしか扱えないため、専用スレッドで検証する。
/// </remarks>
public class PrintPreviewViewModelPrintTests
{
    // A4 の DIP 寸法（210mm / 297mm を 1/96 インチで表した値）
    private const double A4LongEdgeDip = 1122.5;
    private const double A4ShortEdgeDip = 793.7;

    private sealed class FakePrintService : PrintService
    {
        private readonly Size? _dialogPageSize;

        public FakePrintService(Size? dialogPageSize)
            : base(new Mock<IReportDataBuilder>().Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<PrintService>.Instance)
        {
            _dialogPageSize = dialogPageSize;
        }

        public int RequestCount { get; private set; }

        public DocumentPaginator? SentPaginator { get; private set; }

        public string? SentDocumentName { get; private set; }

        internal override PrintRequest? RequestPrint(PageOrientation orientation)
        {
            RequestCount++;
            if (_dialogPageSize == null)
            {
                return null;
            }

            return new PrintRequest(_dialogPageSize.Value, (paginator, name) =>
            {
                SentPaginator = paginator;
                SentDocumentName = name;
            });
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("STA スレッドが時間内に完了すること");

        if (captured != null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }

    /// <summary>
    /// A4 横向きでは 2 ページ、A4 縦向きでは 1 ページに収まる行数の帳票データ
    /// （横: データ領域 563.7 に 25 行、縦: 892.5 に 40 行）
    /// </summary>
    private static ReportPrintData CreateReportData()
    {
        return new ReportPrintData
        {
            CardType = "はやかけん",
            CardNumber = "H001",
            Year = 2026,
            Month = 6,
            WarekiYearMonth = "令和8年6月",
            Rows = Enumerable.Range(1, 30)
                .Select(i => new ReportRow { DateDisplay = $"6/{i}", Summary = "鉄道（博多～天神）", Expense = 210, Balance = 1000 })
                .ToList(),
            MonthlyTotal = new ReportTotal { Label = "6月計", Expense = 6300, Balance = 1000 },
        };
    }

    private static int CountPageSections(FlowDocument? document) => document!.Blocks.OfType<Section>().Count();

    [Fact]
    public void Print_印刷ダイアログの寸法でドキュメントを組み直しプレビューは書き換えないこと()
    {
        RunOnSta(() =>
        {
            var service = new FakePrintService(new Size(A4ShortEdgeDip, A4LongEdgeDip));
            var viewModel = new PrintPreviewViewModel(service);
            viewModel.SetDocument(CreateReportData(), "物品出納簿");

            var preview = viewModel.Document!;
            preview.PageWidth.Should().BeApproximately(A4LongEdgeDip, 0.1, "プレビューは A4 横向き（DIP）");
            CountPageSections(preview).Should().Be(2, "前提: A4 横向きでは 30 行が 2 ページに分割される");

            viewModel.PrintCommand.Execute(null);

            service.SentPaginator.Should().NotBeNull();
            var printed = service.SentPaginator!.Source.Should().BeOfType<FlowDocument>().Subject;
            printed.Should().NotBeSameAs(preview, "印刷用のドキュメントは組み直す");
            printed.PageWidth.Should().BeApproximately(A4ShortEdgeDip, 0.1);
            printed.PageHeight.Should().BeApproximately(A4LongEdgeDip, 0.1);
            CountPageSections(printed).Should().Be(1, "改ページ位置も印刷ダイアログの寸法で計算し直す");
            service.SentPaginator!.PageSize.Should().Be(new Size(A4ShortEdgeDip, A4LongEdgeDip));
            service.SentDocumentName.Should().Be("物品出納簿");

            viewModel.Document.Should().BeSameAs(preview, "プレビューのドキュメントは差し替えない");
            preview.PageWidth.Should().BeApproximately(A4LongEdgeDip, 0.1, "プレビューの寸法は書き換えない");
            preview.PageHeight.Should().BeApproximately(A4ShortEdgeDip, 0.1);
            CountPageSections(preview).Should().Be(2);
            viewModel.StatusMessage.Should().Be("印刷を開始しました");
        });
    }

    [Fact]
    public void Print_複数カードでも印刷ダイアログの寸法で組み直すこと()
    {
        RunOnSta(() =>
        {
            var service = new FakePrintService(new Size(A4ShortEdgeDip, A4LongEdgeDip));
            var viewModel = new PrintPreviewViewModel(service);
            viewModel.SetDocument(new List<ReportPrintData> { CreateReportData(), CreateReportData() }, "物品出納簿（2件）");

            var preview = viewModel.Document!;
            CountPageSections(preview).Should().Be(4, "前提: A4 横向きでは各カード 2 ページ");

            viewModel.PrintCommand.Execute(null);

            var printed = service.SentPaginator!.Source.Should().BeOfType<FlowDocument>().Subject;
            printed.PageWidth.Should().BeApproximately(A4ShortEdgeDip, 0.1);
            CountPageSections(printed).Should().Be(2, "A4 縦向きでは各カード 1 ページ");
            CountPageSections(preview).Should().Be(4);
        });
    }

    [Fact]
    public void Print_印刷ダイアログをキャンセルしたら何も送らないこと()
    {
        RunOnSta(() =>
        {
            var service = new FakePrintService(dialogPageSize: null);
            var viewModel = new PrintPreviewViewModel(service);
            viewModel.SetDocument(CreateReportData(), "物品出納簿");

            viewModel.PrintCommand.Execute(null);

            service.RequestCount.Should().Be(1);
            service.SentPaginator.Should().BeNull();
            viewModel.StatusMessage.Should().Be("印刷がキャンセルされました");
        });
    }
}
