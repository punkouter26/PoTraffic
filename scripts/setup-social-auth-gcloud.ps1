param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectId,

    [Parameter(Mandatory = $true)]
    [string]$AppDisplayName,

    [Parameter(Mandatory = $true)]
    [string]$RedirectUri,

    [Parameter(Mandatory = $false)]
    [string]$SupportEmail = "",

    [Parameter(Mandatory = $false)]
    [string]$Region = "europe-west1"
)

$ErrorActionPreference = "Stop"

Write-Host "Configuring Google Cloud project for OAuth..." -ForegroundColor Cyan

gcloud config set project $ProjectId | Out-Null
gcloud services enable iam.googleapis.com | Out-Null
gcloud services enable cloudresourcemanager.googleapis.com | Out-Null
gcloud services enable oauth2.googleapis.com | Out-Null

Write-Host "Project and APIs are configured." -ForegroundColor Green
Write-Host ""
Write-Host "Next step (Google Cloud Console, currently required for standard OAuth client creation):" -ForegroundColor Yellow
Write-Host "1) Open APIs & Services -> Credentials"
Write-Host "2) Create OAuth client ID (Web application)"
Write-Host "3) Add authorized redirect URI: $RedirectUri"
if (-not [string]::IsNullOrWhiteSpace($SupportEmail)) {
    Write-Host "4) Ensure OAuth consent support email is: $SupportEmail"
}
Write-Host ""
Write-Host "After creation, capture GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET." -ForegroundColor Yellow
