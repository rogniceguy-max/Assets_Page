-- Additive proposal; apply via the host's normal migration system, never on import.
-- Existing XLSX bytes and existing tables remain unchanged.
CREATE TABLE IF NOT EXISTS pwb_asset_row_state (
    file_id TEXT NOT NULL,
    row_id TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK(version >= 1),
    selected_image_id TEXT NULL,
    PRIMARY KEY(file_id, row_id)
);

CREATE TABLE IF NOT EXISTS pwb_asset_row_candidates (
    file_id TEXT NOT NULL,
    row_id TEXT NOT NULL,
    image_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK(ordinal >= 0 AND ordinal < 100),
    PRIMARY KEY(file_id, row_id, image_id),
    UNIQUE(file_id, row_id, ordinal),
    FOREIGN KEY(file_id, row_id) REFERENCES pwb_asset_row_state(file_id, row_id) ON DELETE CASCADE
);
-- Image IDs refer to the host's authorized asset catalog. This schema deliberately
-- does not assume its table names. ACL/existence are checked transactionally by hooks.
