-- v3 additive proposal. Execute only via host migrations, never automatically.
CREATE TABLE IF NOT EXISTS pwb_scene_state (
    file_id TEXT NOT NULL,
    row_id TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK(typeof(version)='integer' AND version >= 1),
    label_level TEXT NULL CHECK(label_level IS NULL OR label_level IN ('红','黄','绿')),
    breakdown_plan TEXT NULL,
    PRIMARY KEY(file_id,row_id)
);
CREATE TABLE IF NOT EXISTS pwb_scene_origins (
    file_id TEXT NOT NULL,
    row_id TEXT NOT NULL,
    origin_file_id TEXT NOT NULL,
    origin_row_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0 AND ordinal < 100),
    PRIMARY KEY(file_id,row_id,origin_file_id,origin_row_id),
    UNIQUE(file_id,row_id,ordinal),
    CHECK(file_id <> origin_file_id),
    FOREIGN KEY(file_id,row_id) REFERENCES pwb_scene_state(file_id,row_id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS pwb_selection_history (
    id TEXT PRIMARY KEY,
    file_id TEXT NOT NULL,
    row_id TEXT NOT NULL,
    previous_image_id TEXT NULL,
    image_id TEXT NULL,
    user_id TEXT NOT NULL,
    user_name TEXT NOT NULL,
    created_at TEXT NOT NULL,
    reason TEXT NULL
);
CREATE INDEX IF NOT EXISTS pwb_selection_history_scope ON pwb_selection_history(file_id,row_id,created_at,id);
CREATE TABLE IF NOT EXISTS pwb_operations (
    user_id TEXT NOT NULL,
    operation_id TEXT NOT NULL,
    operation_kind TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    response_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(user_id,operation_id,operation_kind)
);
