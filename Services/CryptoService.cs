using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Autofac;
using Ciphernote.Crypto;
using Ciphernote.Extensions;
using Ciphernote.IO;
using Ciphernote.Logging;
using CodeContracts;
using ReactiveUI;
using Splat;

namespace Ciphernote.Services
{
    public class CryptoService : ReactiveObject
    {
        public CryptoService(IComponentContext ctx, IRandomNumberGenerator rng, 
            IFileEx fileEx, IPasswordSanitizer passwordSanitizer,
            IAppCoreSettings appSettings)
        {
            this.ctx = ctx;
            this.rng = rng;
            this.fileEx = fileEx;
            this.passwordSanitizer = passwordSanitizer;
            this.appSettings = appSettings;
        }

        private readonly IComponentContext ctx;
        private readonly IRandomNumberGenerator rng;
        private readonly IFileEx fileEx;
        private readonly IPasswordSanitizer passwordSanitizer;
        private readonly IAppCoreSettings appSettings;

        protected const int PasswordIterations = 50000;
        protected const int Pbdf2Sha1BlockSize = 20;
        private int IvLength = 16;
        public const int AccountKeyLength = 16;    // 128-Bit
        public const int AesKeyLength = 32;    // AES-256
        public const int HmacLength = 32;    // HMAC-SHA-256
        public const int MaxUsernameLength = 256;

        // Cached material (sensitive)
        private string email;
        private string password;
        private byte[] credentialsKey;
        private byte[] accountKeyEncryptionKey;
        private byte[] accountKey;
        private byte[] masterKeyEncryptionKey;
        private byte[] masterKey;
        private string profileName;
        private string gravatarId;
        private string accessToken;

        // ReSharper disable InconsistentNaming
        [Flags]
        private enum Flags
        {
            CIPHER_AES_CBC_PKCS7 = 0x1,

            HMAC_SHA256 = 0x100,
        }
        // ReSharper restore InconsistentNaming

        private class DecryptorWrapper : IDisposable
        {
            public DecryptorWrapper(SymmetricAlgorithm alg, ICryptoTransform transform)
            {
                this.alg = alg;
                Transform = transform;
            }

            private SymmetricAlgorithm alg;
            internal ICryptoTransform Transform { get; private set; }

            public void Dispose()
            {
                if (alg == null)
                    throw new ObjectDisposedException(nameof(DecryptorWrapper));

                Transform.Dispose();
                Transform = null;

                alg.Dispose();
                alg = null;
            }
        }

        #region API Surface

        public string Email => email;
        public string ProfileName => profileName;
        public string GravatarId => gravatarId;
        public string AccessToken => accessToken;

        public byte[] AccountKey => accountKey;
        public byte[] DbEncryptionKey { get; private set; }

        /// <summary>
        /// Initializes various fields from the supplied credentials
        /// </summary>
        public async Task SetCredentialsAsync(string email, string password)
        {
            Contract.RequiresNonNull(email, nameof(email));
            Contract.RequiresNonNull(password, nameof(password));
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(email), $"{nameof(email)} should not be empty");
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(password), $"{nameof(password)} should not be empty");

            this.Log().Info(() => $"{nameof(SetCredentialsAsync)}");

