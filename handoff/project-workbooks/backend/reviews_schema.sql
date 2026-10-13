-- Additive proposal; apply only through host migrations, never at module import.
-- Deliberately no FK to selected/candidate links: unlinking an image retains reviews.
CREATE TABLE IF NOT EXISTS pwb_review_votes (
    file_id TEXT NOT NULL,
    row_id TEXT NOT NULL,
    image_id TEXT NOT NULL CHECK(length(image_id) > 0),
    user_id TEXT NOT NULL,
    verdict TEXT NOT NULL CHECK(verdict IN ('good', 'bad')),
    PRIMARY KEY(file_id, row_id, image_id, user_id)
);

CREATE TABLE IF NOT EXISTS pwb_review_comments (
    id TEXT PRIMARY KEY,
    file_id TEXT NOT NULL,
    row_id TEXT NOT NULL,
    scope_image_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    user_name TEXT NOT NULL,
    text TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK(typeof(version) = 'integer' AND version >= 1),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
-- scope_image_id='' represents general asset discussion internally; wire DTO uses null.
-- Unicode scalar length/trim/surrogate validation happens in review_text; SQLite
-- length() stops at U+0000 and therefore is not used as a different text contract.
CREATE INDEX IF NOT EXISTS pwb_review_comments_scope
    ON pwb_review_comments(file_id, row_id, scope_image_id, created_at, id);
