using System;
using System.IO;
using FluentAssertions;
using ICCardManager.Data;
using Xunit;

namespace ICCardManager.Tests.Data
{
    /// <summary>
    /// Issue #1559: 共有モード判定ロジック（UNC / マップドネットワークドライブのみ共有モード扱い）の単体テスト。
    /// </summary>
    public class DbContextSharedModeDetectionTests : IDisposable
    {
        private readonly string _tempDir;

        public DbContextSharedModeDetectionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempDir, true); } catch { /* best-effort cleanup */ }
        }

        [Fact]
        public void IsSharedMode_DefaultPath_IsFalse()
        {
            using var ctx = new DbContext(databasePath: null);
            ctx.IsSharedMode.Should().BeFalse("デフォルト（null）パスはローカルモード扱い");
        }

        [Fact]
        public void IsSharedMode_LocalAbsolutePath_IsFalse()
        {
            var localPath = Path.Combine(_tempDir, "iccard.db");
            using var ctx = new DbContext(databasePath: localPath);
            ctx.IsSharedMode.Should().BeFalse("ローカルフォルダのフルパス指定は共有モード扱いにしない (Issue #1559)");
        }

        [Fact]
        public void IsSharedMode_UncPath_IsTrue()
        {
            using var ctx = new DbContext(databasePath: @"\\server\share\iccard.db");
            ctx.IsSharedMode.Should().BeTrue("UNCパス指定時は共有モード");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsNetworkDrive_NullOrWhitespace_ReturnsFalse(string path)
        {
            DbContext.IsNetworkDrive(path).Should().BeFalse();
        }

        [Fact]
        public void IsNetworkDrive_LocalPath_ReturnsFalse()
        {
            var localPath = Path.Combine(_tempDir, "iccard.db");
            DbContext.IsNetworkDrive(localPath).Should().BeFalse("ローカル一時フォルダはネットワークドライブではない");
        }

        [Fact]
        public void IsNetworkDrive_UncPath_ReturnsFalse()
        {
            DbContext.IsNetworkDrive(@"\\server\share\iccard.db").Should()
                .BeFalse("UNCは IsUncPath 側で判定するため IsNetworkDrive は false を返す");
        }

        // ── Issue #1597: 共有モード判定の共通メソッド IsSharedModePath ──
        // App.xaml.cs のキャッシュTTL短縮判定と DbContext のコンストラクタ判定が
        // 同一基準（UNC／マップドネットワークドライブ）を共有することを固定する。

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsSharedModePath_NullOrWhitespace_ReturnsFalse(string path)
        {
            DbContext.IsSharedModePath(path).Should()
                .BeFalse("パス未指定（デフォルトローカルDB）は共有モードではない");
        }

        [Fact]
        public void IsSharedModePath_LocalAbsolutePath_ReturnsFalse()
        {
            var localPath = Path.Combine(_tempDir, "iccard.db");
            DbContext.IsSharedModePath(localPath).Should()
                .BeFalse("ローカルフルパス指定は共有モード扱いにしない (Issue #1597: パス指定の有無だけでTTL短縮しない)");
        }

        [Fact]
        public void IsSharedModePath_UncPath_ReturnsTrue()
        {
            DbContext.IsSharedModePath(@"\\server\share\iccard.db").Should()
                .BeTrue("UNCパス指定時は共有モード（キャッシュTTL短縮対象）");
        }

        [Fact]
        public void IsSharedModePath_MatchesConstructorIsSharedMode_ForLocalPath()
        {
            // App.xaml.cs と DbContext が同じ判定結果になることを保証（#1559 の修正漏れ #1597 再発防止）
            var localPath = Path.Combine(_tempDir, "iccard.db");
            using var ctx = new DbContext(databasePath: localPath);
            DbContext.IsSharedModePath(localPath).Should().Be(ctx.IsSharedMode,
                "キャッシュTTL判定とコンストラクタの IsSharedMode は同一ロジックでなければならない");
        }

        [Fact]
        public void IsSharedModePath_MatchesConstructorIsSharedMode_ForUncPath()
        {
            const string uncPath = @"\\server\share\iccard.db";
            using var ctx = new DbContext(databasePath: uncPath);
            DbContext.IsSharedModePath(uncPath).Should().Be(ctx.IsSharedMode,
                "UNCパスでもキャッシュTTL判定と IsSharedMode は一致する");
        }

        // ── Issue #1605: IsNetworkDrive の正方向分岐（DriveType.Network → true）──
        // 実ネットワークドライブなしには検証できなかった「Z:\ がネットワークドライブなら共有モード」
        // という #1559 / #1584 の本丸を、DriveType 解決を注入する internal seam で固定する。
        // 注入リゾルバはルート文字列（例: "Z:\\"）を受け取り DriveType を返す。

        [Fact]
        public void IsNetworkDrive_MappedNetworkDrive_ReturnsTrue()
        {
            // ルートが Network ドライブと解決されるケース（正方向）
            string capturedRoot = null;
            Func<string, DriveType> resolver = root =>
            {
                capturedRoot = root;
                return DriveType.Network;
            };

            DbContext.IsNetworkDrive(@"Z:\share\iccard.db", resolver).Should()
                .BeTrue("DriveType.Network のマップドドライブは共有モード対象 (Issue #1559 / #1605)");
            capturedRoot.Should().Be(@"Z:\", "Path.GetPathRoot で得たドライブルートがリゾルバに渡る");
        }

        [Theory]
        [InlineData(DriveType.Fixed)]
        [InlineData(DriveType.Removable)]
        [InlineData(DriveType.Ram)]
        [InlineData(DriveType.CDRom)]
        [InlineData(DriveType.NoRootDirectory)]
        [InlineData(DriveType.Unknown)]
        public void IsNetworkDrive_NonNetworkDriveTypes_ReturnFalse(DriveType driveType)
        {
            Func<string, DriveType> resolver = _ => driveType;

            DbContext.IsNetworkDrive(@"X:\data\iccard.db", resolver).Should()
                .BeFalse($"DriveType.{driveType} はネットワークドライブではないためローカル扱い");
        }

        [Fact]
        public void IsNetworkDrive_UncPath_DoesNotInvokeResolver()
        {
            // UNC は早期 false（責務分離）。Network を返すリゾルバを渡しても呼ばれず false のまま。
            var resolverInvoked = false;
            Func<string, DriveType> resolver = _ =>
            {
                resolverInvoked = true;
                return DriveType.Network;
            };

            DbContext.IsNetworkDrive(@"\\server\share\iccard.db", resolver).Should()
                .BeFalse("UNCは IsUncPath 側の責務のため IsNetworkDrive は false を返す");
            resolverInvoked.Should().BeFalse("UNC は DriveType 解決前に早期 return される");
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsNetworkDrive_NullOrWhitespace_DoesNotInvokeResolver(string path)
        {
            var resolverInvoked = false;
            Func<string, DriveType> resolver = _ =>
            {
                resolverInvoked = true;
                return DriveType.Network;
            };

            DbContext.IsNetworkDrive(path, resolver).Should().BeFalse();
            resolverInvoked.Should().BeFalse("null/空白は DriveType 解決前に早期 return される");
        }

        [Fact]
        public void IsNetworkDrive_ResolverThrows_FallsBackToFalse()
        {
            // 未マウントドライブ等で DriveInfo が例外を投げる状況を模擬。catch で false にフォールバック。
            Func<string, DriveType> resolver = _ => throw new ArgumentException("drive not ready");

            DbContext.IsNetworkDrive(@"Q:\share\iccard.db", resolver).Should()
                .BeFalse("DriveType 解決で例外が出た場合はローカル扱い（false）にフォールバックする");
        }

        [Fact]
        public void IsSharedModePath_MappedNetworkDrive_ReturnsTrue()
        {
            // 本丸: 「Z:\ がネットワークドライブなら共有モード（IsSharedMode=true 相当）」を固定。
            Func<string, DriveType> resolver = _ => DriveType.Network;

            DbContext.IsSharedModePath(@"Z:\share\iccard.db", resolver).Should()
                .BeTrue("マップドネットワークドライブ指定時は共有モード (Issue #1559 / #1605)");
        }

        [Fact]
        public void IsSharedModePath_MappedLocalDrive_ReturnsFalse()
        {
            // 同じドライブレター形式でも Fixed ドライブなら共有モードにしない。
            Func<string, DriveType> resolver = _ => DriveType.Fixed;

            DbContext.IsSharedModePath(@"Z:\share\iccard.db", resolver).Should()
                .BeFalse("ローカル（Fixed）ドライブ上のフルパスは共有モード扱いにしない (Issue #1597)");
        }

        [Fact]
        public void IsSharedModePath_UncPath_WithResolver_ReturnsTrueWithoutInvokingResolver()
        {
            // UNC は IsUncPath が先に true を返すため、Fixed を返すリゾルバでも共有モードになる。
            var resolverInvoked = false;
            Func<string, DriveType> resolver = _ =>
            {
                resolverInvoked = true;
                return DriveType.Fixed;
            };

            DbContext.IsSharedModePath(@"\\server\share\iccard.db", resolver).Should()
                .BeTrue("UNCパスは DriveType に関わらず共有モード");
            resolverInvoked.Should().BeFalse("UNC は短絡評価で IsNetworkDrive まで到達しない");
        }
    }
}
