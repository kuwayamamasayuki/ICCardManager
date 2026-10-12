#nullable enable
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using ICCardManager.Data.Repositories;
using ICCardManager.Infrastructure.CardReader;
using ICCardManager.Infrastructure.Sound;
using ICCardManager.Views.Dialogs;
using Microsoft.Extensions.Options;

namespace ICCardManager.Services
{
    /// <summary>
    /// 職員認証サービスの実装
    /// </summary>
    /// <remarks>
    /// Issue #429: 重要な操作の前に職員証タッチを必須とする
    /// </remarks>
    public class StaffAuthService : IStaffAuthService
    {
        private readonly IStaffRepository _staffRepository;
        private readonly ICardReader _cardReader;
        private readonly ISoundPlayer _soundPlayer;
        private readonly IMessenger _messenger;
        private readonly IOptions<AppOptions> _appOptions;
        private readonly ICurrentOperatorContext _operatorContext;

        public StaffAuthService(
            IStaffRepository staffRepository,
            ICardReader cardReader,
            ISoundPlayer soundPlayer,
            IMessenger messenger,
            IOptions<AppOptions> appOptions,
            ICurrentOperatorContext operatorContext)
        {
            _staffRepository = staffRepository;
            _cardReader = cardReader;
            _soundPlayer = soundPlayer;
            _messenger = messenger;
            _appOptions = appOptions;
            _operatorContext = operatorContext;
        }

        /// <inheritdoc/>
        public Task<StaffAuthResult?> RequestAuthenticationAsync(string operationDescription)
        {
            var dialog = new StaffAuthDialog(_staffRepository, _cardReader, _soundPlayer, _messenger, _appOptions)
            {
                Owner = Application.Current.MainWindow,
                OperationDescription = operationDescription
            };

            if (dialog.ShowDialog() == true && dialog.IsAuthenticated)
            {
                return Task.FromResult<StaffAuthResult?>(
                    CreateAuthenticatedResult(_operatorContext, dialog.AuthenticatedIdm!, dialog.AuthenticatedStaffName!));
            }

            return Task.FromResult<StaffAuthResult?>(null);
        }

        /// <summary>
        /// 認証に成功した職員の認証結果を作る。操作者セッションはまだ開始しない。
        /// </summary>
        /// <remarks>
        /// Issue #1265: 後続の OperationLogger によるログ記録は、操作者セッションから操作者を解決する。
        /// Issue #2260: セッションは呼び出し元の画面が <see cref="StaffAuthResult.Enter"/> でその画面の非同期の流れに
        /// 開始し、結果の破棄で終わる。ここ（認証サービス）で開始しないのは、セッションが非同期の流れ
        /// （<c>AsyncLocal</c>）に結び付いており、<c>async</c> の内側で開始した値は呼び出し元へ戻らないため
        /// （現在の本メソッドの呼び出し元は async ではないが、そうなった時点で黙って効かなくなる）。
        /// 認証ダイアログ（モーダル）を伴わない部分を切り出し、単体テストから本物のセッションを検証できるようにしている。
        /// </remarks>
        internal static StaffAuthResult CreateAuthenticatedResult(
            ICurrentOperatorContext operatorContext, string idm, string staffName)
        {
            return new StaffAuthResult(idm, staffName, operatorContext);
        }
    }
}
