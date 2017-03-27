using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Extensions;
using Ciphernote.IO;
using Ciphernote.Logging;
using Ciphernote.Net;
using Ciphernote.Resources;
using Ciphernote.Services;
using Ciphernote.Services.Dto;
using Ciphernote.UI;
using FluentValidation;
using ReactiveUI;
using Splat;

namespace Ciphernote.ViewModels
{
    public class PasswordChangeViewModel : ViewModelBase
    {
        public PasswordChangeViewModel(IComponentContext ctx, ICoreStrings coreStrings,
            IAppCoreSettings appSettings, IFileEx fileEx, IPromptFactory promptFactory,
            CryptoService cryptoService, AccountService accountService) : base(ctx)
        {
            this.coreStrings = coreStrings;
            this.appSettings = appSettings;
            this.fileEx = fileEx;
            this.promptFactory = promptFactory;
            this.cryptoService = cryptoService;
            this.accountService = accountService;

            changeCmdValidator = new ChangeCmdValidator(coreStrings);

            ChangePasswordCommand = ReactiveCommand.CreateFromTask(ExecuteChangePassword);

            isBusy = ChangePasswordCommand.IsExecuting.ToProperty(this, x => x.IsBusy);

            var passwordEntropy = this.WhenAny(x => x.NewPassword, x => x.Value)
                .Sample(TimeSpan.FromMilliseconds(200))
                .Select(x =>
                {
                    if (string.IsNullOrEmpty(x))
                        return 0;

                    try
                    {
                        return (int) Zxcvbn.Zxcvbn.MatchPassword(x).Entropy;
                    }
                    catch
                    {
                        // ignored
                    }

                    return 0;
                })
                .ObserveOn(RxApp.MainThreadScheduler);

            passwordEntropyBits = passwordEntropy
                .ToProperty(this, x => x.PasswordEntropyBits);

            passwordEntropyText = passwordEntropy
                .Select(ToPasswordEntropyText)
                .ToProperty(this, x => x.PasswordEntropyText);

            passwordEntropyColor = passwordEntropy
                .Select(ToPasswordEntropyColor)
                .ToProperty(this, x => x.PasswordEntropyColor);

            disposables.Add(isBusy);
            disposables.Add(passwordEntropyBits);
            disposables.Add(passwordEntropyText);
            disposables.Add(passwordEntropyColor);
        }

        private readonly ICoreStrings coreStrings;
        private readonly IAppCoreSettings appSettings;
        private readonly IFileEx fileEx;
        private readonly IPromptFactory promptFactory;
        private readonly CryptoService cryptoService;
        private readonly AccountService accountService;

        private readonly ChangeCmdValidator changeCmdValidator;
        private bool networkError = false;
        private bool newPasswordEqualToCurrentPassword = false;
        private readonly ObservableAsPropertyHelper<bool> isBusy;
        private readonly ObservableAsPropertyHelper<int> passwordEntropyBits;
        private readonly ObservableAsPropertyHelper<string> passwordEntropyText;
        private readonly ObservableAsPropertyHelper<Color> passwordEntropyColor;
        
        class ChangeCmdValidator : AbstractValidator<PasswordChangeViewModel>
        {
            public ChangeCmdValidator(ICoreStrings coreStrings)
            {
                RuleFor(vm => vm.Password).NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error))
                    .Unless(vm=> !vm.ValidateOldPassword);

