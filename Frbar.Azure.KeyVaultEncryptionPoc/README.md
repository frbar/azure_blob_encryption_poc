# Azure Blob Storage / Azure Key Vault Encryption POC

This demonstrates client-side encryption of Blobs using Azure SDK and an RSA key managed by Key Vault.

A text blob will be uploaded, downloaded, copied, updated to use the backup key, etc...

## Requirements

- 2 storages accounts (in different regions)
- 2 key vault accounts (in different regions but same geography)
- A service principal, with permissions on Keys, on the 2 vaults
- The console app will generate an RSA key the first time. Download it from the portal and restore it in the secondary vault

## Config

In `secrets.json`:

```
{
  "StorageConnectionString": "DefaultEndpointsProtocol=https;AccountName=xxx;EndpointSuffix=core.windows.net",
  "StorageConnectionStringBackup": "DefaultEndpointsProtocol=https;AccountName=xxx;EndpointSuffix=core.windows.net",
  "TenantId": "xxx",
  "ClientId": "xxx",
  "ClientSecret": "xxx",
  "VaultName": "xxx",
  "VaultNameBackup": "xxx"
}
```