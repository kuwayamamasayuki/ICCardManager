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
    }
}
