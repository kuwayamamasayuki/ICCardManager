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
                // Issue #1265: 認証成功時に操作者コンテキストを更新し、
                // 後続の OperationLogger によるログ記録で使用する。
                _operatorContext.BeginSession(dialog.AuthenticatedIdm!, dialog.AuthenticatedStaffName!);

                return Task.FromResult<StaffAuthResult?>(new StaffAuthResult
                {
                    Idm = dialog.AuthenticatedIdm!,
                    StaffName = dialog.AuthenticatedStaffName!
                });
            }

            return Task.FromResult<StaffAuthResult?>(null);
        }
    }
}
