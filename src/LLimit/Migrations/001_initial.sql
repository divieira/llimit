CREATE TABLE IF NOT EXISTS projects (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    api_key_hash TEXT NOT NULL UNIQUE,
    endpoint_url TEXT NOT NULL,
    endpoint_key TEXT NOT NULL,
    budget_daily    REAL,
    default_user_budget_daily   REAL,
    allow_user_keys INTEGER NOT NULL DEFAULT 0,
    is_active   INTEGER NOT NULL DEFAULT 1,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS model_pricing (
    model_pattern       TEXT PRIMARY KEY,
    input_per_million   REAL NOT NULL,
    output_per_million  REAL NOT NULL,
    updated_at          TEXT NOT NULL
);

-- Cached LiteLLM prices, used as fallback when the online fetch fails.
-- Populated after each successful LiteLLM fetch.
CREATE TABLE IF NOT EXISTS litellm_prices (
    model               TEXT PRIMARY KEY,
    input_per_token     REAL NOT NULL,
    output_per_token    REAL NOT NULL,
    fetched_at          TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS request_log (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id          TEXT NOT NULL,
    user_id             TEXT,
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

CREATE TABLE IF NOT EXISTS usage_daily (
    project_id          TEXT NOT NULL,
    user_id             TEXT,
    date                TEXT NOT NULL,
    total_cost          REAL NOT NULL DEFAULT 0.0,
    prompt_tokens       INTEGER NOT NULL DEFAULT 0,
    completion_tokens   INTEGER NOT NULL DEFAULT 0,
    request_count       INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (project_id, user_id, date)
);

-- Users: populated on first OAuth login
CREATE TABLE IF NOT EXISTS users (
    id              TEXT PRIMARY KEY,
    email           TEXT NOT NULL UNIQUE,
    display_name    TEXT NOT NULL,
    created_at      TEXT NOT NULL,
    last_login      TEXT NOT NULL
);

-- One active key per user per project (enforced by partial unique index below)
CREATE TABLE IF NOT EXISTS user_api_keys (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id         TEXT NOT NULL REFERENCES users(id),
    project_id      TEXT NOT NULL REFERENCES projects(id),
    api_key_hash    TEXT NOT NULL UNIQUE,
    created_at      TEXT NOT NULL,
    revoked_at      TEXT
);

-- Portal sessions (token hash to user mapping)
CREATE TABLE IF NOT EXISTS user_sessions (
    token_hash      TEXT PRIMARY KEY,
    user_id         TEXT NOT NULL REFERENCES users(id),
    created_at      TEXT NOT NULL,
    expires_at      TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_request_log_project_ts ON request_log(project_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_request_log_user_ts ON request_log(project_id, user_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_usage_daily_project ON usage_daily(project_id, date);
CREATE INDEX IF NOT EXISTS idx_user_api_keys_hash ON user_api_keys(api_key_hash);
CREATE INDEX IF NOT EXISTS idx_user_api_keys_user ON user_api_keys(user_id);
CREATE INDEX IF NOT EXISTS idx_user_sessions_expires ON user_sessions(expires_at);

-- Allow only one active (non-revoked) key per user per project
CREATE UNIQUE INDEX IF NOT EXISTS idx_user_api_keys_active
ON user_api_keys(user_id, project_id) WHERE revoked_at IS NULL;
