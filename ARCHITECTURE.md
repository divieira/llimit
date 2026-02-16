# llimit — LLM Cost Gateway Architecture

## Overview

llimit is a lightweight reverse-proxy that sits between your applications and Azure OpenAI APIs. It transparently forwards requests while tracking costs, enforcing budgets, and exposing usage through a dashboard.

```
┌──────────┐         ┌─────────────────────────────────────────────┐        ┌───────────────┐
│  Caller  │────────>│                  llimit                     │───────>│  Azure OpenAI │
│ (app/user)│<────────│                                             │<───────│     API       │
└──────────┘         │  ┌───────┐  ┌────────┐  ┌───────────────┐  │        └───────────────┘
                     │  │ Auth &│─>│  Cost  │─>│    Budget     │  │
                     │  │ Route │  │ Tracker│  │   Enforcer    │  │
                     │  └───────┘  └───┬────┘  └───────────────┘  │
                     │                 │                           │
                     │            ┌────▼────┐                     │
                     │            │   SQLite │                     │
                     │            │    DB    │                     │
                     │            └────┬────┘                     │
                     │                 │                           │
                     │           ┌─────▼──────┐                   │
                     │           │  Dashboard  │                   │
                     │           │  (Web UI)   │                   │
                     │           └─────────────┘                   │
                     └─────────────────────────────────────────────┘
```

## Tech Stack

| Layer          | Choice         | Rationale                                              |
|----------------|----------------|--------------------------------------------------------|
| Language       | **Python 3.12+** | Fast to build, good Azure SDK ecosystem              |
| Web framework  | **FastAPI**      | Async, high-throughput, OpenAPI docs for free         |
| Database       | **SQLite**       | Zero-ops, single-file, sufficient for single-node    |
| Migrations     | **Alembic**      | Standard SQLAlchemy migration tool                   |
| ORM            | **SQLAlchemy 2** | Async support, mature, pairs with Alembic            |
| Dashboard      | **Jinja2 + htmx** | Server-rendered, no JS build step, minimal deps    |
| HTTP client    | **httpx**        | Async, streaming support for proxying SSE            |
| Config         | **Pydantic Settings** | Typed config from env vars / .env file          |
| Testing        | **pytest + pytest-asyncio** | Standard Python async test stack         |
| Packaging      | **Docker**       | Single container deployment                          |

## Core Concepts

### Caller Identity

Every request must include a custom header:

```
X-LLimit-Caller: <caller_id>
```

A `caller_id` is a free-form string (e.g., `team-search`, `user:alice`, `project-copilot`).
Requests without this header are rejected with `400`.

### Cost Calculation

Token costs are computed from the Azure OpenAI response:

1. Read `usage.prompt_tokens` and `usage.completion_tokens` from the response body.
2. Look up the per-token price for the model (from a static config table, keyed by deployment name).
3. `cost = prompt_tokens * input_price + completion_tokens * output_price`

For streaming responses, token counts come from the final chunk's `usage` field (Azure returns this when `stream_options: {"include_usage": true}` is set — the proxy injects this automatically).

### Budget Enforcement

Budgets are defined per caller in config:

```yaml
budgets:
  team-search:
    daily:  5.00    # USD
    weekly: 25.00
  default:
    daily:  2.00
    weekly: 10.00
```

Before forwarding a request, the proxy checks the caller's accumulated spend for the current day and week. If either limit is exceeded, the request is rejected with `429 Too Many Requests` and a JSON body explaining the reason.

### Request Flow

```
1. Receive request
2. Extract X-LLimit-Caller header (reject if missing)
3. Check budget (reject with 429 if exceeded)
4. Strip llimit-specific headers
5. Forward request to Azure OpenAI (streaming or non-streaming)
6. Read response, extract token usage
7. Calculate cost, write usage record to DB
8. Return response to caller
```

## Data Model

### Tables

```sql
-- Static model pricing (seeded from config)
CREATE TABLE model_pricing (
    deployment_name  TEXT PRIMARY KEY,
    input_per_1k     REAL NOT NULL,  -- USD per 1K input tokens
    output_per_1k    REAL NOT NULL   -- USD per 1K output tokens
);

-- Every request gets a log row
CREATE TABLE request_log (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    caller_id       TEXT NOT NULL,
    timestamp       TEXT NOT NULL,     -- ISO 8601 UTC
    deployment_name TEXT NOT NULL,
    endpoint        TEXT NOT NULL,     -- e.g., /chat/completions
    prompt_tokens   INTEGER NOT NULL DEFAULT 0,
    completion_tokens INTEGER NOT NULL DEFAULT 0,
    cost_usd        REAL NOT NULL DEFAULT 0.0,
    status_code     INTEGER NOT NULL,
    latency_ms      INTEGER NOT NULL DEFAULT 0
);

-- Materialized daily rollups (updated on each request)
CREATE TABLE daily_usage (
    caller_id  TEXT NOT NULL,
    date       TEXT NOT NULL,          -- YYYY-MM-DD
    total_cost REAL NOT NULL DEFAULT 0.0,
    request_count INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (caller_id, date)
);
```

