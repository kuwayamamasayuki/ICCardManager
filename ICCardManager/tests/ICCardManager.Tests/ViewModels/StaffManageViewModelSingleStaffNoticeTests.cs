using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using ICCardManager.Data;
using ICCardManager.Data.Repositories;
using ICCardManager.Dtos;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Models;
using ICCardManager.Services;
using ICCardManager.Tests.Infrastructure.Timing;
using ICCardManager.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IOperationLogRepository = ICCardManager.Data.Repositories.IOperationLogRepository;

namespace ICCardManager.Tests.ViewModels;

/// <summary>
/// Issue #2467: 登録済みの職員が 1 人だけのとき、職員管理画面に「紛失に備えて 2 人以上登録する」注意を出すことの検証
/// </summary>
/// <remarks>
/// <para>
/// 職員の登録・削除などの認証には登録済みの職員の職員証が要り、免除されるのは登録済みの職員が 0 人のときだけ。
/// 1 人だけでその職員証を紛失すると、画面の操作では誰も認証できなくなる。
/// </para>
/// <para>
/// 「出る側」（1 人）と「出ない側」（0 人・2 人以上）を対で置く。片方だけだと、常に出す／常に出さない実装でも緑になる。
/// 人数が変わる経路（登録・削除・復元のあとの再読込、一覧の差し替え）で表示が追随することも、操作を通して確かめる。
/// </para>
/// </remarks>
public class StaffManageViewModelSingleStaffNoticeTests : IDisposable
{
    private readonly Mock<IStaffRepository> _staffRepositoryMock = new();
    private readonly Mock<IStaffAuthService> _staffAuthServiceMock = new();
    private readonly Mock<IDialogService> _dialogServiceMock = new();
    private readonly DbContext _dbContext;
    private readonly StaffManageViewModel _viewModel;

