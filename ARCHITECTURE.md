# llimit — LLM Cost Gateway Architecture

## Overview

llimit is a lightweight reverse-proxy in front of Azure OpenAI that:
- Transparently forwards requests to Azure with minimal overhead
- Tracks costs per **project** and per **user** independently
- Enforces **daily** budget limits per project and per user
- Issues one API key per project (callers use it instead of the real Azure key)
- Supports **personal user API keys** via OAuth (Azure AD/Entra) login
- Provides a web dashboard, log viewer, user portal, and project settings UI
- Rejects requests for unknown models before forwarding (if model in request body)
- Each project has its own Azure OpenAI **endpoint URL and API key**

Key design constraints:
- **Core must be as lean as possible** — proxy logic separate from calculation/budget
- **Budget enforcement is lazy** — block on the *next* request, not mid-request
- **Direct DB queries for budget checks** — simple, correct, no cache drift
- **Single K8s replica is fine** — SQLite with a PVC, accept brief redeploy downtime

## Tech Stack: C# (.NET 8 Minimal API)

| Need              | Library                          | Rationale                                    |
|-------------------|----------------------------------|----------------------------------------------|
| HTTP framework    | ASP.NET Minimal API              | Built-in, no extra package                   |
| SQLite            | `Microsoft.Data.Sqlite`          | Ships with ASP.NET                           |
| SQL mapping       | `Dapper`                         | Micro-ORM, thin SQL wrapper                  |
| HTTP client       | `IHttpClientFactory`             | Built-in, connection pooling                 |
| Config            | `IConfiguration`                 | Built-in, env vars + JSON                    |

2 external NuGet packages. ASP.NET provides HTTP, JSON, DI, hosting, `IHttpClientFactory`.
OAuth is implemented manually (authorization code flow) with no additional libraries.

## Authentication

### Project API Keys
Each project has a shared API key (`llimit-...`) for team use. Callers include it in the `api-key` header.
The optional `X-LLimit-User` header enables per-user tracking and budget enforcement.

### User API Keys (OAuth)
Users sign in via Azure AD/Entra at `/portal/login` and can generate a personal API key (`llimit-u-...`)
for any project where the admin has enabled `allow_user_keys`. When using a personal key:
- No `X-LLimit-User` header is needed — the user is derived from the key
- Per-user budget limits are enforced automatically
- Usage is tracked under the user's corporate identity

The username is the part before `@` of the corporate email (e.g., `alice@contoso.com` → `alice`).

### Proxy Key Resolution Order
1. Try as **project key** → if found, extract optional `X-LLimit-User` header
2. Try as **user key** → if found, derive project + user from key
3. Check inactive projects → 403 if deactivated
4. Return 401 if no match

## Request Flow

```
Client                      llimit                              Azure OpenAI
  │                           │                                      │
  │──POST /openai/deploy/X───>│  t0 = Stopwatch.Start()             │
  │   api-key: <key>          │                                      │
  │                           │──resolve key (project or user, DB)   │
  │                           │──check daily budget (DB query)───┐   │
  │                           │                                  │   │
  │                           │  if over budget:                 │   │
  │<──429 budget exceeded─────│<─────────────────────────────────┘   │
  │                           │                                      │
  │                           │  if model in body & no pricing:      │
  │<──422 unknown_model───────│                                      │
  │                           │                                      │
  │                           │  if ok:                              │
  │                           │  t1 = mark pre-forward               │
  │                           │──forward to project endpoint────────>│
  │                           │  (uses project's endpoint_url/key)   │
  │                           │                                      │
  │                           │  t2 = first byte from Azure          │
  │                           │──copy response HEADERS to client     │
  │<──response headers────────│<──response headers──────────────────│
  │<──response body (stream)──│<──response body (stream)────────────│
  │                           │  t3 = last byte / response done      │
  │                           │                                      │
  │                           │──sync: extract usage from body       │
  │                           │──sync: calc cost (model→price)       │
  │                           │──async: write to SQLite              │
  │                           │──async: on failure → log + increment │
  │                           │         Diagnostics.AsyncFailures    │
```

