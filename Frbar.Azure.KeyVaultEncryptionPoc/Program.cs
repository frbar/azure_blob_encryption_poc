using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Frbar.Azure.KeyVaultEncryptionPoc.Proxy;
using Microsoft.Extensions.Configuration;

namespace Frbar.Azure.KeyVaultEncryptionPoc
{
    /// <summary>
    /// https://docs.microsoft.com/fr-fr/azure/storage/blobs/storage-encrypt-decrypt-blobs-key-vault
    /// https://docs.microsoft.com/en-us/azure/storage/common/storage-client-side-encryption#blob-service-encryption
    /// https://docs.microsoft.com/en-us/azure/storage/common/storage-client-side-encryption?tabs=dotnet#interface-and-dependencies
    /// https://stackoverflow.com/questions/64644174/encryption-with-azure-bob-storage-v12-sdk-for-net
    /// https://docs.microsoft.com/en-us/samples/azure/azure-sdk-for-net/azure-key-vault-proxy/
    /// </summary>

    class Program
    {
        private static string _storageConnectionString;
        private static string _storageConnectionStringBackup;
        private static HttpPipelinePolicy _proxy = new KeyVaultProxy();
        private static string _clientId;
        private static string _clientSecret;
        private static string _tenantId;
        private static string _vaultName;
        private static string _vaultNameBackup;

        static async Task Main(string[] args)
        {
            var config = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", true)
                .AddUserSecrets<Program>()
                .Build();

            _storageConnectionString = config.GetValue<string>("StorageConnectionString");
            _storageConnectionStringBackup = config.GetValue<string>("StorageConnectionStringBackup");
            _tenantId = config.GetValue<string>("TenantId");
            _clientId = config.GetValue<string>("ClientId");
            _clientSecret = config.GetValue<string>("ClientSecret");
            _vaultName = config.GetValue<string>("VaultName");
            _vaultNameBackup = config.GetValue<string>("VaultNameBackup");
            
            
            Console.WriteLine("Hello World!");

            var containerName = "tenant-y";
            var copyContainerName = "tenant-y-copy";
            var containerNameOtherStorage = "tenant-y-other";

            var fileName = await UploadBlob_KeyPerStorage(containerName, null);
            await CopyBlobSameStorage(containerName, copyContainerName, fileName);
            
            await DownloadCopySameStorage(copyContainerName, fileName);
            await CopyBlobSameStorageAndUseBackupKey(containerName, copyContainerName, fileName);
            await DownloadCopyWithBackupKey(copyContainerName, fileName);
            
            await CopyBlobToBackupAndUseBackupKey(containerName, containerNameOtherStorage, fileName);
            var dest = await DownloadCopyFromBackupWithBackupKey(containerNameOtherStorage, fileName);

            var originalContent = File.ReadAllText("files/" + fileName);
            var finalContent = File.ReadAllText(dest);

            if (originalContent != finalContent)
            {
                throw new Exception("An error has occured and the content has been altered.");
            }
            
            Console.WriteLine("Done - All Good");
        }

        private static async Task<string> UploadBlob_KeyPerStorage(string containerName, string fileName)
        {
            if (fileName == null)
            {
                fileName = Guid.NewGuid() + ".txt";
                File.WriteAllText("files/" + fileName, $"Hello - {DateTime.Now}");
            }

            var cred = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            var vaultUri = new Uri("https://" + _vaultName +".vault.azure.net/");

            KeyClientOptions keyClientOptions = new KeyClientOptions();
            keyClientOptions.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            KeyClient keyClient = new KeyClient(vaultUri, cred, keyClientOptions);
            
            
            // if you do not have key, please use following code to create
            var keyName = "nomdustorage-masterkey";
            var keyWrapAlgorithm = "RSA1_5";
            KeyVaultKey rsaKey;
            try
            {
                rsaKey = await keyClient.GetKeyAsync(keyName);
            }
            catch (Exception e)
            {
                // no key, let's create one
                // in real life this won't be done by the app but by the provisioning pipeline
                rsaKey = await keyClient.CreateRsaKeyAsync(new CreateRsaKeyOptions(keyName)
                {
                    KeySize = 4096
                });
            }

            CryptographyClientOptions cryptographyClientOptions = new CryptographyClientOptions();
            cryptographyClientOptions.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            var key = new CryptographyClient(rsaKey.Id, cred, cryptographyClientOptions);
            var keyResolver = new KeyResolver(cred, cryptographyClientOptions);
            var encryptionOptions = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V1_0)
            {
                KeyEncryptionKey = key,
                KeyResolver = keyResolver,
                KeyWrapAlgorithm = keyWrapAlgorithm
            };

