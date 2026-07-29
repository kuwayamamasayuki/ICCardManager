using System;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace ICCardManager.Services
{
    /// <summary>
    /// WPF の <see cref="Clipboard"/> を用いた <see cref="IClipboardService"/> 実装（Issue #1690）
    /// </summary>
    public class WpfClipboardService : IClipboardService
    {
        private readonly ILogger<WpfClipboardService> _logger;

        public WpfClipboardService(ILogger<WpfClipboardService> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc/>
        public bool TrySetText(string text)
        {
            try
            {
                // null を渡すと ArgumentNullException になるため空文字へ寄せる
                Clipboard.SetText(text ?? string.Empty);
                return true;
            }
            catch (Exception ex)
            {
                // クリップボードは OS 全体で共有される資源で、他プロセス（リモートデスクトップの
                // クリップボード同期など）がロックしていると COMException / ExternalException で失敗する。
                // 技術的詳細はログへ逃がし、呼び出し側には成否だけを返す（Issue #1614）。
                _logger.LogWarning(ex, "クリップボードへの書き込みに失敗しました");
                return false;
            }
        }
    }
}
