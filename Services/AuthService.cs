using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Ciphernote.Crypto;
using Ciphernote.Net;
using Ciphernote.Services.Dto;
using CodeContracts;
using Newtonsoft.Json;
using ReactiveUI;

namespace Ciphernote.Services
{
    public class AuthService : ReactiveObject
    {
        public AuthService(CryptoService cryptoService, HttpClient httpClient)
        {
            this.cryptoService = cryptoService;
            this.client = new RestClient(AppCoreConstants.ApiEndpoint, httpClient);
        }

        private readonly CryptoService cryptoService;
        private readonly RestClient client;
        private DateTime? bearerTokenExpiration;
        private string bearerToken;

        public async Task SetupAuth(RestRequest request, bool forceRefresh = false)
        {
            Contract.RequiresNonNull(request, nameof(request));

            if (forceRefresh || string.IsNullOrEmpty(bearerToken) ||
                !bearerTokenExpiration.HasValue ||
                DateTime.UtcNow >= bearerTokenExpiration.Value.AddSeconds(-30))
            {
                var response = await RefreshToken();

                bearerToken = response.BearerToken;
                bearerTokenExpiration = response.Expires;
            }

            request.AddHeader("Authorization", $"Bearer {bearerToken}");
        }

        public async Task SetupAuth(HttpRequestMessage request, bool forceRefresh = false)
        {
            Contract.RequiresNonNull(request, nameof(request));

            if (forceRefresh || string.IsNullOrEmpty(bearerToken) ||
                !bearerTokenExpiration.HasValue ||
                DateTime.UtcNow >= bearerTokenExpiration.Value.AddSeconds(-30))
            {
                var response = await RefreshToken();

                bearerToken = response.BearerToken;
                bearerTokenExpiration = response.Expires;
            }

            request.Headers.Add("Authorization", $"Bearer {bearerToken}");
        }

        public void ResetToken()
        {
            bearerToken = null;
            bearerTokenExpiration = null;
        }

        private async Task<LoginResponse> RefreshToken()
        {
            var payload = new LoginRequest
            {
                Username = cryptoService.Email,
                AccessToken = cryptoService.AccessToken,
            };

            var request = RestRequest.Create("/api/token", HttpMethod.Post, payload);
            var json = await client.GetStringAsync(request);
            var response = JsonConvert.DeserializeObject<LoginResponse>(json);
            return response;
        }
    }
}
