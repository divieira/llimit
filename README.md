# llimit

LLM cost gateway — a lightweight reverse-proxy in front of Azure AI Foundry that tracks costs, enforces budgets, and provides a dashboard.

## Quick Start

```bash
# Set required env vars
export AZURE_FOUNDRY_ENDPOINT=https://your-resource.eastus.inference.ai.azure.com
export AZURE_FOUNDRY_API_KEY=your-foundry-key
export LLIMIT_ADMIN_TOKEN=your-admin-secret

# Run
cd src/LLimit
dotnet run
```

## Configuration

| Variable | Required | Description |
|---|---|---|
| `AZURE_FOUNDRY_ENDPOINT` | Yes | Azure AI Foundry endpoint URL |
| `AZURE_FOUNDRY_API_KEY` | Yes | Azure AI Foundry API key |
| `LLIMIT_ADMIN_TOKEN` | Yes | Token for admin API + dashboard |
| `LLIMIT_DB_PATH` | No | SQLite path (default: `llimit.db`) |

## Usage

### Create a project

```bash
curl -X POST http://localhost:5000/api/v1/projects \
  -H "Authorization: Bearer $LLIMIT_ADMIN_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"id": "my-team", "name": "My Team", "budgetDaily": 10.00}'
```

Response includes `apiKey` — shown once, give it to the team.

### Use as proxy

```bash
curl http://localhost:5000/v1/chat/completions \
  -H "api-key: llimit-..." \
  -H "X-LLimit-User: alice" \
  -H "Content-Type: application/json" \
  -d '{"model": "claude-3-5-sonnet", "messages": [{"role": "user", "content": "Hello"}]}'
```

`X-LLimit-User` is optional (defaults to `_anonymous`).

### Dashboard

Visit `http://localhost:5000/dashboard/` and log in with the admin token.

## Docker

```bash
docker build -t llimit .
docker run -p 8080:8080 \
  -e AZURE_FOUNDRY_ENDPOINT=... \
  -e AZURE_FOUNDRY_API_KEY=... \
  -e LLIMIT_ADMIN_TOKEN=... \
  -v llimit-data:/data \
  llimit
