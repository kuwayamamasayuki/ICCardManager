using System;
using DebugDataViewer;
using FluentAssertions;
using Xunit;

namespace ICCardManager.Tests.Tools
{
    /// <summary>
    /// DebugDataViewer のデータベースパス解決（<see cref="DatabasePathResolver"/>）の回帰（Issue #2012）。
    /// </summary>
    /// <remarks>
    /// 「共有 DB を開く側」と「従来の経路を塞いでいない側」を対で置く。
    /// 前者だけだと、常に設定ファイルの値を返す実装でも緑になる。
    /// </remarks>
    [Trait("Category", "Unit")]
    public class DatabasePathResolverTests
    {
        private static readonly string[] NoArgs = { @"C:\tools\DebugDataViewer.exe" };
        private const string ExeDir = @"C:\tools";

        /// <summary>どのファイルも存在しない環境</summary>
        private static readonly Func<string, bool> NothingExists = _ => false;

        [Fact]
        public void 設定ファイルに共有DBのUNCパスがあればそれを開くこと()
        {
            // Issue #2012 の欠陥そのもの。是正前は既定パスのローカル DB を開いていた
            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => @"\\server\share\iccard.db", NothingExists);

            result.Path.Should().Be(@"\\server\share\iccard.db");
            result.Source.Should().Be(DatabasePathSource.ConfigFile);
            result.RejectedConfiguredPath.Should().BeNull();
        }

        [Fact]
        public void 共有DBが今つながっていなくても設定ファイルの値を採用すること()
        {
            // 到達性で判定すると、ネットワークが一時的に切れているだけの正当な共有 DB パスを
            // 無効と判定して黙ってローカルへ切り替わる（#1599 と同じ判断）。
            // NothingExists はどのファイルも存在しない状況を表す
            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => @"Z:\share\iccard.db", NothingExists);

            result.Path.Should().Be(@"Z:\share\iccard.db");
            result.Source.Should().Be(DatabasePathSource.ConfigFile);
        }

        [Fact]
        public void 設定ファイルが未設定なら既定パスを開くこと()
        {
            // 対の表明: 設定ファイル対応を足したことで従来の既定パス経路を壊していないこと
            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => null, NothingExists);

            result.Path.Should().Be(DatabasePathResolver.GetDefaultDatabasePath());
            result.Source.Should().Be(DatabasePathSource.Default);
        }

        [Fact]
        public void 設定ファイルが空白なら既定パスを開くこと()
        {
            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => "   ", NothingExists);

            result.Source.Should().Be(DatabasePathSource.Default);
        }

        [Fact]
        public void コマンドライン引数は設定ファイルより優先すること()
        {
            // 対の表明: 開発者が明示的に指定した DB を設定ファイルで上書きしないこと
            var args = new[] { @"C:\tools\DebugDataViewer.exe", @"C:\work\snapshot.db" };

            var result = DatabasePathResolver.Resolve(
                args, ExeDir, () => @"\\server\share\iccard.db", p => p == @"C:\work\snapshot.db");

            result.Path.Should().Be(@"C:\work\snapshot.db");
            result.Source.Should().Be(DatabasePathSource.CommandLine);
        }

        [Fact]
        public void 設定ファイルは実行ファイルと同じフォルダーのDBより優先すること()
        {
            // 本体が実際に開いている DB を見るのが目的なので、
            // たまたま横に置かれた iccard.db より設定ファイルが強い
            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => @"\\server\share\iccard.db", _ => true);

            result.Source.Should().Be(DatabasePathSource.ConfigFile);
        }

        [Fact]
        public void 設定ファイルが未設定なら実行ファイルと同じフォルダーのDBを開くこと()
        {
            // 対の表明: 持ち出した DB を横に置いて見る従来の運用を塞いでいないこと
            var localDb = System.IO.Path.Combine(ExeDir, "iccard.db");

            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => null, p => p == localDb);

            result.Path.Should().Be(localDb);
            result.Source.Should().Be(DatabasePathSource.ExecutableDirectory);
        }

        [Fact]
        public void 形式が不正な設定値は採用せず元の値を控えること()
        {
            // 相対パスは PathValidator.ValidatePathFormat が弾く（#1599）。
            // 黙って既定へ落ちると本体と違う DB を見ていることに気付けないため、
            // 元の値を返して画面で知らせられるようにする
            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => @"..\iccard.db", NothingExists);

            result.Source.Should().Be(DatabasePathSource.Default);
            result.RejectedConfiguredPath.Should().Be(@"..\iccard.db");
        }

        [Fact]
        public void 設定ファイルの読み取りで例外が出ても既定パスで起動できること()
        {
            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => throw new UnauthorizedAccessException(), NothingExists);

            result.Source.Should().Be(DatabasePathSource.Default);
        }

        [Theory]
        [InlineData(DatabasePathSource.ConfigFile, "database_config.txt")]
        [InlineData(DatabasePathSource.Default, "既定の保存先")]
        public void 解決元のラベルを画面へ出せること(DatabasePathSource source, string expectedLabel)
        {
            // ラベルは「本体と違う DB を見ている」ことに気付くための唯一の手掛かりなので、
            // 経路が区別できることを表明する
            var configured = source == DatabasePathSource.ConfigFile
                ? @"\\server\share\iccard.db"
                : null;

            var result = DatabasePathResolver.Resolve(
                NoArgs, ExeDir, () => configured, NothingExists);

            result.SourceLabel.Should().Be(expectedLabel);
        }
    }
}