## Latency Tracking

Four stopwatch marks per request, three stored columns:

```
t0 ─── request received
 │  auth + budget check + body read + stream_options inject
t1 ─── HttpClient.SendAsync() called
 │  network round-trip to Azure (TTFB for streaming)
t2 ─── first byte from Azure (headers arrive)
 │  body transfer / SSE streaming
t3 ─── last byte / response fully sent to client
```

| Column             | Formula      | What it measures                                  |
|--------------------|--------------|---------------------------------------------------|
| `upstream_ms`      | `t2 - t1`    | Azure processing + network RTT (time-to-first-byte) |
| `transfer_ms`      | `t3 - t2`    | Body/stream transfer time (0 for non-stream)      |
| `overhead_ms`      | `t1 - t0`    | Our pre-forward work (auth, budget, body parse)   |
| `total_ms`         | `t3 - t0`    | End-to-end from client's perspective              |

For **non-streaming**: `t2 ≈ t3` — `transfer_ms ≈ 0`, `upstream_ms` captures the full Azure round-trip.
For **streaming**: `upstream_ms` is TTFB (~200ms), `transfer_ms` is the SSE streaming duration.

## In-Memory State

```
┌──────────────────────────────────────────────────────────────────┐
│                   In-Memory State                                │
│                                                                  │
│  PricingTable                                                    │
│    volatile Dictionary<string, ModelPrice>                        │
│    (loaded from LiteLLM, DB fallback, refreshed every 6h)       │
│    Admin overrides always win.                                   │
│                                                                  │
│  Diagnostics                                                     │
│    ConcurrentDictionary<string, int> UnknownModels               │
│    long AsyncFailures (Interlocked.Increment)                    │
│    long PricingRefreshFailures                                   │
└──────────────────────────────────────────────────────────────────┘
```

API key resolution queries SQLite directly — no in-memory cache, no drift.
Budget checks query `usage_daily` directly — no in-memory cache, no drift.

## Lazy Budget Enforcement

1. Request arrives → check if **already** over daily budget (DB query) → reject or forward
2. Response returns → calculate cost → write to `usage_daily`
3. If that write pushes over budget → NEXT request gets blocked

User is optional with project keys. If `X-LLimit-User` header is absent, `uid` is null — per-user limits are skipped.
User is always known with personal keys — derived from the key's owner.

## Cost Calculation — LiteLLM-Backed Pricing

**Resolution:** Exact match only against the pricing dictionary.

1. Admin override (DB `model_pricing` table) — always wins
2. LiteLLM exact match (`"azure/{model}"`)
3. Unknown → cost=$0, increment `Diagnostics.UnknownModels[model]`, log warning

**Pricing lifecycle:**
- Startup: fetch from LiteLLM online → save to `litellm_prices` table → apply admin overrides
- If LiteLLM fetch fails: load from `litellm_prices` DB cache (fallback)
- Every 6h: background refresh from LiteLLM, save to DB on success

**Pre-validation:** If the request body contains a `model` field (OpenAI SDK clients include it),
the proxy checks pricing before forwarding. Unknown models get 422.

## Database Schema

