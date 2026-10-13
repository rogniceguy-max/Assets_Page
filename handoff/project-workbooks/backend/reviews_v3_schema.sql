-- Fresh v3 review tables; no automatic data migration from historical asset_feedback.
CREATE TABLE IF NOT EXISTS pwb_review_votes_v3 (
    file_id TEXT NOT NULL, row_id TEXT NOT NULL,
    row_kind TEXT NOT NULL CHECK(row_kind='asset'),
    image_id TEXT NOT NULL CHECK(length(image_id)>0),
    user_id TEXT NOT NULL,
    verdict TEXT NOT NULL CHECK(verdict IN ('good','bad')),
    PRIMARY KEY(file_id,row_id,row_kind,image_id,user_id)
);
CREATE TABLE IF NOT EXISTS pwb_review_comments_v3 (
    id TEXT PRIMARY KEY, file_id TEXT NOT NULL, row_id TEXT NOT NULL,
    row_kind TEXT NOT NULL CHECK(row_kind IN ('asset','scene')),
    scope_image_id TEXT NOT NULL,
    user_id TEXT NOT NULL, user_name TEXT NOT NULL, text TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK(typeof(version)='integer' AND version>=1),
    created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
    resolved INTEGER NOT NULL DEFAULT 0 CHECK(resolved IN (0,1)),
    resolution_note TEXT NULL,
    CHECK(row_kind='asset' OR scope_image_id=''),
    CHECK(resolved=0 OR resolution_note IS NOT NULL)
);
-- Python enforces Unicode trim/nonempty/4000 characters. SQLite length/trim
-- have different U+0000/Unicode whitespace behavior and are not the text contract.
CREATE INDEX IF NOT EXISTS pwb_review_comments_v3_scope
    ON pwb_review_comments_v3(file_id,row_id,row_kind,scope_image_id,created_at,id);