            await Task.Run(() =>
            {
                // Extract & Sanitize inputs
                this.email = email.Trim().ToLower();
                this.password = passwordSanitizer.SanitizePassword(password);
                byte[] salt;

                // Derive Profile Name
                profileName = EmailToProfileName(this.email);
#if DEBUG
                if (!string.IsNullOrEmpty(appSettings.ProfileNameOverride))
                    profileName = appSettings.ProfileNameOverride;
#endif
                // GravatarId
                using (var hash = MD5.Create())
                {
                    var hashBytes = hash.ComputeHash(Encoding.UTF8.GetBytes(email));
                    gravatarId = hashBytes.ToHex().ToLower();
                }

                // Derive Salt
                using (var hash = SHA256.Create())
                    salt = hash.ComputeHash(Encoding.UTF8.GetBytes(this.email));

                // Derive Account Key Encryption Key
                using (var kbd = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), salt, PasswordIterations))
                {
                    credentialsKey = kbd.GetBytes(Pbdf2Sha1BlockSize);

                    HkdfDeriveKeys(credentialsKey, hkdf =>
                    {
                        accountKeyEncryptionKey = hkdf.Expand("ACCOUNT-KEY-ENCRYPTION-KEY", AesKeyLength);
                    });
                }
            });
        }

        public void SetAccountKey(byte[] key)
        {
            Contract.RequiresNonNull(key, nameof(key));
            Contract.Requires<ArgumentException>(key.Length == AccountKeyLength, $"key must be exactly {AccountKeyLength} bytes");
            ThrowIfCredentialsNotSet();

            this.Log().Info(() => $"{nameof(SetAccountKey)}");

            accountKey = key;

            // Combine 128-Bit Account-Key and 160-Bit Credentials-Key into 288-Bit intermediate key
            var compositeKey = accountKey
                .Concat(credentialsKey)
                .ToArray();

            HkdfDeriveKeys(compositeKey, hkdf =>
            {
                // Derive Master Key Encryption Key
                masterKeyEncryptionKey = hkdf.Expand("MASTER-KEY-ENCRYPTION-KEY", AesKeyLength);

                // Derive Access Token
                accessToken = Convert.ToBase64String(hkdf.Expand("ACCESS-TOKEN", AesKeyLength));
            });
        }

        /// <summary>
        /// Generates a virgin account key (used during new user registration)
        /// </summary>
        public byte[] GenerateAccountKey()
        {
            return rng.GenerateRandomBytes(AccountKeyLength);
        }

        public async Task<bool> IsAccountKeyPresentForEmail(string email)
        {
            var profile = EmailToProfileName(email);
#if DEBUG
            if (!string.IsNullOrEmpty(appSettings.ProfileNameOverride))
                profileName = appSettings.ProfileNameOverride;
#endif
            try
            {
                using (var source = await OpenAccountKeyForReadAsync(profile))
                {
                    return source.Length > 0;
                }
            }

            catch (IOException)
            {
            }

            return false;
        }

        public bool ValidatePassword(string password)
        {
            Contract.RequiresNonNull(password, nameof(password));

            return this.password == passwordSanitizer.SanitizePassword(password);
        }

        /// <summary>
        /// Initializes the master key from the encrypted version stored in the current user profile
        /// </summary>
        public async Task LoadAccountKeyAsync()
        {
            this.Log().Info(() => $"{nameof(LoadAccountKeyAsync)}");

            using (var source = await OpenAccountKeyForReadAsync())
            {
                using (var destination = new MemoryStream())
                {
                    await DecryptAsync(source, destination, accountKeyEncryptionKey);

                    SetAccountKey(destination.ToArray());
                }
            }
        }

        /// <summary>
        /// Stores the master key encrypted in the current user profile
        /// </summary>
        public async Task SaveAccountKeyAsync()
        {
            ThrowIfAccountKeyNotSet();

            this.Log().Info(() => $"{nameof(SaveAccountKeyAsync)}");

            using (var source = new MemoryStream(accountKey))
            {
                using (var destination = await OpenAccountKeyForWriteAsync())
                {
                    await EncryptAsync(source, destination, accountKeyEncryptionKey);
                }
            }
        }

        /// <summary>
        /// Generates a virgin account key (used during new user registration)
        /// </summary>
        public void GenerateAndSetMasterKey()
        {
            SetMasterKey(rng.GenerateRandomBytes(AesKeyLength));
        }

        /// <summary>
        /// Initializes the master key from the encrypted version stored in the current user profile
        /// </summary>
        public async Task LoadMasterKeyAsync()
        {
            this.Log().Info(() => $"{nameof(LoadMasterKeyAsync)}");

            using (var source = await OpenMasterKeyForReadAsync())
            {
                using (var destination = new MemoryStream())
                {
                    await DecryptAsync(source, destination, masterKeyEncryptionKey);

                    SetMasterKey(destination.ToArray());
                }
            }
        }

        /// <summary>
        /// Stores the master key encrypted in the current user profile
        /// </summary>
        public async Task SaveMasterKeyAsync()
        {
            this.Log().Info(() => $"{nameof(SaveMasterKeyAsync)}");

            using (var source = new MemoryStream(masterKey))
            {
                using (var destination = await OpenMasterKeyForWriteAsync())
                {
                    await EncryptAsync(source, destination, masterKeyEncryptionKey);
                }
            }
        }

        /// <summary>
        /// Initializes the content-key from the specified encrypted version
        /// </summary>
        public async Task ImportMasterKey(byte[] encryptedMasterKey)
        {
            this.Log().Info(() => $"{nameof(ImportMasterKey)}");

            using (var source = new MemoryStream(encryptedMasterKey))
            {
                using (var destination = new MemoryStream())
                {
                    await DecryptAsync(source, destination, masterKeyEncryptionKey);

                    SetMasterKey(destination.ToArray());
                }
            }
        }

        /// <summary>
        /// Returns the content-key encrypted
        /// </summary>
        public async Task<byte[]> ExportEncryptedMasterKey()
        {
            using (var source = new MemoryStream(masterKey))
            {
                using (var destination = new MemoryStream())
                {
                    await EncryptAsync(source, destination, masterKeyEncryptionKey);
                    return destination.ToArray();
                }
            }
        }

        public async Task EncryptAsync(Stream source, Stream destination, byte[] key)
        {
            Contract.RequiresNonNull(source, nameof(source));
            Contract.RequiresNonNull(key, nameof(key));
            Contract.RequiresNonNull(destination, nameof(destination));
            Contract.Requires<ArgumentException>(key.Length == AesKeyLength, $"key must be exactly {AesKeyLength} bytes");
            Contract.Requires<ArgumentException>(destination.CanSeek, "destination stream must be seekable");

            // Derive keys
            byte[] keyAes, keyMac;
            DeriveMacKeys(key, out keyAes, out keyMac);

            // Write HMAC length
            destination.WriteByte(HmacLength);

            // Reserve space for MAC (SHA256) and one byte for HMAC length prefix
            destination.SetLength(HmacLength + 1);
            destination.Seek(0, SeekOrigin.End);

            // Write Flags
            var flags = (ulong)(Flags.CIPHER_AES_CBC_PKCS7 | Flags.HMAC_SHA256);
            var flagBytes = BitConverter.GetBytes(flags);
            await destination.WriteAsync(flagBytes, 0, flagBytes.Length);

            // Write IV
            var iv = rng.GenerateRandomBytes(IvLength);
            await destination.WriteAsync(iv, 0, iv.Length);

            // Encrypt
            using (var symmetricKey = Aes.Create())
            {
                symmetricKey.KeySize = AesKeyLength * 8;
                symmetricKey.Mode = CipherMode.CBC;
                symmetricKey.Padding = PaddingMode.PKCS7;

                using (var encryptor = symmetricKey.CreateEncryptor(keyAes, iv))
                {
                    // CryptoStream has the bad habit of assuming ownership of its input streams,
                    // that's why we do not dispose it. Since we dispose all the other cryptographic
                    // resources, no leaks are caused
                    var cs = new CryptoStream(destination, encryptor, CryptoStreamMode.Write);

                    await source.CopyToAsync(cs);

                    if (!cs.HasFlushedFinalBlock)
                        cs.FlushFinalBlock();
                }
            }

            // Compute HMAC
            destination.Seek(HmacLength + 1, SeekOrigin.Begin);
            var hmac = ComputeHmac(destination, keyMac);

            // Seek to begin and write HMAC
            destination.Seek(1, SeekOrigin.Begin);
            destination.Write(hmac, 0, hmac.Length);
        }

        public async Task DecryptAsync(Stream source, Stream destination, byte[] key)
        {
            Contract.RequiresNonNull(source, nameof(source));
            Contract.RequiresNonNull(key, nameof(key));
            Contract.RequiresNonNull(destination, nameof(destination));
            Contract.Requires<ArgumentException>(key.Length == AesKeyLength, $"key must be exactly {AesKeyLength} bytes");
            Contract.Requires<ArgumentException>(source.CanSeek, "source stream must be seekable");

            using (var decryptor = await CreateDecryptorAsync(source, key))
            {
                // CryptoStream has the bad habit of assuming ownership of its input streams,
                // that's why we do not dispose it. Since we dispose all the other cryptographic
                // resources, no leaks are caused
                var cs = new CryptoStream(source, decryptor.Transform, CryptoStreamMode.Read);

                await cs.CopyToAsync(destination);

                if (!cs.HasFlushedFinalBlock)
                    cs.FlushFinalBlock();
            }
        }

        public async Task<Stream> GetDecryptedStreamAsync(Stream source, byte[] key)
        {
            Contract.RequiresNonNull(source, nameof(source));
            Contract.RequiresNonNull(key, nameof(key));
            Contract.Requires<ArgumentException>(key.Length == AesKeyLength, $"key must be exactly {AesKeyLength} bytes");
            Contract.Requires<ArgumentException>(source.CanSeek, "source stream must be seekable");

            var decryptor = await CreateDecryptorAsync(source, key);
            return new CryptoStreamWithResources(source, decryptor.Transform, CryptoStreamMode.Read, new IDisposable[] { decryptor });
        }

        public Stream GetDecryptedStreamSync(Stream source, byte[] key)
        {
            Contract.RequiresNonNull(source, nameof(source));
            Contract.RequiresNonNull(key, nameof(key));
            Contract.Requires<ArgumentException>(key.Length == AesKeyLength, $"key must be exactly {AesKeyLength} bytes");
            Contract.Requires<ArgumentException>(source.CanSeek, "source stream must be seekable");

            var decryptor = CreateDecryptorSync(source, key);
            return new CryptoStreamWithResources(source, decryptor.Transform, CryptoStreamMode.Read, new IDisposable[] { decryptor });
        }

        public async Task<byte[]> EncryptAsync(byte[] sourceBytes, byte[] key)
        {
            Contract.RequiresNonNull(sourceBytes, nameof(sourceBytes));
            Contract.RequiresNonNull(key, nameof(key));

            using (var source = new MemoryStream(sourceBytes))
            {
                using (var destination = new MemoryStream())
                {
                    await EncryptAsync(source, destination, key);

                    return destination.ToArray();
                }
            }
        }

        public async Task<byte[]> DecryptAsync(byte[] sourceBytes, byte[] key)
        {
            Contract.RequiresNonNull(sourceBytes, nameof(sourceBytes));
            Contract.RequiresNonNull(key, nameof(key));

            using (var source = new MemoryStream(sourceBytes))
            {
                using (var destination = new MemoryStream())
                {
                    await DecryptAsync(source, destination, key);

                    return destination.ToArray();
                }
            }
        }

        public Task EncryptContentAsync(Stream source, Stream destination)
        {
            Contract.RequiresNonNull(source != null, nameof(source));
            Contract.RequiresNonNull(destination, nameof(destination));
            ThrowIfMasterKeyNotSet();

            return EncryptAsync(source, destination, masterKey);
        }

        public Task DecryptContentAsync(Stream source, Stream destination)
        {
            Contract.RequiresNonNull(source, nameof(source));
            Contract.RequiresNonNull(destination, nameof(destination));
            ThrowIfMasterKeyNotSet();

            return DecryptAsync(source, destination, masterKey);
        }

        public async Task<Stream> GetDecryptedContentStreamAsync(Stream source)
        {
            Contract.RequiresNonNull(source, nameof(source));
            ThrowIfMasterKeyNotSet();

            return await GetDecryptedStreamAsync(source, masterKey);
        }

        public Stream GetDecryptedContentStreamSync(Stream source)
        {
            Contract.RequiresNonNull(source, nameof(source));
            ThrowIfMasterKeyNotSet();

            return GetDecryptedStreamSync(source, masterKey);
        }

        public async Task<byte[]> ComputeContentHmacAsync(Stream source)
        {
            Contract.RequiresNonNull(source, nameof(source));
            ThrowIfMasterKeyNotSet();

            return await Task.Run(() =>
            {
                // derive keys
                byte[] keyAes, keyMac;
                DeriveMacKeys(masterKey, out keyAes, out keyMac);

                return ComputeHmac(source, keyMac);
            });
        }

        public static string FormatAccountKey(byte[] key)
        {
            var hexString = key.ToHex().ToUpper();

            var result = new string(hexString.Take(5).ToArray()) + "-" +
                         new string(hexString.Skip(5).Take(5).ToArray()) + "-" +
                         new string(hexString.Skip(10).Take(5).ToArray()) + "-" +
                         new string(hexString.Skip(15).Take(5).ToArray()) + "-" +
                         new string(hexString.Skip(20).Take(5).ToArray()) + "-" +
                         new string(hexString.Skip(25).ToArray());

            return result;
        }

        #endregion // API Surface

        private async Task<Stream> OpenAccountKeyForReadAsync(string profileOverride = null)
        {
            return await fileEx.OpenFileForReadAsync(AppCoreConstants.ProfileDataSpecialFolder, Path.Combine(
                AppCoreConstants.ProfileDataBaseFolder,
                profileOverride ?? profileName,
                AppCoreConstants.EncryptedAccountKeyFilename));
        }

        private async Task<Stream> OpenAccountKeyForWriteAsync()
        {
            var keyPath = Path.Combine(
                AppCoreConstants.ProfileDataBaseFolder,
                profileName,
                AppCoreConstants.EncryptedAccountKeyFilename);

            await fileEx.EnsureFolderExistsAsync(AppCoreConstants.ProfileDataSpecialFolder, Path.GetDirectoryName(keyPath));

            return await fileEx.OpenFileForWriteAsync(AppCoreConstants.ProfileDataSpecialFolder, keyPath);
        }

        private void SetMasterKey(byte[] key)
        {
            Contract.RequiresNonNull(key, nameof(key));
            Contract.Requires<ArgumentException>(key.Length == AesKeyLength, $"key must be exactly {AesKeyLength} bytes");

            masterKey = key;

            HkdfDeriveKeys(masterKey, hkdf =>
            {
                // Derive Database Encryption Key
                DbEncryptionKey = hkdf.Expand("DB-ENCRYPTION-KEY", AesKeyLength);
            });
        }

        private async Task<Stream> OpenMasterKeyForReadAsync()
        {
            return await fileEx.OpenFileForReadAsync(AppCoreConstants.ProfileDataSpecialFolder, Path.Combine(
                AppCoreConstants.ProfileDataBaseFolder,
                profileName,
                AppCoreConstants.EncryptedMasterKeyFilename));
        }

        private async Task<Stream> OpenMasterKeyForWriteAsync()
        {
            var keyPath = Path.Combine(
                AppCoreConstants.ProfileDataBaseFolder,
                profileName,
                AppCoreConstants.EncryptedMasterKeyFilename);

            await fileEx.EnsureFolderExistsAsync(AppCoreConstants.ProfileDataSpecialFolder, Path.GetDirectoryName(keyPath));

            return await fileEx.OpenFileForWriteAsync(AppCoreConstants.ProfileDataSpecialFolder, keyPath);
        }

        private void ThrowIfCredentialsNotSet()
        {
            if (credentialsKey == null)
                throw new CryptoServiceException(CryptoServiceExceptionType.CredentialsNotInitialized);
        }

        private void ThrowIfAccountKeyNotSet()
        {
            if (accountKey == null)
                throw new CryptoServiceException(CryptoServiceExceptionType.AccountKeyNotInitialized);
        }

        private void ThrowIfMasterKeyNotSet()
        {
            if (masterKey == null)
                throw new CryptoServiceException(CryptoServiceExceptionType.MasterKeyNotInitialized);
        }

        private DecryptorWrapper CreateDecryptorSync(Stream source, byte[] key)
        {
            // Derive keys
            byte[] keyAes, keyMac;
            DeriveMacKeys(key, out keyAes, out keyMac);

            // Read HMAC
            var hmacLength = source.ReadByte();
            var hmac = new byte[hmacLength];
            source.Read(hmac, 0, hmac.Length);

            // Read Flags
            var flagBytes = new byte[8];
            source.Read(flagBytes, 0, flagBytes.Length);
            var flags = BitConverter.ToUInt64(flagBytes, 0);

            if (flags != (ulong)(Flags.CIPHER_AES_CBC_PKCS7 | Flags.HMAC_SHA256))
                throw new CryptoServiceException(CryptoServiceExceptionType.UnsupportedFlag);

            // HMAC Verification
            source.Seek(hmacLength + 1, SeekOrigin.Begin);
            VerifyHmacSync(source, keyMac, hmac);

            // Read IV
            var iv = new byte[IvLength];
            source.Seek(hmacLength + 1 + flagBytes.Length, SeekOrigin.Begin);
            source.Read(iv, 0, iv.Length);

            // Decrypt
            var alg = Aes.Create();
            alg.KeySize = AesKeyLength * 8;
            alg.Mode = CipherMode.CBC;
            alg.Padding = PaddingMode.PKCS7;

            var decryptor = alg.CreateDecryptor(keyAes, iv);

            return new DecryptorWrapper(alg, decryptor);
        }

        private async Task<DecryptorWrapper> CreateDecryptorAsync(Stream source, byte[] key)
        {
            return await Task.Run(() => CreateDecryptorSync(source, key));
        }

        private static string EmailToProfileName(string email)
        {
            using (var hash = SHA256.Create())
            {
                return hash.ComputeHash(Encoding.UTF8.GetBytes(email)).ToHex();
            }
        }

        private static void HkdfDeriveKeys(byte[] key, Action<HKDF> action)
        {
            using (var hmac = new HMACSHA256())
            {
                var hkdf = new HKDF(hmac, key);

                action(hkdf);
            }
        }

        private static void DeriveMacKeys(byte[] key, out byte[] keyAes, out byte[] keyMac)
        {
            byte[] aes = null, mac = null;

            HkdfDeriveKeys(key, hkdf =>
            {
                aes = hkdf.Expand("AES", AesKeyLength);
                mac = hkdf.Expand("MAC", AesKeyLength);
            });

            keyAes = aes;
            keyMac = mac;
        }

        private static byte[] ComputeHmac(Stream stream, byte[] key)
        {
            Contract.RequiresNonNull(stream, nameof(stream));
            Contract.RequiresNonNull(key, nameof(key));

            using (var mac = new HMACSHA256(key))
            {
                var result = mac.ComputeHash(stream);

                Debug.Assert(result.Length == HmacLength);
                return result;
            }
        }

        private static void VerifyHmacSync(Stream stream, byte[] key, byte[] hmac)
        {
            Contract.RequiresNonNull(stream, nameof(stream));
            Contract.RequiresNonNull(key, nameof(key));
            Contract.RequiresNonNull(hmac, nameof(hmac));

            using (var mac = new HMACSHA256(key))
            {
                var hmacActual = mac.ComputeHash(stream);

                if (!hmac.ConstantTimeAreEqual(hmacActual))
                    throw new CryptoServiceException(CryptoServiceExceptionType.HmacMismatch);
            }
        }
    }
}