```sql
CREATE TABLE projects (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    api_key_hash TEXT NOT NULL UNIQUE,
    endpoint_url TEXT NOT NULL,           -- Azure OpenAI endpoint for this project
    endpoint_key TEXT NOT NULL,           -- Azure OpenAI API key for this project
    budget_daily    REAL,
    default_user_budget_daily   REAL,
    allow_user_keys INTEGER NOT NULL DEFAULT 0,  -- allow OAuth users to generate personal keys
    is_active   INTEGER NOT NULL DEFAULT 1,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

CREATE TABLE users (                     -- populated on first OAuth login
    id              TEXT PRIMARY KEY,     -- username (email prefix)
    email           TEXT NOT NULL UNIQUE,
    display_name    TEXT NOT NULL,
    created_at      TEXT NOT NULL,
    last_login      TEXT NOT NULL
);

CREATE TABLE user_api_keys (             -- one active key per user per project
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id         TEXT NOT NULL REFERENCES users(id),
    project_id      TEXT NOT NULL REFERENCES projects(id),
    api_key_hash    TEXT NOT NULL UNIQUE,
    created_at      TEXT NOT NULL,
    revoked_at      TEXT                 -- null = active, non-null = revoked
);

CREATE TABLE user_sessions (             -- portal login sessions
    token_hash      TEXT PRIMARY KEY,
    user_id         TEXT NOT NULL REFERENCES users(id),
    created_at      TEXT NOT NULL,
    expires_at      TEXT NOT NULL
);

CREATE TABLE model_pricing (             -- admin overrides
    model_pattern       TEXT PRIMARY KEY,
    input_per_million   REAL NOT NULL,
    output_per_million  REAL NOT NULL,
    updated_at          TEXT NOT NULL
);

CREATE TABLE litellm_prices (            -- cached LiteLLM prices (fallback)
    model               TEXT PRIMARY KEY,
    input_per_token     REAL NOT NULL,
    output_per_token    REAL NOT NULL,
    fetched_at          TEXT NOT NULL
);

CREATE TABLE request_log (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id          TEXT NOT NULL,
    user_id             TEXT,             -- null if no user header / anonymous
    timestamp           TEXT NOT NULL,
    model               TEXT NOT NULL,
    deployment          TEXT NOT NULL,
    endpoint            TEXT NOT NULL,
    prompt_tokens       INTEGER NOT NULL DEFAULT 0,
    completion_tokens   INTEGER NOT NULL DEFAULT 0,
    total_tokens        INTEGER NOT NULL DEFAULT 0,
    cost_usd            REAL NOT NULL DEFAULT 0.0,
    status_code         INTEGER NOT NULL,
    overhead_ms         INTEGER NOT NULL DEFAULT 0,
    upstream_ms         INTEGER NOT NULL DEFAULT 0,
    transfer_ms         INTEGER NOT NULL DEFAULT 0,
    total_ms            INTEGER NOT NULL DEFAULT 0,
    is_stream           INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE usage_daily (
    project_id          TEXT NOT NULL,
    user_id             TEXT,
    date                TEXT NOT NULL,
    total_cost          REAL NOT NULL DEFAULT 0.0,
    prompt_tokens       INTEGER NOT NULL DEFAULT 0,
    completion_tokens   INTEGER NOT NULL DEFAULT 0,
    request_count       INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (project_id, user_id, date)
);
```

## API Surface

### Proxy (pass-through to Azure)

```
POST /openai/deployments/{deployment}/chat/completions?api-version={ver}
POST /openai/deployments/{deployment}/completions?api-version={ver}
POST /openai/deployments/{deployment}/embeddings?api-version={ver}
```

Required: `api-key` header (project key or user key). Optional: `X-LLimit-User` header (project keys only).

### Admin API (`Authorization: Bearer <admin-token>`)

```
GET/POST/PUT/DELETE  /api/v1/projects[/{id}]
GET    /api/v1/projects/{id}/users
GET    /api/v1/projects/{id}/usage?from=&to=
GET    /api/v1/projects/{id}/logs?page=&per_page=&user=&model=
GET/PUT/DELETE  /api/v1/pricing[/{model_pattern}]
GET    /api/v1/diagnostics
```

### Dashboard (HTML + htmx, admin auth)

```
GET /dashboard/                     → Overview
GET /dashboard/projects/{id}        → Project detail + latency breakdown
GET /dashboard/projects/{id}/logs   → Log viewer
GET /dashboard/projects/{id}/settings → Project settings (endpoint, budgets, user keys)
GET /dashboard/pricing              → Pricing table
```

### User Portal (HTML, OAuth auth)

```
GET  /portal/login                   → Login page (Sign in with Microsoft)
GET  /portal/                        → Project list (self-service enabled projects)
GET  /portal/projects/{id}           → Project detail + key management + usage
POST /portal/projects/{id}/keys      → Create personal API key
POST /portal/projects/{id}/keys/revoke → Revoke personal API key
```