            var options = new SpecializedBlobClientOptions() { ClientSideEncryption = encryptionOptions };
            options.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            
            var blobContainerClient = new BlobServiceClient(_storageConnectionString, options).GetBlobContainerClient(containerName);
            await blobContainerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            var blob = blobContainerClient.GetBlobClient(fileName);
            await using (FileStream file = File.OpenRead("files/" + fileName))
            {
                await blob.UploadAsync(file);
            }

            await blob.DownloadToAsync("files/downloaded-" + fileName);
            return fileName;
        }

        private static async Task CopyBlobSameStorage(string containerName, string destContainerName, string fileName)
        {
            var sourceBlob = new BlobServiceClient(_storageConnectionString).GetBlobContainerClient(containerName).GetBlobClient(fileName);
            var sourceBlobProperties = await sourceBlob.GetPropertiesAsync();
            var options = new BlobCopyFromUriOptions() { Metadata = sourceBlobProperties.Value.Metadata };
            
            var blobContainerClient = new BlobServiceClient(_storageConnectionString).GetBlobContainerClient(destContainerName);
            await blobContainerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            var blob = blobContainerClient.GetBlobClient(fileName);
            blob.StartCopyFromUri(sourceBlob.Uri, options);
        }

        private static async Task DownloadCopySameStorage(string containerName, string fileName)
        {
            var cred = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            var vaultUri = new Uri("https://" + _vaultName +".vault.azure.net/");
            
            KeyClientOptions keyClientOptions = new KeyClientOptions();
            keyClientOptions.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            KeyClient keyClient = new KeyClient(vaultUri, cred, keyClientOptions);

            var keyName = "nomdustorage-masterkey";
            var keyWrapAlgorithm = "RSA1_5";
            KeyVaultKey rsaKey = await keyClient.GetKeyAsync(keyName);
            
            CryptographyClientOptions cryptographyClientOptions = new CryptographyClientOptions();
            cryptographyClientOptions.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            var key = new CryptographyClient(rsaKey.Id, cred, cryptographyClientOptions);
            var keyResolver = new KeyResolver(cred, cryptographyClientOptions);
            var encryptionOptions = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V1_0)
            {
                KeyEncryptionKey = key,
                KeyResolver = keyResolver,
                KeyWrapAlgorithm = keyWrapAlgorithm
            };

