using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Crypto;
using Ciphernote.DataAccess.Models;
using Ciphernote.Extensions;
using Ciphernote.Logging;
using Ciphernote.Net;
using Ciphernote.Resources;
using Ciphernote.Services;
using FluentValidation;
using ReactiveUI;
using Splat;

namespace Ciphernote.ViewModels
{
    public class OptionsViewModel : ViewModelBase
    {
        public OptionsViewModel(IComponentContext ctx, ICoreStrings coreStrings, 
            IAppCoreSettings appSettings, CryptoService cryptoService, 
            SyncService syncService, AccountService accountService) : base(ctx)
        {
            this.coreStrings = coreStrings;
            this.appSettings = appSettings;
            this.cryptoService = cryptoService;
            this.syncService = syncService;
            this.accountService = accountService;

            Email = cryptoService.Email;
            AccountKeyString = CryptoService.FormatAccountKey(cryptoService.AccountKey);

            UnlockSecurityCommand = ReactiveCommand.Create(ExecuteUnlockSecurity,
                this.WhenAny(x => x.Password, x => !string.IsNullOrEmpty(x.Value)));
        }

        private readonly ICoreStrings coreStrings;
        private readonly IAppCoreSettings appSettings;
        private readonly CryptoService cryptoService;
        private readonly SyncService syncService;
        private readonly AccountService accountService;

        private readonly double[] fontSizes =
        {
            9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 48
        };

        public void Init()
        {
            UpdateAccountInfo();
        }

        public void RestoreDefaults()
        {
        }

        #region Account Options

        public string Email { get; }

        private string password;

        public string Password
        {
            get { return password; }
            set { this.RaiseAndSetIfChanged(ref password, value); }
        }

        private string accountStatus;

        public string AccountStatus
        {
            get { return accountStatus; }
            set { this.RaiseAndSetIfChanged(ref accountStatus, value); }
        }

        private string subscriptionExpires;

        public string SubscriptionExpires
        {
            get { return subscriptionExpires; }
            set { this.RaiseAndSetIfChanged(ref subscriptionExpires, value); }
        }

        public string AccountKeyString { get; }

        private bool isSecurityUnlocked;

        public bool IsSecurityUnlocked
        {
            get { return isSecurityUnlocked; }
            set { this.RaiseAndSetIfChanged(ref isSecurityUnlocked, value); }
        }

        private bool isLoading;

        public bool IsLoading
        {
            get { return isLoading; }
            set { this.RaiseAndSetIfChanged(ref isLoading, value); }
        }

        public ReactiveCommand UnlockSecurityCommand { get; }

        private void ExecuteUnlockSecurity()
        {
            IsSecurityUnlocked = cryptoService.ValidatePassword(Password);
        }

        #endregion // Account Options

        #region Editor Options

        public double[] FontSizes => fontSizes;

        #endregion // Editor Options

        private async void UpdateAccountInfo()
        {
            try
            {
                IsLoading = true;

                var info = await accountService.GetAccountDetails();

                switch (info.SubscriptionType)
                {
                    case SubscriptionType.None:
                        AccountStatus = coreStrings.AccountStatusNone;
                        break;
                    case SubscriptionType.Trial:
                        AccountStatus = coreStrings.AccountStatusTrial;
                        break;
                    case SubscriptionType.Subscribed:
                        AccountStatus = coreStrings.AccountStatusSubscribed;
                        break;
                }

                if (info.SubscriptionExpiration.HasValue)
                    SubscriptionExpires = info.SubscriptionExpiration.Value.ToString("D");
            }

            catch (Exception ex)
            {
                this.Log().Error(() => nameof(UpdateAccountInfo), ex);
            }

            finally
            {
                IsLoading = false;
            }
        }
    }
}
