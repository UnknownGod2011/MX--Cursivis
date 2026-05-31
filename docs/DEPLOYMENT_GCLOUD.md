# Optional Cloud Backend Deployment

This document covers optional cloud deployment for the Cursivis backend.

It is useful for:

- remote demos
- team access
- finals-event backup infrastructure
- hosted proof that the backend can run outside the local machine

It is **not** the core product story. The main story is Logitech-native workflow control through MX Creative Console, MX Master 4, and Actions Ring.

## What This Deploys

- `backend/gemini-agent`

## Important

Deploying the backend does not change the normal local demo unless you explicitly point the companion at the cloud URL.

Default local behavior:

- local backend stays on `http://127.0.0.1:8080`
- companion keeps using the local backend unless told otherwise

## Fast Deployment Script

```powershell
Set-Location -LiteralPath "C:\Users\Admin\OneDrive\Desktop\Cursivis! - Copy\cursivis"

powershell -ExecutionPolicy Bypass -File .\scripts\deploy-cloudrun.ps1 `
  -ProjectId "YOUR_PROJECT_ID" `
  -Region "us-central1" `
  -GoogleApiKey "YOUR_GOOGLE_API_KEY" `
  -UseSecretManager
```

## Manual Build + Deploy

```powershell
gcloud config set project YOUR_PROJECT_ID
gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com secretmanager.googleapis.com

gcloud builds submit --tag gcr.io/YOUR_PROJECT_ID/cursivis-gemini-agent -f backend/gemini-agent/Dockerfile .

gcloud run deploy cursivis-gemini-agent `
  --image gcr.io/YOUR_PROJECT_ID/cursivis-gemini-agent `
  --region us-central1 `
  --platform managed `
  --allow-unauthenticated `
  --set-env-vars GOOGLE_API_KEY=YOUR_KEY,CURSIVIS_ENABLE_LIVE_GROUNDING=true,GEMINI_MODEL=gemini-2.5-flash
```

## Health Check

```powershell
curl https://YOUR_CLOUD_RUN_URL/health
```

Expected:

```json
{"ok":true,"service":"gemini-agent","ts":"..."}
```

## Run The Companion Against The Cloud Backend

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-demo.ps1 `
  -ApiKey "<LOCAL_OR_FALLBACK_KEY>" `
  -BackendUrl "https://YOUR_CLOUD_RUN_URL"
```

## Notes

- keep the local version as the primary Logitech demo path
- use cloud only when hosted backend access is helpful
- this is infrastructure, not the headline story
