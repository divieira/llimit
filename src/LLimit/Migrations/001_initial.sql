CREATE TABLE IF NOT EXISTS projects (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    api_key_hash TEXT NOT NULL UNIQUE,
    budget_daily    REAL,
    default_user_budget_daily   REAL,
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
    is_stream           INTEGER NOT NULL DEFAULT 0,
    used_fallback_pricing INTEGER NOT NULL DEFAULT 0
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

CREATE INDEX IF NOT EXISTS idx_request_log_project_ts ON request_log(project_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_request_log_user_ts ON request_log(project_id, user_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_usage_daily_project ON usage_daily(project_id, date);
CREATE INDEX IF NOT EXISTS idx_request_log_fallback ON request_log(used_fallback_pricing) WHERE used_fallback_pricing = 1;
