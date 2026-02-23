using FluentAssertions;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Models;
using Xunit;

namespace ICCardManager.Tests.Infrastructure.Sound
{
    /// <summary>
    /// SoundPlayer の音声ファイル選択ロジックをテストする。
    /// Issue #832: 職員証タッチ時に音声モードでも常にビープ音が選択されることを検証。
    /// </summary>
    public class SoundPlayerTests
    {
        /// <summary>
        /// SoundPlayer インスタンスを生成する。
        /// WAVファイルがなくても GetSoundFileName のロジックはテスト可能。
        /// </summary>
        private SoundPlayer CreateSut(SoundMode mode = SoundMode.Beep)
        {
            var player = new SoundPlayer();
            player.SoundMode = mode;
            return player;
        }

        #region SoundType.Notify のテスト（Issue #832）

        [Fact]
        public void GetSoundFileName_Notify_Beepモードでもビープ音を返す()
        {
            using var sut = CreateSut(SoundMode.Beep);
            var result = sut.GetSoundFileName(SoundType.Notify);
            result.Should().Be("lend.wav");
        }

        [Fact]
        public void GetSoundFileName_Notify_男性音声モードでもビープ音を返す()
        {
            using var sut = CreateSut(SoundMode.VoiceMale);
            var result = sut.GetSoundFileName(SoundType.Notify);
            result.Should().Be("lend.wav");
        }

        [Fact]
        public void GetSoundFileName_Notify_女性音声モードでもビープ音を返す()
        {
            using var sut = CreateSut(SoundMode.VoiceFemale);
            var result = sut.GetSoundFileName(SoundType.Notify);
            result.Should().Be("lend.wav");
        }

        [Fact]
        public void GetSoundFileName_Notify_Noneモードではnullを返す()
        {
            using var sut = CreateSut(SoundMode.None);
            var result = sut.GetSoundFileName(SoundType.Notify);
            result.Should().BeNull();
        }

        #endregion

        #region SoundType.Lend のテスト（音声モードで音声ファイルが選択されることの確認）

        [Fact]
        public void GetSoundFileName_Lend_Beepモードでビープ音を返す()
        {
            using var sut = CreateSut(SoundMode.Beep);
            var result = sut.GetSoundFileName(SoundType.Lend);
            result.Should().Be("lend.wav");
        }

        [Fact]
        public void GetSoundFileName_Lend_男性音声モードで男性音声を返す()
        {
            using var sut = CreateSut(SoundMode.VoiceMale);
            var result = sut.GetSoundFileName(SoundType.Lend);
            result.Should().Be("lend_male.wav");
        }

        [Fact]
        public void GetSoundFileName_Lend_女性音声モードで女性音声を返す()
        {
            using var sut = CreateSut(SoundMode.VoiceFemale);
            var result = sut.GetSoundFileName(SoundType.Lend);
            result.Should().Be("lend_female.wav");
        }

        #endregion

        #region SoundType.Return のテスト

        [Fact]
        public void GetSoundFileName_Return_男性音声モードで男性音声を返す()
        {
            using var sut = CreateSut(SoundMode.VoiceMale);
            var result = sut.GetSoundFileName(SoundType.Return);
            result.Should().Be("return_male.wav");
        }

        [Fact]
        public void GetSoundFileName_Return_女性音声モードで女性音声を返す()
        {
            using var sut = CreateSut(SoundMode.VoiceFemale);
            var result = sut.GetSoundFileName(SoundType.Return);
            result.Should().Be("return_female.wav");
        }

        #endregion

        #region Noneモードのテスト

        [Theory]
        [InlineData(SoundType.Lend)]
        [InlineData(SoundType.Return)]
        [InlineData(SoundType.Error)]
        [InlineData(SoundType.Warning)]
        [InlineData(SoundType.Notify)]
        public void GetSoundFileName_Noneモードでは全てnullを返す(SoundType soundType)
        {
            using var sut = CreateSut(SoundMode.None);
            var result = sut.GetSoundFileName(soundType);
            result.Should().BeNull();
        }

        #endregion

        #region Error/Warning のテスト（モード非依存）

        [Theory]
        [InlineData(SoundMode.Beep)]
        [InlineData(SoundMode.VoiceMale)]
        [InlineData(SoundMode.VoiceFemale)]
        public void GetSoundFileName_Error_全モードでerror_wavを返す(SoundMode mode)
        {
            using var sut = CreateSut(mode);
            var result = sut.GetSoundFileName(SoundType.Error);
            result.Should().Be("error.wav");
        }

        [Theory]
        [InlineData(SoundMode.Beep)]
        [InlineData(SoundMode.VoiceMale)]
        [InlineData(SoundMode.VoiceFemale)]
        public void GetSoundFileName_Warning_全モードでwarning_wavを返す(SoundMode mode)
        {
            using var sut = CreateSut(mode);
            var result = sut.GetSoundFileName(SoundType.Warning);
            result.Should().Be("warning.wav");
        }

        #endregion
    }
}
