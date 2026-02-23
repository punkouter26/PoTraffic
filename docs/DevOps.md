
# PoTraffic DevOps & Deployment Strategy

## Tech Stack
*   **Hosting**: Azure App Service (B1 Tier minimum)
*   **Database**: Azure SQL Database (Serverless)
*   **Job Processing**: Hangfire (Persistence via SQL)
*   **Logging**: Azure Monitor (App Insights) via Serilog

## CI/CD Pipeline (GitHub Actions)
1.  **Stage: Build & Test**
    - dotnet restore
    - dotnet build --configuration Release
    - dotnet test (Unit + Integration)
2.  **Stage: E2E Verification**
    - Deploy to staging/test environment
    - Run **Playwright .NET** scenarios against API/Client
3.  **Stage: Deploy**
    - dotnet publish
    - Zip and deploy to Azure App Service via Azure/webapps-deploy

## Environment Secrets
| Key | Purpose |
|---|---|
| SQL_CONNECTION_STRING | Link to production Azure SQL |
| JWT_SECRET | 256-bit key for token signing |
| GOOGLE_MAPS_API_KEY | External provider access |
| TOMTOM_API_KEY | External provider access |
