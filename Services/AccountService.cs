using System;
using System.Net.Http;
using System.Threading.Tasks;
using Ciphernote.Net;
using Ciphernote.Services.Dto;
using CodeContracts;
using ReactiveUI;

namespace Ciphernote.Services
{
    public class AccountService : ReactiveObject
    {
        public AccountService(HttpClient httpClient, AuthService authService, CryptoService cryptoService)
        {
            this.client = new RestClient(AppCoreConstants.ApiEndpoint, httpClient);
            this.authService = authService;
            this.cryptoService = cryptoService;
        }

        private readonly RestClient client;
        private readonly AuthService authService;
        private readonly CryptoService cryptoService;

        public async Task<RegistrationResponse> Register(string accessToken, byte[] encryptedContentKey, string lang)
        {
            Contract.RequiresNonNull(accessToken, nameof(accessToken));
            Contract.RequiresNonNull(encryptedContentKey, nameof(encryptedContentKey));

            var payload = new RegistrationRequest
            {
                Email = cryptoService.Email,
                AccessToken = accessToken,
                EncryptedMasterKey = Convert.ToBase64String(encryptedContentKey),
                LanguagePreferenceTwoLetterIsoCode = lang,
            };

            var request = RestRequest.Create("/api/account/register", HttpMethod.Post, payload);
            var response = await client.ExecuteAsync<RegistrationResponse>(request);
            return response.Content;
        }

        public async Task<AccountDetailsResponse> GetAccountDetails()
        {
            var request = RestRequest.Create("/api/account/details", HttpMethod.Post);
            await authService.SetupAuth(request);
            var response = await client.ExecuteAsync<AccountDetailsResponse>(request);
            return response.Content;
        }

        public async Task<ResponseBase> ChangePassword(string accessToken, byte[] encryptedContentKey)
        {
            Contract.RequiresNonNull(accessToken, nameof(accessToken));
            Contract.RequiresNonNull(encryptedContentKey, nameof(encryptedContentKey));

            var payload = new PasswordChangeRequest
            {
                NewAccessToken = accessToken,
                NewEncryptedMasterKey = Convert.ToBase64String(encryptedContentKey),
            };

            var request = RestRequest.Create("/api/account/change-password", HttpMethod.Post, payload);
            await authService.SetupAuth(request);
            var response = await client.ExecuteAsync<ResponseBase>(request);
            return response.Content;
        }

    }
}
