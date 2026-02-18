# llimit — LLM Cost Gateway Architecture

## Overview

llimit is a lightweight reverse-proxy in front of Azure OpenAI that:
- Transparently forwards requests to Azure with minimal overhead
- Tracks costs per **project** and per **user** independently
- Enforces **daily** budget limits per project and per user
- Issues one API key per project (callers use it instead of the real Azure key)
- Provides a web dashboard, log viewer, and project settings UI
- Fails loudly on unknown models (cost=$0, flagged) — no silent fallback pricing

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

## Request Flow

```
Client                      llimit                              Azure OpenAI
  │                           │                                      │
  │──POST /openai/deploy/X───>│  t0 = Stopwatch.Start()             │
  │   api-key: <proj-key>     │                                      │
  │   X-LLimit-User: alice    │  (optional — null if absent)         │
  │                           │                                      │
  │                           │──lookup project by key (in-memory)   │
  │                           │──check daily budget (DB query)───┐   │
  │                           │                                  │   │
  │                           │  if over budget:                 │   │
  │<──429 budget exceeded─────│<─────────────────────────────────┘   │
  │                           │                                      │
  │                           │  if ok:                              │
  │                           │  t1 = mark pre-forward               │
  │                           │──forward request────────────────────>│
  │                           │                                      │
  │                           │  t2 = first byte from Azure          │
  │                           │──copy response HEADERS to client     │
  │<──response headers────────│<──response headers──────────────────│
  │<──response body (stream)──│<──response body (stream)────────────│
  │                           │  t3 = last byte / response done      │
  │                           │                                      │
  │                           │──async: extract usage from body      │
  │                           │──async: calc cost (model→price)      │
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
│  AuthCache                                                       │
│    Dictionary<string, Project>  // keyed by SHA-256(api-key)     │
│    (volatile swap on reload)                                     │
│                                                                  │
│  PricingCache                                                    │
│    volatile Dictionary<string, ModelPrice>                        │
│    (loaded from DB overrides + LiteLLM, refreshed every 6h)     │
│                                                                  │
│  Diagnostics                                                     │
│    ConcurrentDictionary<string, int> UnknownModels               │
│    long AsyncFailures (Interlocked.Increment)                    │
│    long PricingRefreshFailures                                   │
└──────────────────────────────────────────────────────────────────┘
```

Budget checks query `usage_daily` directly — no in-memory cache, no drift.

## Lazy Budget Enforcement

1. Request arrives → check if **already** over daily budget (DB query) → reject or forward
2. Response returns → calculate cost → write to `usage_daily`
3. If that write pushes over budget → NEXT request gets blocked

User is optional. If `X-LLimit-User` header is absent, `uid` is null — per-user limits are skipped. DB storage uses `"_anonymous"` as the default user ID.

## Cost Calculation — LiteLLM-Backed Pricing

**Resolution:** Exact match only against the pricing dictionary.

1. Admin override (DB `model_pricing` table) — always wins
2. LiteLLM exact match (`"azure/{model}"`)
3. Unknown → cost=$0, `used_fallback_pricing=1`, increment `Diagnostics.UnknownModels[model]`

On startup: fetch LiteLLM pricing (fail if unavailable). Every 6h: background refresh.

## Database Schema

```sql
CREATE TABLE projects (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    api_key_hash TEXT NOT NULL UNIQUE,
    budget_daily    REAL,
    default_user_budget_daily   REAL,
    is_active   INTEGER NOT NULL DEFAULT 1,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

CREATE TABLE model_pricing (
    model_pattern       TEXT PRIMARY KEY,
    input_per_million   REAL NOT NULL,
    output_per_million  REAL NOT NULL,
    updated_at          TEXT NOT NULL
);

CREATE TABLE request_log (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id          TEXT NOT NULL,
    user_id             TEXT NOT NULL DEFAULT '_anonymous',
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
    is_stream           INTEGER NOT NULL DEFAULT 0,
    used_fallback_pricing INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE usage_daily (
    project_id          TEXT NOT NULL,
    user_id             TEXT NOT NULL DEFAULT '_anonymous',
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

Required: `api-key` header. Optional: `X-LLimit-User` header.

### Admin API (`Authorization: Bearer <admin-token>`)

```
GET/POST/PUT/DELETE  /api/v1/projects[/{id}]
GET    /api/v1/projects/{id}/users
GET    /api/v1/projects/{id}/usage?from=&to=
GET    /api/v1/projects/{id}/logs?page=&per_page=&user=&model=
GET/PUT/DELETE  /api/v1/pricing[/{model_pattern}]
GET    /api/v1/diagnostics
```

### Dashboard (HTML + htmx)

```
GET /dashboard/                     → Overview
GET /dashboard/projects/{id}        → Project detail + latency breakdown
GET /dashboard/projects/{id}/logs   → Log viewer
GET /dashboard/projects/{id}/settings → Budget settings
GET /dashboard/pricing              → Pricing table
```

### Health

```
GET /health → {"status": "ok"|"degraded", "db": "ok", "unknown_models": 0, "async_failures": 0}
```

## Project Structure

```
LLimit/
├── Program.cs          # Entry, DI, routing, config
├── Proxy.cs            # Forward + stream + usage extraction + latency tracking
├── Cache.cs            # AuthCache + PricingCache + Diagnostics
├── Store.cs            # SQLite: open, migrate, CRUD, log, usage_daily, budget queries
├── Admin.cs            # JSON API route handlers
├── Dashboard.cs        # HTML page handlers
├── wwwroot/style.css
├── Migrations/001_initial.sql
├── LLimit.csproj
├── Dockerfile
└── k8s/
```

## Key Decisions

- **C# (.NET 8 Minimal API)** — 2 NuGet packages, everything else built-in
- **Daily budgets only** — simple, no cache needed, direct DB queries
- **User is optional** — null if absent, per-user limits skipped for anonymous requests
- **LiteLLM pricing** — fetched on startup (fail if unavailable), refreshed every 6h, admin overrides win
- **Exact match pricing** — no fuzzy/prefix matching, unknown models get cost=$0 + flag
- **Latency breakdown** — 4 columns: overhead, upstream TTFB, transfer, total
- **Monitoring** — unknown model counter, async failure counter, exposed via diagnostics endpoint + health degradation
- **SQLite** on single K8s replica with PVC and Recreate strategy
