using System;
using ICCardManager.Infrastructure.Timing;

namespace ICCardManager.Services
{
    /// <summary>
    /// 現在認証されている操作者のセッション情報を保持するコンテキスト。
    /// Issue #1265: 監査ログ (operation_log) の operator_idm / operator_name を
    /// 呼び出し元引数からではなく、このコンテキストから一元的に取得することで
    /// 内部者による監査ログなりすましを防止する。
    /// </summary>
    /// <remarks>
    /// 想定ライフサイクル: Singleton。StaffAuthService が認証成功時に BeginSession を呼び、
    /// OperationLogger が記録時に CurrentIdm / CurrentName を読む。
    /// セッションは指定時間経過で自動失効し、次回ログ記録時には GUI 操作として扱われる。
    /// </remarks>
    public interface ICurrentOperatorContext
    {
        /// <summary>現在のセッションが有効な場合の操作者 IDm。無効な場合は null。</summary>
        string? CurrentIdm { get; }

        /// <summary>現在のセッションが有効な場合の操作者氏名。無効な場合は null。</summary>
        string? CurrentName { get; }

        /// <summary>有効なセッションが存在するか（未期限切れ）。</summary>
        bool HasSession { get; }

        /// <summary>
        /// 認証セッションを開始する（または延長する）。StaffAuthService のみが呼び出すべき。
        /// </summary>
        /// <param name="idm">認証された操作者の IDm（16桁16進）</param>
        /// <param name="name">認証された操作者の氏名</param>
        void BeginSession(string idm, string name);

        /// <summary>
        /// 現在のセッションを明示的に終了する。
        /// </summary>
        void ClearSession();
    }

    /// <summary>
    /// ICurrentOperatorContext の既定実装。シングルトンとして登録されることを前提とする。
    /// </summary>
    public class CurrentOperatorContext : ICurrentOperatorContext
    {
        private readonly object _lock = new object();
        private readonly ISystemClock _clock;
        private readonly TimeSpan _sessionDuration;
        private string? _idm;
        private string? _name;
        private DateTime _expiresAt;

        /// <summary>
        /// セッションの既定有効期間（5分）。職員証タッチから実操作完了までの時間を想定。
        /// </summary>
        public static readonly TimeSpan DefaultSessionDuration = TimeSpan.FromMinutes(5);

        public CurrentOperatorContext(ISystemClock clock)
            : this(clock, DefaultSessionDuration)
        {
        }

        /// <summary>
        /// テスト目的でセッション長を差し替えるためのコンストラクタ。
        /// </summary>
        public CurrentOperatorContext(ISystemClock clock, TimeSpan sessionDuration)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (sessionDuration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionDuration), "セッション有効期間は正の値である必要があります。");
            }
            _sessionDuration = sessionDuration;
        }

        public bool HasSession
        {
            get
            {
                lock (_lock)
                {
                    return _idm != null && _clock.Now < _expiresAt;
                }
            }
        }

        public string? CurrentIdm
        {
            get
            {
                lock (_lock)
                {
                    return (_idm != null && _clock.Now < _expiresAt) ? _idm : null;
                }
            }
        }

        public string? CurrentName
        {
            get
            {
                lock (_lock)
                {
                    return (_idm != null && _clock.Now < _expiresAt) ? _name : null;
                }
            }
        }

        public void BeginSession(string idm, string name)
        {
            if (string.IsNullOrEmpty(idm))
            {
                throw new ArgumentException("IDm は必須です。", nameof(idm));
            }
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("氏名は必須です。", nameof(name));
            }

            lock (_lock)
            {
                _idm = idm;
                _name = name;
                _expiresAt = _clock.Now + _sessionDuration;
            }
        }

        public void ClearSession()
        {
            lock (_lock)
            {
                _idm = null;
                _name = null;
                _expiresAt = DateTime.MinValue;
            }
        }
    }
}
