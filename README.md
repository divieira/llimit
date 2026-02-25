# llimit

LLM cost gateway — a lightweight reverse-proxy in front of Azure OpenAI that tracks costs, enforces budgets, and provides a dashboard. Users can sign in via Azure AD and generate personal API keys.

## Quick Start

```bash
# Set required env vars
export LLIMIT_ADMIN_TOKEN=your-admin-secret

# Optional: Azure AD OAuth for user portal
export AZURE_AD_CLIENT_ID=your-app-client-id
export AZURE_AD_CLIENT_SECRET=your-app-client-secret
export AZURE_AD_TENANT_ID=your-tenant-id
export AZURE_AD_CORPORATE_DOMAIN=contoso.com

# Run
cd src/LLimit
dotnet run
```

## Configuration

| Variable | Required | Description |
|---|---|---|
| `LLIMIT_ADMIN_TOKEN` | Yes | Token for admin API + dashboard |
| `LLIMIT_DB_PATH` | No | SQLite path (default: `llimit.db`) |
| `AZURE_AD_CLIENT_ID` | For OAuth | Azure AD app registration client ID |
| `AZURE_AD_CLIENT_SECRET` | For OAuth | Azure AD app registration client secret |
| `AZURE_AD_TENANT_ID` | For OAuth | Azure AD tenant ID |
| `AZURE_AD_CORPORATE_DOMAIN` | For OAuth | Allowed email domain (e.g., `contoso.com`) |

Each project has its own Azure OpenAI endpoint URL and API key, configured when creating the project.

## Usage

### Create a project

```bash
curl -X POST http://localhost:5000/api/v1/projects \
  -H "Authorization: Bearer $LLIMIT_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "id": "my-team",
    "name": "My Team",
    "endpointUrl": "https://your-resource.openai.azure.com",
    "endpointKey": "your-azure-api-key",
    "budgetDaily": 10.00,
    "allowUserKeys": true
  }'
```

Response includes `apiKey` — shown once, give it to the team.

### Use as Azure OpenAI proxy

With a **project key** (shared team key):

```bash
curl http://localhost:5000/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21 \
  -H "api-key: llimit-..." \
  -H "X-LLimit-User: alice" \
  -H "Content-Type: application/json" \
  -d '{"messages": [{"role": "user", "content": "Hello"}]}'
```

With a **personal user key** (no `X-LLimit-User` header needed — user is derived from the key):

```bash
curl http://localhost:5000/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21 \
  -H "api-key: llimit-u-..." \
  -H "Content-Type: application/json" \
  -d '{"messages": [{"role": "user", "content": "Hello"}]}'
```

### User Portal

If Azure AD OAuth is configured, users can visit `http://localhost:5000/portal/login` to:

1. Sign in with their corporate Microsoft account
2. Browse projects that allow self-service keys
3. Create a personal API key (one per project)
4. Track their own usage and costs

The username is derived from the email prefix (e.g., `alice@contoso.com` → `alice`).

### Admin Dashboard

Visit `http://localhost:5000/dashboard/` and log in with the admin token.

## Docker

```bash
docker build -t llimit .
docker run -p 8080:8080 \
  -e LLIMIT_ADMIN_TOKEN=... \
  -e AZURE_AD_CLIENT_ID=... \
  -e AZURE_AD_CLIENT_SECRET=... \
  -e AZURE_AD_TENANT_ID=... \
  -e AZURE_AD_CORPORATE_DOMAIN=contoso.com \
  -v llimit-data:/data \
  llimit
```

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md) for full design details.