            var options = new SpecializedBlobClientOptions() { ClientSideEncryption = encryptionOptions };
            options.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            
            var blob = new BlobServiceClient(_storageConnectionString, options).GetBlobContainerClient(containerName).GetBlobClient(fileName);
            await blob.DownloadToAsync("files/downloaded-copy-" + fileName);
        }

        private static async Task CopyBlobSameStorageAndUseBackupKey(string containerName, string destContainerName, string fileName)
        {
            var sourceBlob = new BlobServiceClient(_storageConnectionString).GetBlobContainerClient(containerName).GetBlobClient(fileName);
            var sourceBlobProperties = await sourceBlob.GetPropertiesAsync();
            var options = new BlobCopyFromUriOptions()
            {
                Metadata = sourceBlobProperties.Value.Metadata
            };
            
            options.Metadata["encryptiondata"] = options.Metadata["encryptiondata"].Replace(_vaultName, _vaultNameBackup);

            var blobContainerClient = new BlobServiceClient(_storageConnectionString).GetBlobContainerClient(destContainerName);
            await blobContainerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            var blob = blobContainerClient.GetBlobClient(fileName);
            blob.StartCopyFromUri(sourceBlob.Uri, options);
        }

        private static async Task DownloadCopyWithBackupKey(string containerName, string fileName)
        {
            var cred = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            var vaultUri = new Uri("https://" + _vaultNameBackup +".vault.azure.net/");

            KeyClientOptions keyClientOptions = new KeyClientOptions();
            keyClientOptions.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            KeyClient keyClient = new KeyClient(vaultUri, cred, keyClientOptions);

            var keyName = "nomdustorage-masterkey";
            var keyWrapAlgorithm = "RSA1_5";
            KeyVaultKey rsaKey = await keyClient.GetKeyAsync(keyName);

            CryptographyClientOptions cryptographyClientOptions = new CryptographyClientOptions();
            cryptographyClientOptions.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            var key = new CryptographyClient(rsaKey.Id, cred, cryptographyClientOptions);
            var keyResolver = new KeyResolver(cred, cryptographyClientOptions);
            var encryptionOptions = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V1_0)
            {
                KeyEncryptionKey = key,
                KeyResolver = keyResolver,
                KeyWrapAlgorithm = keyWrapAlgorithm
            };

            var options = new SpecializedBlobClientOptions() { ClientSideEncryption = encryptionOptions };
            options.AddPolicy(_proxy, HttpPipelinePosition.PerCall);

            var blob = new BlobServiceClient(_storageConnectionString, options).GetBlobContainerClient(containerName).GetBlobClient(fileName);
            await blob.DownloadToAsync("files/downloaded-copy-kv2-" + fileName);
        }

        private static async Task CopyBlobToBackupAndUseBackupKey(string containerName, string destContainerName, string fileName)
        {
            var sourceBlob = new BlobServiceClient(_storageConnectionString).GetBlobContainerClient(containerName).GetBlobClient(fileName);
            var sourceBlobProperties = await sourceBlob.GetPropertiesAsync();
            var options = new BlobCopyFromUriOptions()
            {
                Metadata = sourceBlobProperties.Value.Metadata
            };
            var sasToken = sourceBlob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.Now.AddMinutes(2));

            options.Metadata["encryptiondata"] = options.Metadata["encryptiondata"].Replace(_vaultName, _vaultNameBackup);

            var blobContainerClient = new BlobServiceClient(_storageConnectionStringBackup).GetBlobContainerClient(destContainerName);
            await blobContainerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            Task.Delay(5000);
            
            var blob = blobContainerClient.GetBlobClient(fileName);
            blob.StartCopyFromUri(sasToken, options);
        }

        private static async Task<string> DownloadCopyFromBackupWithBackupKey(string containerName, string fileName)
        {
            var cred = new ClientSecretCredential(_tenantId, _clientId, _clientSecret);
            var vaultUri = new Uri("https://" + _vaultNameBackup +".vault.azure.net/");

            KeyClientOptions keyClientOptions = new KeyClientOptions();
            keyClientOptions.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            KeyClient keyClient = new KeyClient(vaultUri, cred, keyClientOptions);

            var keyName = "nomdustorage-masterkey";
            var keyWrapAlgorithm = "RSA1_5";
            KeyVaultKey rsaKey = await keyClient.GetKeyAsync(keyName);

            CryptographyClientOptions cryptographyClientOptions = new CryptographyClientOptions();
            cryptographyClientOptions.AddPolicy(_proxy, HttpPipelinePosition.PerCall);
            var key = new CryptographyClient(rsaKey.Id, cred, cryptographyClientOptions);
            var keyResolver = new KeyResolver(cred, cryptographyClientOptions);
            var encryptionOptions = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V1_0)
            {
                KeyEncryptionKey = key,
                KeyResolver = keyResolver,
                KeyWrapAlgorithm = keyWrapAlgorithm
            };

            var options = new SpecializedBlobClientOptions() { ClientSideEncryption = encryptionOptions };
            options.AddPolicy(_proxy, HttpPipelinePosition.PerCall);

            var blob = new BlobServiceClient(_storageConnectionStringBackup, options).GetBlobContainerClient(containerName).GetBlobClient(fileName);
            var dest = "files/downloaded-copy-kv2-backup-" + fileName;
            await blob.DownloadToAsync(dest);
            return dest;
        }
    }
}
