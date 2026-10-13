-- OPTIONAL host-managed migration ONLY when actual v2 handoff tables contain data.
-- Apply reviews_v3_schema.sql first, stop old v2 writers, backup, then execute once
-- inside the host migration transaction. These statements deliberately fail on
-- conflicting destination IDs rather than silently dropping different data.
-- No BEGIN/COMMIT here: the host migration runner owns the transaction.
INSERT INTO pwb_review_votes_v3(file_id,row_id,row_kind,image_id,user_id,verdict)
SELECT file_id,row_id,'asset',image_id,user_id,verdict FROM pwb_review_votes;
INSERT INTO pwb_review_comments_v3(id,file_id,row_id,row_kind,scope_image_id,user_id,user_name,text,
    version,created_at,updated_at,resolved,resolution_note)
SELECT id,file_id,row_id,'asset',scope_image_id,user_id,user_name,text,version,created_at,updated_at,0,NULL
FROM pwb_review_comments;
-- Retain old tables until counts/IDs/content are checked and rollback window ends.
-- The unrelated legacy asset_feedback table is NOT read or converted here.