                RuleFor(vm => vm.NewPassword).Must((vm, response) => !vm.newPasswordEqualToCurrentPassword).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.NewPasswordEqualToCurrentPasswordError);
                });

                RuleFor(vm => vm.NewPassword).NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error));
                RuleFor(vm => vm.NewPassword).Length(4, 64).WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.length_error));
                RuleFor(vm => vm.ConfirmPassword).NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error));

                RuleFor(vm => vm.ConfirmPassword).Must((vm, password) => vm.NewPassword == password).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.ValidationPasswordConfirmMismatch);
                });

                RuleFor(vm => vm.NewPassword).Must((vm, response) => !vm.networkError).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.NetworkError);
                });
            }
        }

        private bool validateOldPassword;

        public bool ValidateOldPassword
        {
            get { return validateOldPassword; }
            set { this.RaiseAndSetIfChanged(ref validateOldPassword, value); }
        }

        private string password;

        public string Password
        {
            get { return password; }
            set { this.RaiseAndSetIfChanged(ref password, value); }
        }

        private string newPassword;

        public string NewPassword
        {
            get { return newPassword; }
            set { this.RaiseAndSetIfChanged(ref newPassword, value); }
        }

        private string confirmPassword;

        public string ConfirmPassword
        {
            get { return confirmPassword; }
            set { this.RaiseAndSetIfChanged(ref confirmPassword, value); }
        }

        public int PasswordEntropyBits => passwordEntropyBits.Value;
        public string PasswordEntropyText => passwordEntropyText.Value;
        public Color PasswordEntropyColor => passwordEntropyColor.Value;

        public ReactiveCommand ChangePasswordCommand { get; }

        public bool IsBusy => isBusy.Value;

        private async Task ExecuteChangePassword()
        {
            networkError = false;
            var isChanged = false;
            CryptoService.IPasswordChangeTransaction ptx = null;

            if (Validate(changeCmdValidator))
            {
                try
                {
                    ptx = cryptoService.CreatePasswordChangeTransaction(NewPassword);

                    // prepare transaction
                    var tuple = await ptx.PrepareAsync();
                    
                    // retrieve masterkey and access token encrypted with new keys
                    var newEncryptedMasterKey = tuple.Item1;
                    var newAccessToken = tuple.Item2;

                    // update backend
                    var response = await accountService.ChangePassword(newAccessToken, newEncryptedMasterKey);

                    if (response.Success)
                    {
                        await ptx.CommitAsync();
                        isChanged = true;

                        // reset fields
                        Password = "";
                        NewPassword = "";
                        ConfirmPassword = "";

                        // update appSettings
                        if (appSettings.RememberMe)
                        {
                            appSettings.Password = NewPassword;
                        }

                        // notify
                        promptFactory.ShowToast(ToastType.Success, coreStrings.PasswordChangeSuccessTitle,
                            coreStrings.PasswordChangeSuccessMessage, null, TimeSpan.FromSeconds(15));
                    }
                }

                catch (RestRequestException ex)
                {
                    this.Log().Error(() => nameof(ExecuteChangePassword), ex);

                    if (ex.StatusCode != HttpStatusCode.Forbidden)
                    {
                        networkError = true;

                        promptFactory.NotifyNoCloud(coreStrings);
                    }

                    Validate(changeCmdValidator);
                }

                catch (HttpRequestException ex)
                {
                    this.Log().Error(() => nameof(ExecuteChangePassword), ex);

                    networkError = true;
                    Validate(changeCmdValidator);

                    promptFactory.NotifyNoCloud(coreStrings);
                }

                catch (Exception ex)
                {
                    this.Log().Error(() => nameof(ExecuteChangePassword), ex);

                    networkError = true;
                }
            }

            if (!isChanged)
            {
                if (ptx != null)
                {
                    await ptx.RollbackAsync();

                    promptFactory.ShowToast(ToastType.Warning, coreStrings.PasswordChangeErrorTitle,
                        coreStrings.PasswordChangeErrorMessage, null, TimeSpan.FromSeconds(15));
                }
            }
        }

        private string ToPasswordEntropyText(int bits)
        {
            if (bits >= 96)
                return coreStrings.PasswordStrengthVeryStrong.ToLower();
            if (bits >= 64)
                return coreStrings.PasswordStrengthStrong.ToLower();
            if (bits >= 48)
                return coreStrings.PasswordStrengthSufficient.ToLower();
            if (bits >= 32)
                return coreStrings.PasswordStrengthWeak.ToLower();

            return coreStrings.PasswordStrengthVeryWeak.ToLower();
        }

        private Color ToPasswordEntropyColor(int bits)
        {
            if (bits >= 96)
                return Color.LawnGreen;
            if (bits >= 64)
                return Color.Green;
            if (bits >= 48)
                return Color.Yellow;
            if (bits >= 32)
                return Color.Orange;

            return Color.DarkRed;
        }

        public void ResetPasswordChange()
        {
            Password = null;
            NewPassword = null;
            ConfirmPassword = null;
        }
    }
}
