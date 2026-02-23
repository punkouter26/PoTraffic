# Deployment Report (2026-02-23)

## Deployment
- Commit: 9918cdfce30eabeefcc6a968f277caa2e8817545
- GitHub Actions run: 22327160061
- Run URL: https://github.com/punkouter26/PoTraffic/actions/runs/22327160061
- Pipeline conclusion: success
- Root endpoint status: 500
- Health endpoint status: 500
- Startup root cause from App Service logs: SQL authentication failure (`Login failed for user 'potrafficadmin'`).
- Validation note: ConnectionStrings__Default was aligned with Key Vault secret, app restarted, and `/health` remained 500.

## Resource Group Audit
### PoShared
- Microsoft.App/managedEnvironments :: cae-poshared
- Microsoft.Web/serverFarms :: asp-poshared
- Microsoft.OperationalInsights/workspaces :: PoShared-LogAnalytics
- Microsoft.KeyVault/vaults :: kv-poshared
- Microsoft.Insights/components :: poappideinsights8f9c9a4e
- Microsoft.CognitiveServices/accounts :: openai-poshared-eastus
- microsoft.insights/actiongroups :: Application Insights Smart Detection
- Microsoft.Storage/storageAccounts :: stpocouplequiz26
- Microsoft.ContainerRegistry/registries :: crposhared
- Microsoft.ManagedIdentity/userAssignedIdentities :: mi-poshared-containerapps
- Microsoft.Web/serverFarms :: asp-poshared-linux
- Microsoft.CognitiveServices/accounts :: cv-poshared-eastus
- Microsoft.CognitiveServices/accounts :: speech-poshared-eastus
- Microsoft.CognitiveServices/accounts :: punkouter26-7216-resource
- Microsoft.CognitiveServices/accounts/projects :: punkouter26-7216-resource/punkouter26-7216
- Microsoft.Storage/storageAccounts :: stpunkouter2495025212114
- Microsoft.KeyVault/vaults :: kv-punkoute495025212114
- Microsoft.MachineLearningServices/workspaces :: punkouter26-2650_ai
- Microsoft.MachineLearningServices/workspaces :: punkouter26-4140
- Microsoft.Maps/accounts :: maps-potraffic
- Microsoft.Sql/servers :: potraffic-sql-shared-22602
- Microsoft.Sql/servers/databases :: potraffic-sql-shared-22602/master
- Microsoft.Sql/servers/databases :: potraffic-sql-shared-22602/potrafficdb
- Microsoft.Sql/servers/databases :: potraffic-sql-shared-22602/free-sql-db-6747937

### PoTraffic
- Microsoft.Web/sites :: potraffic-shared-22602

