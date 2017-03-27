using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Crypto;
using Ciphernote.DataAccess.Models;
using Ciphernote.Extensions;
using Ciphernote.IO;
using Ciphernote.Logging;
using Ciphernote.Model;
using Ciphernote.Net;
using Ciphernote.Resources;
using Ciphernote.Services;
using Ciphernote.UI;
using FluentValidation;
using Newtonsoft.Json;
using ReactiveUI;
using Splat;

namespace Ciphernote.ViewModels
{
    public class OptionsViewModel : PasswordChangeViewModel
    {
        public OptionsViewModel(IComponentContext ctx, ICoreStrings coreStrings, 
            IAppCoreSettings appSettings, CryptoService cryptoService,
            IFileEx fileEx, IPromptFactory promptFactory,
            SyncService syncService, AccountService accountService) : 
            base(ctx, coreStrings, appSettings, fileEx, promptFactory, cryptoService, accountService)
        {
            this.coreStrings = coreStrings;
            this.appSettings = appSettings;
            this.cryptoService = cryptoService;
            this.syncService = syncService;
            this.accountService = accountService;

            Email = cryptoService.Email;
            AccountKeyString = CryptoService.FormatAccountKey(cryptoService.AccountKey);

            UnlockSecurityCommand = ReactiveCommand.Create(ExecuteUnlockSecurity,
                this.WhenAny(x => x.UnlockPassword, x => !string.IsNullOrEmpty(x.Value)));

            // update QR-Code on password change
            var passwordChangeCommandState = ChangePasswordCommand.IsExecuting.Zip(
                ChangePasswordCommand.IsExecuting.Skip(1), Tuple.Create);

            disposables.Add(passwordChangeCommandState.Where(x=> x.Item1 && !x.Item2)
                .Subscribe(_=> UpdateQrCode()));
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

        private string unlockPassword;

        public string UnlockPassword
        {
            get { return unlockPassword; }
            set { this.RaiseAndSetIfChanged(ref unlockPassword, value); }
        }

        private string qrCodeData;

        public string QrCodeData
        {
            get { return qrCodeData; }
            set { this.RaiseAndSetIfChanged(ref qrCodeData, value); }
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
            IsSecurityUnlocked = cryptoService.ValidatePassword(UnlockPassword);

            if (IsSecurityUnlocked)
                UpdateQrCode();
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
                    case ActiveSubscriptionType.None:
                        AccountStatus = coreStrings.AccountStatusNone;
                        break;
                    case ActiveSubscriptionType.Trial:
                        AccountStatus = coreStrings.AccountStatusTrial;
                        break;
                    case ActiveSubscriptionType.Subscribed:
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

        private void UpdateQrCode()
        {
            var data = new CredentialsQrCode();
            data.AccountKey = Convert.ToBase64String(cryptoService.AccountKey);
            data.Email = cryptoService.Email;
            data.Password = cryptoService.Password;

            var json = JsonConvert.SerializeObject(data);
            QrCodeData = json;
        }
    }
}
