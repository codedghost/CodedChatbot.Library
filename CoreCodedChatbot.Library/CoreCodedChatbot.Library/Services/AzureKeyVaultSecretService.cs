using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using CoreCodedChatbot.Library.Interfaces.Services;
using Microsoft.Azure.KeyVault;
using Microsoft.IdentityModel.Clients.ActiveDirectory;

namespace CoreCodedChatbot.Library.Services
{
    public class AzureKeyVaultSecretService : ISecretService
    {
        private string _appId;
        private string _certThumbprint;
        private string _baseUrl;

        private Dictionary<string, string> _secrets;

        public AzureKeyVaultSecretService(IConfigService configService)
        {
            var config = configService.GetConfig();

            _appId = config.KeyVaultAppId;
            _certThumbprint = config.KeyVaultCertThumbprint;
            _baseUrl = config.KeyVaultBaseUrl;
        }

        public async Task Initialize()
        {
            using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                store.Open(OpenFlags.ReadOnly);

                var cert = store.Certificates.Find(X509FindType.FindByThumbprint,
                    _certThumbprint, false)[0];

                var assertionCert = new ClientAssertionCertificate(_appId, cert);

                var keyVaultClient = new KeyVaultClient(async (authority, resource, scope) =>
                {
                    var context = new AuthenticationContext(authority, TokenCache.DefaultShared);

                    var result = await context.AcquireTokenAsync(resource, assertionCert);

                    return result.AccessToken;
                });

                _secrets = new Dictionary<string, string>();

                var listSecrets = await keyVaultClient.GetSecretsAsync(_baseUrl);

                foreach (var secretInfo in listSecrets)
                {
                    var secret = await keyVaultClient.GetSecretAsync(_baseUrl, secretInfo.Identifier.Name);

                    _secrets.Add(secretInfo.Identifier.Name, secret.Value);
                }
            }
        }

        public T GetSecret<T>(string secretKey)
        {
            var secret = _secrets[secretKey];

            if (string.IsNullOrWhiteSpace(secret))
            {
                return default(T);
            }

            return (T) Convert.ChangeType(secret, typeof(T));
        }
    }
}