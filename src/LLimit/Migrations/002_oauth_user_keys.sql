-- Per-project Azure endpoint override (optional; falls back to global AZURE_OPENAI_* if NULL)
ALTER TABLE projects ADD COLUMN endpoint_url TEXT;
ALTER TABLE projects ADD COLUMN endpoint_key TEXT;

-- When true, users can generate a personal API key for this project via the portal
ALTER TABLE projects ADD COLUMN allow_user_keys INTEGER NOT NULL DEFAULT 0;

-- OAuth-authenticated users (registered via Azure AD sign-in)
-- `id` is the derived username: the part before @ for corporate-domain users, full email otherwise
CREATE TABLE IF NOT EXISTS users (
    id           TEXT PRIMARY KEY,
    email        TEXT UNIQUE NOT NULL,
    display_name TEXT NOT NULL,
    created_at   TEXT NOT NULL
);

-- One personal API key per user per project
CREATE TABLE IF NOT EXISTS user_keys (
    id           TEXT PRIMARY KEY,
    user_id      TEXT NOT NULL REFERENCES users(id),
    project_id   TEXT NOT NULL REFERENCES projects(id),
    api_key_hash TEXT UNIQUE NOT NULL,
    created_at   TEXT NOT NULL,
    UNIQUE(user_id, project_id)
);

CREATE INDEX IF NOT EXISTS idx_user_keys_user ON user_keys(user_id);
