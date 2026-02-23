# Social Auth Setup (Google + Microsoft)

This guide configures PoTraffic social sign-in using:
- `gcloud` for Google Cloud project/API prerequisites
- `az` for Azure Key Vault secret storage and App Service settings

## 1) Google setup (gcloud-first)

Run:

```powershell
./scripts/setup-social-auth-gcloud.ps1 \
  -ProjectId "<gcp-project-id>" \
  -AppDisplayName "PoTraffic" \
  -RedirectUri "https://<your-host>/api/auth/external/google/callback" \
  -SupportEmail "<owner-email>"
```

Then complete OAuth client creation in Google Cloud Console (required for standard web OAuth clients):
1. APIs & Services -> Credentials -> Create OAuth client ID (Web application)
2. Add redirect URI: `https://<your-host>/api/auth/external/google/callback`
3. Save generated `Client ID` and `Client Secret`

## 2) Microsoft setup (Azure portal / Entra app registration)

Create an app registration that supports personal + organizational accounts and add redirect URI:
- `https://<your-host>/api/auth/external/microsoft/callback`

Collect:
- `MICROSOFT_CLIENT_ID`
- `MICROSOFT_CLIENT_SECRET`

## 3) Persist secrets to Azure Key Vault and apply app settings (Azure CLI)

Run:

```powershell
./scripts/setup-social-auth-azure.ps1 \
  -SubscriptionId "<azure-subscription-id>" \
  -ResourceGroup "<resource-group>" \
  -KeyVaultName "<keyvault-name>" \
  -WebAppName "<webapp-name>" \
  -GoogleClientId "<google-client-id>" \
  -GoogleClientSecret "<google-client-secret>" \
  -MicrosoftClientId "<microsoft-client-id>" \
  -MicrosoftClientSecret "<microsoft-client-secret>"
```

Key Vault secret names are mapped to config keys by `--`:
- `ExternalAuth--Google--ClientId`
- `ExternalAuth--Google--ClientSecret`
- `ExternalAuth--Google--Enabled`
- `ExternalAuth--Microsoft--ClientId`
- `ExternalAuth--Microsoft--ClientSecret`
- `ExternalAuth--Microsoft--Enabled`

## 4) Local development

If not using Key Vault in local dev, set in `src/PoTraffic.Api/appsettings.Development.json` or user-secrets:
- `ExternalAuth:Google:Enabled`
- `ExternalAuth:Google:ClientId`
- `ExternalAuth:Google:ClientSecret`
- `ExternalAuth:Microsoft:Enabled`
- `ExternalAuth:Microsoft:ClientId`
- `ExternalAuth:Microsoft:ClientSecret`

## 5) Verify

1. Open `/login`
2. Click `Continue with Google` and `Continue with Microsoft`
3. Complete provider login
4. Confirm redirect lands in `/dashboard` as authenticated user
