using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Crypto;
using Ciphernote.Extensions;
using Ciphernote.Logging;
using Ciphernote.Net;
using Ciphernote.Resources;
using Ciphernote.Services;
using Ciphernote.UI;
using FluentValidation;
using ReactiveUI;
using Splat;

namespace Ciphernote.ViewModels
{
    public class LoginViewModel : ViewModelBase
    {
        public LoginViewModel(IComponentContext ctx, ICoreStrings coreStrings, 
            IAppCoreSettings appSettings, CryptoService cryptoService, IPromptFactory promptFactory,
            SyncService syncService, AccountService accountService) : base(ctx)
        {
            this.coreStrings = coreStrings;
            this.appSettings = appSettings;
            this.cryptoService = cryptoService;
            this.promptFactory = promptFactory;
            this.accountService = accountService;

            RememberMe = appSettings.RememberMe;

            if (RememberMe)
            {
                Email = appSettings.Email;
                Password = appSettings.Password;
            }

            loginCmdValidator = new LoginCmdValidator(coreStrings);

            LoginCommand = ReactiveCommand.CreateFromTask(ExecuteLogin);

            isBusy = LoginCommand.IsExecuting.ToProperty(this, x => x.IsBusy);
        }

        private class LoginCmdValidator : AbstractValidator<LoginViewModel>
        {
            public LoginCmdValidator(ICoreStrings coreStrings)
            {
                RuleFor(vm => vm.Email).NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error));
                RuleFor(vm => vm.Email).EmailAddress().NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.email_error));
                RuleFor(vm => vm.Password).NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error));
                RuleFor(vm => vm.AccountKey).NotEmpty().When(x=> x.IsAccountKeyRequired).WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error));

                RuleFor(vm => vm.AccountKey).Must((vm, response) => !vm.invalidAccountKeyFormat).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.InvalidAccountKeyFormat);
                });

                RuleFor(vm => vm.Email).Must((vm, response) => !vm.invalidCredentials).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.LoginFailed);
                });

                RuleFor(vm => vm.Password).Must((vm, response) => !vm.invalidCredentials).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.LoginFailed);
                });

                RuleFor(vm => vm.Password).Must((vm, response) => !vm.networkError).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.NetworkError);
                });
            }
        }

        readonly LoginCmdValidator loginCmdValidator;
        private readonly ICoreStrings coreStrings;
        private readonly IAppCoreSettings appSettings;
        private readonly IPromptFactory promptFactory;
        private readonly CryptoService cryptoService;
        private readonly AccountService accountService;

        private readonly ObservableAsPropertyHelper<bool> isBusy;
        private bool invalidCredentials = false;
        private bool networkError = false;
        private bool invalidAccountKeyFormat = false;

        public Action SetComplete { get; set; }

        private string email;

        public string Email
        {
            get { return email; }
            set { this.RaiseAndSetIfChanged(ref email, value); }
        }

        private string password;

        public string Password
        {
            get { return password; }
            set { this.RaiseAndSetIfChanged(ref password, value); }
        }

        private bool rememberMe;

        public bool RememberMe
        {
            get { return rememberMe; }
            set { this.RaiseAndSetIfChanged(ref rememberMe, value); }
        }

        private bool isAccountKeyRequired;

        public bool IsAccountKeyRequired
        {
            get { return isAccountKeyRequired; }
            set { this.RaiseAndSetIfChanged(ref isAccountKeyRequired, value); }
        }

        private string accountKey;

        public string AccountKey
        {
            get { return accountKey; }
            set { this.RaiseAndSetIfChanged(ref accountKey, value); }
        }

        public ReactiveCommand LoginCommand { get; private set; }

        public bool IsBusy => isBusy.Value;

        public async Task CheckAccountKeyPresent()
        {
            if(!string.IsNullOrEmpty(Email))
                IsAccountKeyRequired = !(await cryptoService.IsAccountKeyPresentForEmail(Email));
        }

        public async Task Init()
        {
            await CheckAccountKeyPresent();
        }

        private async Task ExecuteLogin()
        {
            invalidCredentials = false;
            networkError = false;
            invalidAccountKeyFormat = false;
            var isLoggedIn = false;

            if(!RememberMe)
            {
                appSettings.Email = string.Empty;
                appSettings.Password = string.Empty;
                appSettings.RememberMe = false;
            }

            if (Validate(loginCmdValidator))
            {
                var masterKeyNotFound = false;

                try
                {
                    await cryptoService.SetCredentialsAsync(Email, Password);

                    if (!IsAccountKeyRequired)
                        await cryptoService.LoadAccountKeyAsync();
                    else
                    {
                        byte[] accountKeyBinary = null;

                        try
                        {
                            accountKeyBinary = AccountKey.Trim().Replace("-", "").FromHex();
                        }

                        catch (Exception )
                        {
                            invalidAccountKeyFormat = true;
                            Validate(loginCmdValidator);
                            return;
                        }

                        cryptoService.SetAccountKey(accountKeyBinary);
                    }
                }

                catch (IOException)
                {
                    IsAccountKeyRequired = true;
                    Validate(loginCmdValidator);
                    return;
                }

                catch (CryptoServiceException ex)
                {
                    this.Log().Error(() => nameof(ExecuteLogin), ex);

                    // account key failed to decrypt
                    invalidCredentials = true;
                    Validate(loginCmdValidator);
                    return;
                }

                try
                {
                    await cryptoService.LoadMasterKeyAsync();

                    isLoggedIn = true;
                }

                catch (IOException)
                {
                    masterKeyNotFound = true;
                }

                if (masterKeyNotFound)
                {
                    // query backend
                    try
                    {
                        var details = await accountService.GetAccountDetails();

                        // Import content-key
                        await cryptoService.ImportMasterKey(Convert.FromBase64String(details.EncryptedMasterKey));
                        await cryptoService.SaveMasterKeyAsync();

                        isLoggedIn = true;
                    }

                    catch (RestRequestException ex)
                    {
                        this.Log().Error(() => nameof(ExecuteLogin), ex);

                        if (ex.StatusCode == HttpStatusCode.Forbidden)
                            invalidCredentials = true;
                        else
                        {
                            networkError = true;
                            promptFactory.NotifyNoCloud(coreStrings);
                        }

                        Validate(loginCmdValidator);
                    }

                    catch (HttpRequestException ex)
                    {
                        this.Log().Error(() => nameof(ExecuteLogin), ex);

                        networkError = true;
                        Validate(loginCmdValidator);
                        promptFactory.NotifyNoCloud(coreStrings);
                    }

                    catch (Exception ex)
                    {
                        this.Log().Error(()=> nameof(ExecuteLogin), ex);
                    }
                }

                if (isLoggedIn)
                {
                    appSettings.RememberMe = RememberMe;

                    if (IsAccountKeyRequired)
                        await cryptoService.SaveAccountKeyAsync();

                    if (appSettings.RememberMe)
                    {
                        appSettings.Email = Email;
                        appSettings.Password = Password;
                    }

                    SetComplete();
                }
            }
        }
    }
}
