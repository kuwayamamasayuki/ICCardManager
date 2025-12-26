using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PCSC;
using PCSC.Monitoring;
using PcscICardReader = PCSC.ICardReader;

namespace ICCardManager.Infrastructure.CardReader
{
/// <summary>
    /// PC/SCライブラリの抽象化インターフェース。
    /// テスト容易性のためにPCSCライブラリ依存を分離します。
    /// </summary>
    /// <remarks>
    /// <para>
    /// このインターフェースは以下を提供します：
    /// </para>
    /// <list type="bullet">
    /// <item><description>カードリーダーの列挙（<see cref="GetReaders"/>）</description></item>
    /// <item><description>カードモニターの作成（<see cref="CreateMonitor"/>）</description></item>
    /// <item><description>リーダーへの接続（<see cref="ConnectReader"/>）</description></item>
    /// </list>
    /// <para>
    /// 本番環境では<see cref="DefaultPcScProvider"/>を使用し、
    /// テスト時はモックプロバイダーを注入します。
    /// </para>
    /// </remarks>
    public interface IPcScProvider : IDisposable
    {
        /// <summary>
        /// 接続可能なカードリーダーの一覧を取得します。
        /// </summary>
        /// <returns>リーダー名の配列。リーダーがない場合は空配列またはnull</returns>
        string[] GetReaders();

        /// <summary>
        /// カードモニターを作成します。
        /// </summary>
        /// <returns>カードの挿入/取り外しを監視するモニター</returns>
        ISCardMonitor CreateMonitor();

        /// <summary>
        /// 指定したリーダーに接続します。
        /// </summary>
        /// <param name="readerName">リーダー名</param>
        /// <param name="shareMode">共有モード</param>
        /// <param name="protocol">通信プロトコル</param>
        /// <returns>PCSC カードリーダー接続</returns>
        PcscICardReader ConnectReader(string readerName, SCardShareMode shareMode, SCardProtocol protocol);
    }

    /// <summary>
    /// 実際のPC/SCライブラリを使用するプロバイダー実装
    /// </summary>
    public class DefaultPcScProvider : IPcScProvider
    {
        private readonly ISCardContext _context;
        private bool _disposed;

        /// <summary>
        /// 新しいDefaultPcScProviderを初期化します。
        /// </summary>
        public DefaultPcScProvider()
        {
            _context = ContextFactory.Instance.Establish(SCardScope.System);
        }

        /// <inheritdoc/>
        public string[] GetReaders()
        {
            return _context.GetReaders();
        }

        /// <inheritdoc/>
        public ISCardMonitor CreateMonitor()
        {
            return MonitorFactory.Instance.Create(SCardScope.System);
        }

        /// <inheritdoc/>
        public PcscICardReader ConnectReader(string readerName, SCardShareMode shareMode, SCardProtocol protocol)
        {
            return _context.ConnectReader(readerName, shareMode, protocol);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!_disposed)
            {
                _context.Dispose();
                _disposed = true;
            }
        }
    }
}
