using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using ICCardManager.Common;
using Xunit;

namespace ICCardManager.Tests.Common;

/// <summary>
/// FolderWriteAccessProbe の単体テスト（Issue #1690）
/// </summary>
/// <remarks>
/// Directory.Exists だけでは「見えるが書けない」読み取り専用共有を検出できないため、
/// 実際に一時ファイルを作って書き込み可否を確かめる方式を採っている。
/// 本テストでは実フォルダに対して動作させ、
/// 「診断がゴミファイルを残さない」ことも併せて固定する。
/// </remarks>
public class FolderWriteAccessProbeTests : IDisposable
{
    private readonly string _tempFolder;

    public FolderWriteAccessProbeTests()
    {
        _tempFolder = Path.Combine(Path.GetTempPath(), "iccard_probe_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempFolder);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempFolder))
                Directory.Delete(_tempFolder, recursive: true);
        }
        catch (IOException)
        {
            // テスト後始末の失敗はテスト結果に影響させない
        }
    }

    [Fact]
    public void Probe_WithWritableFolder_ReturnsWritable()
    {
        FolderWriteAccessProbe.Probe(_tempFolder).Should().Be(FolderWriteAccess.Writable);
    }

    [Fact]
    public void Probe_WithWritableFolder_LeavesNoProbeFileBehind()
    {
        FolderWriteAccessProbe.Probe(_tempFolder);

        // FileOptions.DeleteOnClose により OS が削除するため、痕跡が残ってはならない
        Directory.GetFiles(_tempFolder)
            .Select(Path.GetFileName)
            .Should().NotContain(name => name.StartsWith(FolderWriteAccessProbe.ProbeFileNamePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_CalledTwice_SucceedsBothTimes()
    {
        // 一時ファイル名が固定だと 2 回目が「既に存在する」で失敗する。
        // GUID による一意化が効いていることを固定する。
        FolderWriteAccessProbe.Probe(_tempFolder).Should().Be(FolderWriteAccess.Writable);
        FolderWriteAccessProbe.Probe(_tempFolder).Should().Be(FolderWriteAccess.Writable);
    }

    [Fact]
    public void Probe_WithNonExistentFolder_ReturnsFolderNotFound()
    {
        var missing = Path.Combine(_tempFolder, "not_created_" + Guid.NewGuid().ToString("N"));

        FolderWriteAccessProbe.Probe(missing).Should().Be(FolderWriteAccess.FolderNotFound);
    }

    [Fact]
    public void Probe_WithUncPathToNonExistentShare_ReturnsFolderNotFound()
    {
        // 共有モードでネットワークが切断された・共有名が変わった状況を模す。
        // DriveInfo と異なり例外にならず、判定値として返ることを確認する。
        // Issue #2107: 存在しないホスト名だと名前解決を待って数秒止まり得るため、
        // 名前解決の要らないループバックアドレス上の存在しない共有名を使う。
        var unc = @"\\127.0.0.1\iccard-nonexistent-share-" + Guid.NewGuid().ToString("N");

        FolderWriteAccessProbe.Probe(unc).Should().Be(FolderWriteAccess.FolderNotFound);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Probe_WithEmptyPath_ReturnsPathNotSpecified(string path)
    {
        FolderWriteAccessProbe.Probe(path).Should().Be(FolderWriteAccess.PathNotSpecified);
    }

    [Fact]
    public void Probe_WithInvalidPathCharacters_ReturnsFailureWithoutThrowing()
    {
        // 設定ファイルを手編集して壊れたパスが入るケース。例外を外へ漏らさないことが要件
        var invalid = "C:\\invalid\0path";

        var act = () => FolderWriteAccessProbe.Probe(invalid);

        act.Should().NotThrow();
        FolderWriteAccessProbe.Probe(invalid).Should().NotBe(FolderWriteAccess.Writable);
    }

    /// <summary>
    /// Issue #2107: 「見えるが書けない」フォルダを AccessDenied と判定すること
    /// </summary>
    /// <remarks>
    /// <para>
    /// この検査の主目的（<see cref="Directory.Exists"/> では検出できない読み取り専用共有の検出）そのものに
    /// テストが無かった。<c>catch (UnauthorizedAccessException)</c> を消しても末尾の <c>catch (Exception)</c> が
    /// <see cref="FolderWriteAccess.Failed"/> を返すため、ほかのテストは緑のまま、接続診断の文言が
    /// 「権限がありません」から原因不明の失敗へ変わる。
    /// </para>
    /// <para>
    /// 現在のユーザーに「ファイルの作成」だけを拒否する ACE を付けて再現する。一覧・読み取りは許すので
    /// フォルダは見え続ける（共有の読み取り専用アクセス許可と同じ状態）。
    /// </para>
    /// </remarks>
    [Fact]
    public void Probe_WithFolderVisibleButNotWritable_ReturnsAccessDenied()
    {
        var readOnlyFolder = Path.Combine(_tempFolder, "read_only");
        Directory.CreateDirectory(readOnlyFolder);
        var denyCreate = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData,
            AccessControlType.Deny);
        var directory = new DirectoryInfo(readOnlyFolder);
        var security = directory.GetAccessControl();
        security.AddAccessRule(denyCreate);
        directory.SetAccessControl(security);

        try
        {
            Directory.Exists(readOnlyFolder).Should().BeTrue("前提: フォルダは見えているべき");

            FolderWriteAccessProbe.Probe(readOnlyFolder).Should().Be(FolderWriteAccess.AccessDenied);
        }
        finally
        {
            // 後始末（Dispose のフォルダ削除）を妨げないよう、付けた ACE を外す
            security = directory.GetAccessControl();
            security.RemoveAccessRule(denyCreate);
            directory.SetAccessControl(security);
        }
    }
}
