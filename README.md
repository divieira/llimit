# llimit

LLM cost gateway — a lightweight reverse-proxy in front of Azure OpenAI that tracks costs, enforces budgets, and provides a dashboard.

## Quick Start

```bash
# Set required env vars
export AZURE_OPENAI_ENDPOINT=https://your-resource.openai.azure.com
export AZURE_OPENAI_API_KEY=your-azure-key
export LLIMIT_ADMIN_TOKEN=your-admin-secret

# Run
cd src/LLimit
dotnet run
```

## Configuration

| Variable | Required | Description |
|---|---|---|
| `AZURE_OPENAI_ENDPOINT` | Yes | Azure OpenAI base URL |
| `AZURE_OPENAI_API_KEY` | Yes | Azure API key |
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

### Use as Azure OpenAI proxy

```bash
curl http://localhost:5000/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21 \
  -H "api-key: llimit-..." \
  -H "X-LLimit-User: alice" \
  -H "Content-Type: application/json" \
  -d '{"messages": [{"role": "user", "content": "Hello"}]}'
```

`X-LLimit-User` is optional (defaults to `_anonymous`).

### Dashboard

Visit `http://localhost:5000/dashboard/` and log in with the admin token.

## Docker

```bash
docker build -t llimit .
docker run -p 8080:8080 \
  -e AZURE_OPENAI_ENDPOINT=... \
  -e AZURE_OPENAI_API_KEY=... \
  -e LLIMIT_ADMIN_TOKEN=... \
  -v llimit-data:/data \
  llimit
```

## Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md) for full design details.