## Key Vault Secret Names
### kv-poshared
- ApplicationInsights--ConnectionString
- ApplicationInsights-ConnectionString
- Authentication--Google--ClientId
- Authentication--Google--ClientSecret
- Authentication-Google-ClientId
- Authentication-Google-ClientSecret
- azure-credentials-json
- azure-sp-client-id
- azure-sp-client-secret
- azure-sp-subscription-id
- azure-sp-tenant-id
- AzureAI--ApiKey
- AzureAI--Endpoint
- AzureAI--Language--ApiKey
- AzureAI--Language--Endpoint
- AzureAI--ModelId
- AzureAI-Language-ApiKey
- AzureAI-Language-Endpoint
- AzureAiFoundry--ApiKey
- AzureAiFoundry--Endpoint
- AzureAiFoundry--ImageModel
- AzureAiFoundry--Model
- AzureMaps--ApiKey
- AzureOpenAI--ApiKey
- AzureOpenAI--DeploymentName
- AzureOpenAI--Endpoint
- AzureOpenAI--ModelId
- AzureOpenAI-ApiKey
- AzureOpenAI-DeploymentName
- AzureOpenAI-Endpoint
- AzureOpenAI-ImageEndpoint
- AzureOpenAI-ImageKey
- AzureSpeech-Region
- AzureSpeech-SubscriptionKey
- AzureStorageConnectionString
- CognitiveServiceEndpoint
- CognitiveServiceKey
- ComputerVision--ApiKey
- ComputerVision--Endpoint
- ComputerVision-ApiKey
- ComputerVision-Endpoint
- ConnectionStrings--AzureTableStorage
- ConnectionStrings--Tables
- ExternalAuth--Google--ClientId
- ExternalAuth--Google--ClientSecret
- ExternalAuth--Google--Enabled
- ExternalAuth--Microsoft--ClientId
- ExternalAuth--Microsoft--ClientSecret
- ExternalAuth--Microsoft--Enabled
- GitHub--PAT
- GoogleMaps--ApiKey
- PoAppIdea--AzureAI--ApiKey
- PoAppIdea--AzureAI--DeploymentName
- PoAppIdea--AzureAI--Endpoint
- PoAppIdea--AzureStorage--ConnectionString
- PoAppIdea--GoogleOAuth--ClientId
- PoAppIdea--GoogleOAuth--ClientSecret
- PoAppIdea--GoogleOAuth--CreationDate
- PoAppIdea--GoogleOAuth--Status
- PoAppIdea--MicrosoftOAuth--ClientId
- PoAppIdea--MicrosoftOAuth--ClientSecret
- PoBabyTouch-AzureTableStorage
- PoBabyTouch-TableStorage
- PoConnectFive--AzureTableStorage--ConnectionString
- PoCoupleQuiz--ApplicationInsights--ConnectionString
- PoCoupleQuiz--AzureOpenAI--ApiKey
- PoCoupleQuiz--AzureOpenAI--DeploymentName
- PoCoupleQuiz--AzureOpenAI--Endpoint
- PoCoupleQuiz--AzureStorage--ConnectionString
- PoDebateRap-NewsApi-ApiKey
- PoDebateRap-StorageConnection
- PoDropSquare--ApplicationInsights--ConnectionString
- PoDropSquare--AzureStorage--ConnectionString
- PoFight-StorageConnectionString
- PoFunQuiz-TableStorageConnectionString
- PoHappyTrump--ApplicationInsights--ConnectionString
- PoHappyTrump--AzureOpenAI--ApiKey
- PoHappyTrump--AzureOpenAI--DeploymentName
- PoHappyTrump--AzureOpenAI--Endpoint
- PoHappyTrump--AzureSpeech--ApiKey
- PoHappyTrump--AzureSpeech--Region
- PoMiniGames--StorageAccountName
- PoNovaWeight--AzureStorage--ConnectionString
- PoNovaWeight--Google--ClientId
- PoNovaWeight--Google--ClientSecret
- PoNovaWeight--Microsoft--ClientId
- PoRaceRagdoll-TableStorageConnectionString
- PoRedoImage-ApplicationInsights-ConnectionString
- PoRedoImage-ComputerVision-ApiKey
- PoRedoImage-ComputerVision-Endpoint
- PoRedoImage-OpenAI-ApiKey
- PoRedoImage-OpenAI-DeploymentName
- PoRedoImage-OpenAI-Endpoint
- PoRedoImage-OpenAI-ImageEndpoint
- PoRedoImage-OpenAI-ImageKey
- PoRedoImage-StorageConnectionString
- PoReflex-AppInsightsConnectionString
- PoReflex-TableStorageConnectionString
- PoRepoLineTracker--GitHub--ClientId
- PoRepoLineTracker--GitHub--ClientSecret
- PoRobotStocks-AlphaVantageApiKey
- PoRobotStocks-AzureAI-Language-ApiKey
- PoRobotStocks-AzureOpenAI-ApiKey
- PoRobotStocks-StorageConnectionString
- PoRobotStocks-TableStorage
- PoSeeReview--AzureOpenAI--DalleDeploymentName
- PoSeeReview--AzureStorage--ConnectionString
- PoSeeReview--ConnectionStrings--AzureBlobStorage
- PoSeeReview--ConnectionStrings--AzureTableStorage
- PoSeeReview--GoogleMaps--ApiKey
- PoSnakeGame-AppInsights
- PoSnakeGame-StorageConnection
- PoTicTac--AzureStorage--ConnectionString
- PoTicTac-TestSecret
- PoTraffic--ConnectionStrings--Default
- PoTraffic--GoogleMaps--ApiKey
- PoTraffic--Jwt--Key
- SemanticKernel--AzureOpenAIApiKey
- SemanticKernel--AzureOpenAIEndpoint
- SemanticKernel--DeploymentName

### kv-punkoute495025212114
- ACCESS_DENIED: current principal cannot list secrets for this vault.

## Recommendations
1. Rotate/reset SQL credential for `potrafficadmin` (or migrate to Managed Identity for DB auth), then update `PoTraffic--ConnectionStrings--Default` and web app setting to match.
2. Add a post-deploy smoke gate in GitHub Actions that fails on non-200 `/health` to catch runtime startup crashes early.
3. Enforce PoTraffic-prefixed secret naming and retire mixed legacy names in kv-poshared.
4. Move sensitive app settings to Key Vault references and reduce plain-text connection string exposure.