    public StaffManageViewModelSingleStaffNoticeTests()
    {
        var validationServiceMock = new Mock<IValidationService>();
        validationServiceMock.Setup(v => v.ValidateStaffIdm(It.IsAny<string>())).Returns(ValidationResult.Success());
        validationServiceMock.Setup(v => v.ValidateStaffName(It.IsAny<string>())).Returns(ValidationResult.Success());

        _dialogServiceMock.Setup(d => d.ShowConfirmation(It.IsAny<string>(), It.IsAny<string>())).Returns(true);

        _staffAuthServiceMock.Setup(s => s.RequestAuthenticationAsync(It.IsAny<string>()))
            .ReturnsAsync(new StaffAuthResult { Idm = "AAAA000000000001", StaffName = "認証 太郎" });

        var operationLoggerMock = new Mock<OperationLogger>(
            new Mock<IOperationLogRepository>().Object, Mock.Of<ICurrentOperatorContext>());

        _dbContext = new DbContext(":memory:");
        _dbContext.InitializeDatabase();

        _viewModel = new StaffManageViewModel(
            _staffRepositoryMock.Object,
            new Mock<ICardReader>().Object,
            validationServiceMock.Object,
            new StaffManagementService(
                _dbContext, _staffRepositoryMock.Object, operationLoggerMock.Object,
                NullLogger<StaffManagementService>.Instance),
            _dialogServiceMock.Object,
            _staffAuthServiceMock.Object,
            new WeakReferenceMessenger(),
            new RecordingDispatcherService(),
            new TestTimerFactory(),
            new FixedSystemClock(new DateTime(2026, 11, 16, 9, 0, 0)));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static List<Staff> CreateStaff(int count)
        => Enumerable.Range(1, count)
            .Select(i => new Staff
            {
                StaffIdm = $"FFFF00000000000{i}",
                Name = $"職員{i}",
                Number = $"S-00{i}"
            })
            .ToList();

    private void SetupActiveStaff(params List<Staff>[] snapshots)
    {
        var sequence = _staffRepositoryMock.SetupSequence(r => r.GetAllAsync());
        foreach (var snapshot in snapshots)
        {
            sequence = sequence.ReturnsAsync(snapshot);
        }

        // 認証の要否（登録済みの職員がいるか）は最初の一覧と一致させる
        _staffRepositoryMock.Setup(r => r.HasActiveStaffAsync()).ReturnsAsync(snapshots[0].Count > 0);
    }

    #region 人数の境界（0 / 1 / 2 人以上）

    [Fact]
    public async Task 登録済みの職員が1人だけなら注意を表示すること()
    {
        SetupActiveStaff(CreateStaff(1));

        await _viewModel.InitializeAsync();

        _viewModel.StaffList.Should().HaveCount(1);
        _viewModel.IsSingleStaffNoticeVisible.Should().BeTrue(
            "その職員の職員証を紛失すると、画面の操作では誰も認証できなくなるため");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task 登録済みの職員が0人または2人以上なら注意を表示しないこと(int count)
    {
        SetupActiveStaff(CreateStaff(count));

        await _viewModel.InitializeAsync();

        _viewModel.StaffList.Should().HaveCount(count);
        _viewModel.IsSingleStaffNoticeVisible.Should().BeFalse(
            count == 0
                ? "0 人のときは登録の認証が免除される（初回セットアップ）ため、注意は要らない"
                : "2 人以上いれば、1 人が職員証を紛失してもほかの職員の職員証で認証できる");
    }

    [Fact]
    public void 一覧を読み込む前は注意を表示しないこと()
    {
        _viewModel.StaffList.Should().BeEmpty();
        _viewModel.IsSingleStaffNoticeVisible.Should().BeFalse();
    }

    #endregion

    #region 人数が変わる経路への追随

    [Fact]
    public async Task 再読込で1人から2人になると注意が消え_2人から1人になると再び出ること()
    {
        SetupActiveStaff(CreateStaff(1), CreateStaff(2), CreateStaff(1));
        await _viewModel.LoadStaffAsync();
        _viewModel.IsSingleStaffNoticeVisible.Should().BeTrue();

        var changes = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        await _viewModel.LoadStaffAsync();
        _viewModel.IsSingleStaffNoticeVisible.Should().BeFalse();
        changes.Should().Contain(nameof(StaffManageViewModel.IsSingleStaffNoticeVisible),
            "画面の表示は PropertyChanged でしか追随しない");

        await _viewModel.LoadStaffAsync();
        _viewModel.IsSingleStaffNoticeVisible.Should().BeTrue();
    }

    [Fact]
    public async Task 職員が1人のときに2人目を登録すると注意が消えること()
    {
        var before = CreateStaff(1);
        var after = CreateStaff(2);
        SetupActiveStaff(before, after);
        await _viewModel.LoadStaffAsync();
        _viewModel.IsSingleStaffNoticeVisible.Should().BeTrue();

        var newIdm = after[1].StaffIdm;
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(newIdm, true)).ReturnsAsync((Staff?)null);
        _staffRepositoryMock.Setup(r => r.InsertAsync(It.IsAny<Staff>(), It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = newIdm;
        _viewModel.EditName = after[1].Name;
        await _viewModel.SaveAsync();

        _staffRepositoryMock.Verify(r => r.InsertAsync(
            It.Is<Staff>(s => s.StaffIdm == newIdm), It.IsAny<SQLiteTransaction>()), Times.Once);
        _staffAuthServiceMock.Verify(s => s.RequestAuthenticationAsync("職員の登録"), Times.Once,
            "登録済みの職員が 1 人いるので、登録には認証が要る（免除は 0 人のときだけ）");
        _viewModel.StaffList.Should().HaveCount(2);
        _viewModel.IsSingleStaffNoticeVisible.Should().BeFalse();
    }

    [Fact]
    public async Task 職員が2人のときに1人を削除すると注意が出ること()
    {
        var before = CreateStaff(2);
        var after = before.Take(1).ToList();
        SetupActiveStaff(before, after);
        await _viewModel.LoadStaffAsync();
        _viewModel.IsSingleStaffNoticeVisible.Should().BeFalse();

        var target = before[1];
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(target.StaffIdm, false)).ReturnsAsync(target);
        _staffRepositoryMock.Setup(r => r.DeleteAsync(target.StaffIdm, It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        _viewModel.SelectedStaff = _viewModel.StaffList.Single(s => s.StaffIdm == target.StaffIdm);
        await _viewModel.DeleteAsync();

        _staffRepositoryMock.Verify(r => r.DeleteAsync(target.StaffIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        _viewModel.StaffList.Should().HaveCount(1);
        _viewModel.IsSingleStaffNoticeVisible.Should().BeTrue();
    }

    [Fact]
    public async Task 職員が1人のときに削除済みの職員を復元すると注意が消えること()
    {
        var before = CreateStaff(1);
        var after = CreateStaff(2);
        SetupActiveStaff(before, after);
        await _viewModel.LoadStaffAsync();
        _viewModel.IsSingleStaffNoticeVisible.Should().BeTrue();

        var deleted = new Staff
        {
            StaffIdm = after[1].StaffIdm,
            Name = after[1].Name,
            Number = after[1].Number,
            IsDeleted = true,
            DeletedAt = new DateTime(2026, 4, 1, 9, 0, 0)
        };
        _staffRepositoryMock.Setup(r => r.GetByIdmAsync(deleted.StaffIdm, true)).ReturnsAsync(deleted);
        _staffRepositoryMock.Setup(r => r.RestoreAsync(deleted.StaffIdm, It.IsAny<SQLiteTransaction>()))
            .ReturnsAsync(true);

        _viewModel.StartNewStaff();
        _viewModel.EditStaffIdm = deleted.StaffIdm;
        _viewModel.EditName = "入力した氏名";
        await _viewModel.SaveAsync();

        _staffRepositoryMock.Verify(r => r.RestoreAsync(deleted.StaffIdm, It.IsAny<SQLiteTransaction>()), Times.Once);
        _viewModel.StaffList.Should().HaveCount(2);
        _viewModel.IsSingleStaffNoticeVisible.Should().BeFalse();
    }

    [Fact]
    public void 一覧そのものを差し替えても注意が追随し_古い一覧の変化には反応しないこと()
    {
        var oldList = _viewModel.StaffList;
        var replaced = new ObservableCollection<StaffDto>
        {
            new() { StaffIdm = "FFFF000000000001", Name = "職員1" }
        };

        var changes = new List<string?>();
        _viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        // 差し替えそのものでも表示の再評価を知らせる（[NotifyPropertyChangedFor]）
        _viewModel.StaffList = replaced;
        changes.Should().Contain(nameof(StaffManageViewModel.IsSingleStaffNoticeVisible),
            "一覧を差し替えると人数が変わり得るので、画面へ表示の再評価を知らせる");
        _viewModel.IsSingleStaffNoticeVisible.Should().BeTrue();
        changes.Clear();

        // 古い一覧への追加は表示に関係しない（購読が新しい一覧へ移っている）
        oldList.Add(new StaffDto { StaffIdm = "FFFF000000000009", Name = "古い一覧の職員" });
        changes.Should().NotContain(nameof(StaffManageViewModel.IsSingleStaffNoticeVisible));

        // 新しい一覧への追加には追随する
        replaced.Add(new StaffDto { StaffIdm = "FFFF000000000002", Name = "職員2" });
        changes.Should().Contain(nameof(StaffManageViewModel.IsSingleStaffNoticeVisible));
        _viewModel.IsSingleStaffNoticeVisible.Should().BeFalse();
    }

    #endregion

    #region 文言

    [Fact]
    public void 注意の文言は何が_なぜ_どうすればを含むこと()
    {
        StaffManageViewModel.SingleStaffNotice.Should().Be(
            "登録済みの職員が 1 人だけです。職員の登録・削除などには登録済みの職員の職員証による認証が要るため、" +
            "その職員証を紛失すると、画面の操作では誰も認証できなくなります。" +
            "紛失に備えて、もう 1 人以上の職員を登録してください。");
        StaffManageViewModel.SingleStaffNotice.Should().MatchRegex("してください。$");
        _viewModel.SingleStaffNoticeText.Should().Be(StaffManageViewModel.SingleStaffNotice,
            "画面と読み上げは同じ文言を表示する");
    }

    #endregion
}