### OAuth

```
GET /auth/login     → Redirect to Azure AD authorize endpoint
GET /auth/callback  → Handle OAuth callback, create user + session
GET /auth/logout    → Clear session, redirect to login
```

### Health

```
GET /health → {"status": "ok"|"degraded", "db": "ok", "unknown_models": 0, "async_failures": 0}
```

## Project Structure

```
LLimit/
├── Program.cs          # Entry, DI, routing, startup validation, pricing load with fallback
├── Proxy.cs            # Forward + stream + usage extraction + cost calc + latency tracking
├── Pricing.cs          # PricingTable (LiteLLM + DB fallback + admin overrides) + Diagnostics
├── Store.cs            # SQLite: open, migrate, CRUD, log, usage_daily, budget + key resolution
├── Admin.cs            # JSON API route handlers
├── Dashboard.cs        # Admin HTML page handlers (PicoCSS + htmx)
├── OAuth.cs            # OAuth login + user portal HTML handlers
├── wwwroot/style.css
├── Migrations/001_initial.sql
├── Properties/launchSettings.json  # Local dev profile (port 5125, Development env)
├── LLimit.csproj
├── Dockerfile
└── k8s/                # Deployment, Service, PVC with docs
```

## Configuration

| Variable                | Required   | Description                                                       |
|------------------------|------------|-------------------------------------------------------------------|
| `LLIMIT_ADMIN_TOKEN`   | Yes        | Bearer token for admin API and dashboard login                    |
| `LLIMIT_DB_PATH`       | No         | SQLite database path (default: `llimit.db`)                       |
| `AZURE_AD_CLIENT_ID`   | For OAuth  | Azure AD app registration client ID                               |
| `AZURE_AD_CLIENT_SECRET`| For OAuth | Azure AD app registration client secret                           |
| `AZURE_AD_TENANT_ID`   | For OAuth  | Azure AD tenant ID                                                |
| `AZURE_AD_CORPORATE_DOMAIN` | For OAuth | Allowed email domain for login (e.g., `contoso.com`)         |

Each project has its own Azure OpenAI endpoint URL and API key, configured at project creation time.

**Local development** uses `Properties/launchSettings.json` which sets `ASPNETCORE_ENVIRONMENT=Development`
and listens on port 5125. See: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/environments

## Key Decisions

- **C# (.NET 8 Minimal API)** — 2 NuGet packages, everything else built-in
- **Per-project endpoints** — each project stores its own Azure OpenAI URL and key; no global fallback
- **OAuth without libraries** — authorization code flow implemented with HttpClient; JWT payload decoded without verification (trusted since received from Azure AD over TLS)
- **Personal user keys** — `llimit-u-...` prefix; one active key per user per project (enforced by partial unique index)
- **Session tokens** — random 32-byte token in cookie, SHA-256 hash in DB; 7-day expiry; cleaned hourly
- **Daily budgets only** — simple, no cache needed, direct DB queries
- **User is optional** — null if absent (project keys), always known (user keys)
- **LiteLLM pricing with DB fallback** — fetched on startup, saved to DB; falls back to DB cache if fetch fails; refreshed every 6h
- **Exact match pricing** — no fuzzy/prefix matching, unknown models get cost=$0 + diagnostics flag
- **Pre-validate model** — if `model` field in request body, check pricing before forwarding (422 if unknown)
- **Cost calc in request path** — pricing lookup is synchronous; only DB writes are fire-and-forget
- **Usage extraction throws** — `GetProperty("usage")` instead of TryGet; log errors on failure
- **No fallback pricing** — unknown models are flagged, not silently estimated
- **Latency breakdown** — 4 columns: overhead, upstream TTFB, transfer, total
- **Monitoring** — unknown model counter, async failure counter, exposed via diagnostics endpoint + health degradation
- **SQLite** on single K8s replica with PVC and Recreate strategy
