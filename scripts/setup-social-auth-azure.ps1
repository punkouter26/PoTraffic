param(
    [Parameter(Mandatory = $true)]
    [string]$SubscriptionId,

    [Parameter(Mandatory = $true)]
    [string]$ResourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$KeyVaultName,

    [Parameter(Mandatory = $true)]
    [string]$WebAppName,

    [Parameter(Mandatory = $true)]
    [string]$GoogleClientId,

    [Parameter(Mandatory = $true)]
    [string]$GoogleClientSecret,

    [Parameter(Mandatory = $true)]
    [string]$MicrosoftClientId,

    [Parameter(Mandatory = $true)]
    [string]$MicrosoftClientSecret
)

$ErrorActionPreference = "Stop"

Write-Host "Logging into Azure and selecting subscription..." -ForegroundColor Cyan
az login | Out-Null
az account set --subscription $SubscriptionId

Write-Host "Writing social auth secrets to Key Vault..." -ForegroundColor Cyan
az keyvault secret set --vault-name $KeyVaultName --name "ExternalAuth--Google--ClientId" --value $GoogleClientId | Out-Null
az keyvault secret set --vault-name $KeyVaultName --name "ExternalAuth--Google--ClientSecret" --value $GoogleClientSecret | Out-Null
az keyvault secret set --vault-name $KeyVaultName --name "ExternalAuth--Google--Enabled" --value "true" | Out-Null
az keyvault secret set --vault-name $KeyVaultName --name "ExternalAuth--Microsoft--ClientId" --value $MicrosoftClientId | Out-Null
az keyvault secret set --vault-name $KeyVaultName --name "ExternalAuth--Microsoft--ClientSecret" --value $MicrosoftClientSecret | Out-Null
az keyvault secret set --vault-name $KeyVaultName --name "ExternalAuth--Microsoft--Enabled" --value "true" | Out-Null

Write-Host "Setting app service ExternalAuth flags (non-secret) ..." -ForegroundColor Cyan
az webapp config appsettings set \
  --resource-group $ResourceGroup \
  --name $WebAppName \
  --settings ExternalAuth__Google__Enabled=true ExternalAuth__Microsoft__Enabled=true | Out-Null

Write-Host "Social auth settings applied." -ForegroundColor Green