Indexes on `request_log(caller_id, timestamp)` and `daily_usage(caller_id, date)` for fast budget checks and dashboard queries.

## Project Structure

```
llimit/
├── alembic/                   # DB migrations
│   └── versions/
├── llimit/
│   ├── __init__.py
│   ├── main.py                # FastAPI app, lifespan, mount routes
│   ├── config.py              # Pydantic Settings (env vars, budgets, pricing)
│   ├── proxy.py               # Core proxy logic: forward, stream, extract usage
│   ├── budget.py              # Budget checking and enforcement
│   ├── cost.py                # Token cost calculation
│   ├── db.py                  # SQLAlchemy engine, session factory
│   ├── models.py              # SQLAlchemy ORM models
│   ├── middleware.py          # Caller extraction, request logging
│   ├── dashboard/
│   │   ├── routes.py          # Dashboard HTTP routes
│   │   └── templates/
│   │       ├── base.html
│   │       ├── index.html     # Overview: all callers, totals
│   │       └── caller.html    # Per-caller detail view
│   └── static/
│       └── style.css
├── tests/
│   ├── conftest.py
│   ├── test_proxy.py
│   ├── test_budget.py
│   ├── test_cost.py
│   └── test_dashboard.py
├── alembic.ini
├── pyproject.toml
├── Dockerfile
├── config.yaml                # Budget & pricing config
├── .env.example               # Azure credentials template
└── README.md
```

## Configuration

All via environment variables (with `.env` support):

| Variable                  | Description                          | Example                              |
|---------------------------|--------------------------------------|--------------------------------------|
| `AZURE_OPENAI_ENDPOINT`  | Azure OpenAI base URL                | `https://myorg.openai.azure.com`     |
| `AZURE_OPENAI_API_KEY`   | Azure API key                        | `sk-...`                             |
| `AZURE_OPENAI_API_VERSION` | API version                        | `2024-10-21`                         |
| `LLIMIT_DB_PATH`         | SQLite file path                     | `./llimit.db`                        |
| `LLIMIT_CONFIG_PATH`     | Path to config.yaml                  | `./config.yaml`                      |
| `LLIMIT_PORT`            | Port to listen on                    | `8000`                               |

## API Surface

### Proxy endpoints (pass-through to Azure)

The proxy forwards any path under `/openai/deployments/` to Azure:

```
POST /openai/deployments/{deployment}/chat/completions
POST /openai/deployments/{deployment}/completions
POST /openai/deployments/{deployment}/embeddings
```

Callers use llimit exactly as they would use Azure OpenAI, except:
- Point the base URL to llimit instead of Azure
- Add `X-LLimit-Caller: <caller_id>` header

### Dashboard endpoints

```
GET /dashboard/                    # Overview of all callers
GET /dashboard/caller/{caller_id}  # Detail for one caller
GET /dashboard/api/usage           # JSON API for usage data
```

### Admin endpoints

```
GET  /health                       # Health check
```

## Streaming Support

For streaming (`stream: true`) requests:

1. The proxy adds `stream_options: {"include_usage": true}` to the request body if not present.
2. It forwards SSE chunks to the caller in real-time using `StreamingResponse`.
3. It captures the final chunk containing `usage` data.
4. After the stream ends, it logs cost asynchronously.

This ensures callers see tokens as they arrive with no added latency.

## Dashboard

Server-rendered HTML with htmx for interactivity:

- **Overview page**: Table of all callers with daily/weekly spend, budget utilization bars, request counts.
- **Caller detail page**: Daily cost chart (last 30 days), request log table with search/filter, budget status.
- **Auto-refresh**: htmx polls every 30s for live updates.

No JavaScript build step. No SPA. Just HTML templates and htmx.

## Implementation Plan

### Phase 1: Foundation
1. Project scaffolding (pyproject.toml, directory structure, dependencies)
2. Configuration module (Pydantic Settings, config.yaml loader)
3. Database setup (SQLAlchemy models, Alembic initial migration)
4. Health check endpoint

### Phase 2: Core Proxy
5. Non-streaming proxy (forward request, read response, return)
6. Token usage extraction and cost calculation
7. Request logging to DB
8. Streaming proxy with SSE forwarding and usage capture

### Phase 3: Budget Enforcement
9. Budget checking logic (daily/weekly lookups)
10. Budget enforcement middleware (reject with 429)
11. Daily usage rollup table maintenance

### Phase 4: Dashboard
12. Overview page (all callers, spend summaries)
13. Caller detail page (history, charts)
14. JSON API endpoint for programmatic access

### Phase 5: Packaging & Polish
15. Dockerfile
16. README with setup instructions
17. Tests for proxy, budget, cost, dashboard
