using System;
using System.Drawing;
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
    public class RegistrationViewModel : ViewModelBase
    {
        public RegistrationViewModel(IComponentContext ctx, ICoreStrings coreStrings,
            IAppCoreSettings appSettings, IFileEx fileEx, IPromptFactory promptFactory,
            CryptoService cryptoService, AccountService accountService) : base(ctx)
        {
            this.coreStrings = coreStrings;
            this.appSettings = appSettings;
            this.fileEx = fileEx;
            this.promptFactory = promptFactory;
            this.cryptoService = cryptoService;
            this.accountService = accountService;

            registerCmdValidator = new RegisterCmdValidator(coreStrings);

            RegisterCommand = ReactiveCommand.CreateFromTask(ExecuteRegister);

            NextStepCommand = ReactiveCommand.CreateFromTask(ExecuteNextStep);
            PreviousStepCommand = ReactiveCommand.CreateFromTask(ExecutePreviousStep);

            isBusy = RegisterCommand.IsExecuting.ToProperty(this, x => x.IsBusy);

            var passwordEntropy = this.WhenAny(x => x.Password, x => x.Value)
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

            // setup account key
            accountKey = cryptoService.GenerateAccountKey();
            AccountKeyString = CryptoService.FormatAccountKey(accountKey);
        }

        private readonly ICoreStrings coreStrings;
        private readonly IAppCoreSettings appSettings;
        private readonly IFileEx fileEx;
        private readonly IPromptFactory promptFactory;
        private readonly CryptoService cryptoService;
        private readonly AccountService accountService;

        private readonly RegisterCmdValidator registerCmdValidator;
        private bool isEmailTaken = false;
        private bool networkError = false;
        private readonly ObservableAsPropertyHelper<bool> isBusy;
        private readonly ObservableAsPropertyHelper<int> passwordEntropyBits;
        private readonly ObservableAsPropertyHelper<string> passwordEntropyText;
        private readonly ObservableAsPropertyHelper<Color> passwordEntropyColor;
        
        class RegisterCmdValidator : AbstractValidator<RegistrationViewModel>
        {
            public RegisterCmdValidator(ICoreStrings coreStrings)
            {
                RuleFor(vm => vm.Email).NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error));
                RuleFor(vm => vm.Email).EmailAddress().NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.email_error));

                RuleFor(vm => vm.Email).Must((vm, password) => !vm.isEmailTaken).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.RegisterEmailTaken);
                });

                RuleFor(vm => vm.Password).NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error));
                RuleFor(vm => vm.Password).Length(4, 64).WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.length_error));
                RuleFor(vm => vm.ConfirmPassword).NotEmpty().WithLocalizedMessage(typeof(FluentValidation.Resources.Messages), nameof(FluentValidation.Resources.Messages.notempty_error));


                RuleFor(vm => vm.ConfirmPassword).Must((vm, password) => vm.Password == password).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.ValidationPasswordConfirmMismatch);
                });

                RuleFor(vm => vm.Password).Must((vm, response) => !vm.networkError).Configure(cfg => {
                    cfg.CurrentValidator.ErrorMessageSource = new FluentValidation.Resources.LazyStringSource((_) => coreStrings.NetworkError);
                });
            }
        }

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

        private string confirmPassword;

        public string ConfirmPassword
        {
            get { return confirmPassword; }
            set { this.RaiseAndSetIfChanged(ref confirmPassword, value); }
        }

        public int PasswordEntropyBits => passwordEntropyBits.Value;
        public string PasswordEntropyText => passwordEntropyText.Value;
        public Color PasswordEntropyColor => passwordEntropyColor.Value;

        public ReactiveCommand NextStepCommand { get; }
        public ReactiveCommand PreviousStepCommand { get; }

        public ReactiveCommand RegisterCommand { get; }

        public bool IsBusy => isBusy.Value;

        private readonly byte[] accountKey;
        public string AccountKeyString { get; }

        private int step = 1;

        public int Step
        {
            get { return step; }
            set { this.RaiseAndSetIfChanged(ref step, value); }
        }

        private Task ExecuteNextStep()
        {
            Step++;
            return Task.FromResult(true);
        }

        private Task ExecutePreviousStep()
        {
            Step--;
            return Task.FromResult(true);
        }
        
        private async Task ExecuteRegister()
        {
            isEmailTaken = false;
            networkError = false;
            var isRegistered = false;

            if (Validate(registerCmdValidator))
            {
                try
                {
                    await cryptoService.SetCredentialsAsync(Email, Password);

                    // Setup account-key
                    cryptoService.SetAccountKey(accountKey);

                    // Setup virgin master-key
                    cryptoService.GenerateAndSetMasterKey();

                    // and export it 
                    var encryptedContentKey = await cryptoService.ExportEncryptedMasterKey();

                    // register with backend
                    var response = await accountService.Register(cryptoService.AccessToken, encryptedContentKey);

                    if (response.Success)
                        isRegistered = true;
                    else
                    {
                        isEmailTaken = response.ResponseMessageId == ServicesErrors.ApiErrorEmailTaken;
                        Validate(registerCmdValidator);
                    }
                }

                catch (RestRequestException ex)
                {
                    this.Log().Error(() => nameof(ExecuteRegister), ex);

                    if (ex.StatusCode != HttpStatusCode.Forbidden)
                    {
                        networkError = true;

                        promptFactory.NotifyNoCloud(coreStrings);
                    }

                    Validate(registerCmdValidator);
                }

                catch (HttpRequestException ex)
                {
                    this.Log().Error(() => nameof(ExecuteRegister), ex);

                    networkError = true;
                    Validate(registerCmdValidator);

                    promptFactory.NotifyNoCloud(coreStrings);
                }

                catch (Exception ex)
                {
                    this.Log().Error(() => nameof(ExecuteRegister), ex);

                    networkError = true;
                }
            }

            if (isRegistered)
            {
                await EnsureCleanProfileFolder();
                await cryptoService.SaveAccountKeyAsync();
                await cryptoService.SaveMasterKeyAsync();

                appSettings.RememberMe = false;
                Step++;
            }
        }

        private async Task EnsureCleanProfileFolder()
        {
            var profilePath = Path.Combine(
                AppCoreConstants.ProfileDataBaseFolder,
                cryptoService.ProfileName);

            try
            {
                await fileEx.DeleteFolderAsync(AppCoreConstants.ProfileDataSpecialFolder, profilePath);
            }

            catch (IOException ex)
            {
                this.Log().Error(() => nameof(EnsureCleanProfileFolder), ex);
            }

            try
            {
                await fileEx.EnsureFolderExistsAsync(AppCoreConstants.ProfileDataSpecialFolder, profilePath);
            }

            catch (IOException ex)
            {
                this.Log().Error(() => nameof(EnsureCleanProfileFolder), ex);
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
    }
}
