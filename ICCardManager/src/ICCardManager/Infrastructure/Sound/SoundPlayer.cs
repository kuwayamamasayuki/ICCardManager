using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Media;
using ICCardManager.Models;
using System.Threading;

namespace ICCardManager.Infrastructure.Sound
{
/// <summary>
    /// 効果音再生サービス
    /// </summary>
    public class SoundPlayer : ISoundPlayer
    {
        private readonly Dictionary<string, System.Media.SoundPlayer?> _players;
        private bool _disposed;
        private SoundMode _soundMode = SoundMode.Beep;

        /// <summary>
        /// 効果音ファイルのベースパス
        /// </summary>
        private string SoundsBasePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "Sounds");

        /// <summary>
        /// 音声を有効にするかどうか（マスタースイッチ）
        /// </summary>
        /// <remarks>
        /// IsEnabled: 一時的に全音声を無効化するためのスイッチ（デバッグ用途など）
        /// SoundMode.None: ユーザー設定として恒久的に無音を選択
        /// 両方とも音声を無効化できるが、用途が異なる
        /// </remarks>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 音声モード（ユーザー設定）
        /// </summary>
        /// <remarks>
        /// 全ての音声ファイルは初期化時にロード済みのため、
        /// モード変更時の再読み込みは不要
        /// </remarks>
        public SoundMode SoundMode
        {
            get => _soundMode;
            set => _soundMode = value;
        }

        /// <summary>
        /// 効果音ファイル名の定義
        /// </summary>
        private static class SoundFiles
        {
            // 効果音（ピッ/ピピッ）
            public const string LendBeep = "lend.wav";
            public const string ReturnBeep = "return.wav";

            // 男性音声
            public const string LendMale = "lend_male.wav";
            public const string ReturnMale = "return_male.wav";

            // 女性音声
            public const string LendFemale = "lend_female.wav";
            public const string ReturnFemale = "return_female.wav";

            // エラー・警告（モード共通）
            public const string Error = "error.wav";
            public const string Warning = "warning.wav";
        }

        public SoundPlayer()
        {
            _players = new Dictionary<string, System.Media.SoundPlayer?>();

            // プレイヤーを事前にロード
            LoadPlayers();
        }

        /// <summary>
        /// 効果音プレイヤーをロード
        /// </summary>
        private void LoadPlayers()
        {
            // すべての音声ファイルをロード
            var allFiles = new[]
            {
                SoundFiles.LendBeep,
                SoundFiles.ReturnBeep,
                SoundFiles.LendMale,
                SoundFiles.ReturnMale,
                SoundFiles.LendFemale,
                SoundFiles.ReturnFemale,
                SoundFiles.Error,
                SoundFiles.Warning
            };

            foreach (var fileName in allFiles)
            {
                LoadPlayer(fileName);
            }
        }

        /// <summary>
        /// 個別のプレイヤーをロード
        /// </summary>
        private void LoadPlayer(string fileName)
        {
            var filePath = Path.Combine(SoundsBasePath, fileName);
            if (File.Exists(filePath))
            {
                try
                {
                    var player = new System.Media.SoundPlayer(filePath);
                    player.Load();
                    _players[fileName] = player;
                }
                catch
                {
                    _players[fileName] = null;
                }
            }
            else
            {
                _players[fileName] = null;
            }
        }

        /// <summary>
        /// 現在のモードに応じた音声ファイル名を取得
        /// </summary>
        internal string GetSoundFileName(SoundType soundType)
        {
            // Noneモードの場合は再生しない
            if (_soundMode == SoundMode.None)
            {
                return null;
            }

            return soundType switch
            {
                SoundType.Lend => _soundMode switch
                {
                    SoundMode.Beep => SoundFiles.LendBeep,
                    SoundMode.VoiceMale => SoundFiles.LendMale,
                    SoundMode.VoiceFemale => SoundFiles.LendFemale,
                    _ => SoundFiles.LendBeep
                },
                SoundType.Return => _soundMode switch
                {
                    SoundMode.Beep => SoundFiles.ReturnBeep,
                    SoundMode.VoiceMale => SoundFiles.ReturnMale,
                    SoundMode.VoiceFemale => SoundFiles.ReturnFemale,
                    _ => SoundFiles.ReturnBeep
                },
                SoundType.Error => SoundFiles.Error,
                SoundType.Warning => SoundFiles.Warning,
                // 通知音は音声モードに関係なく常にビープ音
                SoundType.Notify => SoundFiles.LendBeep,
                _ => null
            };
        }

        /// <inheritdoc/>
        public void Play(SoundType soundType)
        {
            if (!IsEnabled || _soundMode == SoundMode.None)
            {
                return;
            }

            var fileName = GetSoundFileName(soundType);
            if (fileName == null)
            {
                return;
            }

            if (_players.TryGetValue(fileName, out var player) && player != null)
            {
                try
                {
                    player.Play();
                }
                catch
                {
                    // 再生失敗時はシステムビープで代替
                    PlaySystemBeep(soundType);
                }
            }
            else
            {
                // ファイルがない場合はシステムビープで代替
                PlaySystemBeep(soundType);
            }
        }

        /// <inheritdoc/>
        public Task PlayAsync(SoundType soundType)
        {
            return Task.Run(() => Play(soundType));
        }

        /// <summary>
        /// システムビープ音を再生（フォールバック用）
        /// </summary>
        private void PlaySystemBeep(SoundType soundType)
        {
            try
            {
                switch (soundType)
                {
                    case SoundType.Lend:
                        // 貸出：短い高音
                        Console.Beep(1000, 100);
                        break;

                    case SoundType.Return:
                        // 返却：短い高音×2
                        Console.Beep(1000, 100);
                        Thread.Sleep(50);
                        Console.Beep(1000, 100);
                        break;

                    case SoundType.Error:
                        // エラー：長い低音
                        Console.Beep(500, 500);
                        break;

                    case SoundType.Warning:
                        // 警告：中音
                        Console.Beep(750, 200);
                        break;

                    case SoundType.Notify:
                        // 通知：短い高音（貸出と同じ）
                        Console.Beep(1000, 100);
                        break;
                }
            }
            catch
            {
                // Console.Beepが使えない環境では無視
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    foreach (var player in _players.Values)
                    {
                        player?.Dispose();
                    }
                    _players.Clear();
                }
                _disposed = true;
            }
        }
    }
}
