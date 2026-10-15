use std::{path::Path, sync::Mutex};

use anyhow::{Context, Result};
use muying_transfer_common::protocol::StagedObjectReceipt;
use rusqlite::{Connection, OptionalExtension, params};

#[derive(Clone, Debug)]
pub struct UploadRow {
    pub upload_id: String,
    pub op_id: String,
    pub object_id: String,
    pub object_kind: String,
    pub user_id: String,
    pub expected_version: i64,
    pub size: u64,
    pub sha256: String,
    pub chunk_size: u64,
    pub state: String,
    pub receipt: Option<StagedObjectReceipt>,
    pub created_at: i64,
}

#[derive(Clone, Debug)]
pub struct DownloadRow {
    pub download_id: String,
    pub op_id: String,
    pub object_id: String,
    pub object_kind: String,
    pub user_id: String,
    pub size: u64,
    pub sha256: String,
    pub exp: i64,
}

pub struct TransferStore {
    connection: Mutex<Connection>,
}

impl TransferStore {
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        if let Some(parent) = path.as_ref().parent() {
            std::fs::create_dir_all(parent).context("create transfer state directory")?;
        }
        let connection = Connection::open(path).context("open transfer service state")?;
        connection.pragma_update(None, "journal_mode", "WAL")?;
        connection.pragma_update(None, "synchronous", "FULL")?;
        connection.execute_batch(
            "CREATE TABLE IF NOT EXISTS uploads (
                upload_id TEXT PRIMARY KEY,
                op_id TEXT NOT NULL UNIQUE,
                object_id TEXT NOT NULL,
                object_kind TEXT NOT NULL,
                user_id TEXT NOT NULL,
                expected_version INTEGER NOT NULL,
                size INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                chunk_size INTEGER NOT NULL,
                state TEXT NOT NULL,
                receipt_json TEXT,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                cleanup_pending INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS upload_chunks (
                upload_id TEXT NOT NULL REFERENCES uploads(upload_id) ON DELETE CASCADE,
                start_offset INTEGER NOT NULL,
                end_offset INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                PRIMARY KEY(upload_id,start_offset)
            );
            CREATE INDEX IF NOT EXISTS idx_uploads_state ON uploads(state,updated_at);
            CREATE TABLE IF NOT EXISTS downloads (
                download_id TEXT PRIMARY KEY,
                op_id TEXT NOT NULL,
                object_id TEXT NOT NULL,
                object_kind TEXT NOT NULL,
                user_id TEXT NOT NULL,
                size INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                exp INTEGER NOT NULL,
                created_at INTEGER NOT NULL
            );",
        )?;
        ensure_column(&connection, "uploads", "object_kind", "TEXT NOT NULL DEFAULT 'other'")?;
        ensure_column(&connection, "downloads", "object_kind", "TEXT NOT NULL DEFAULT 'other'")?;
        ensure_column(
            &connection,
            "uploads",
            "cleanup_pending",
            "INTEGER NOT NULL DEFAULT 0",
        )?;
        migrate_user_id_to_text(&connection, "uploads")?;
        migrate_user_id_to_text(&connection, "downloads")?;
        Ok(Self {
            connection: Mutex::new(connection),
        })
    }

    pub fn ensure_upload(
        &self,
        op_id: &str,
        object_id: &str,
        object_kind: &str,
        user_id: &str,
        expected_version: i64,
        size: u64,
        sha256: &str,
        chunk_size: u64,
        max_staging_bytes_per_user: u64,
        max_receiving_uploads_per_user: usize,
        max_staging_bytes_total: u64,
        max_receiving_uploads_total: usize,
    ) -> Result<UploadRow> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        let existing = connection.query_row(
            "SELECT upload_id,op_id,object_id,object_kind,user_id,expected_version,size,sha256,chunk_size,state,receipt_json,created_at
             FROM uploads WHERE op_id=?1",
            [op_id],
            decode_upload,
        ).optional()?;
        if let Some(row) = existing {
            if row.state == "aborted" {
                anyhow::bail!("upload_aborted");
            }
            if row.state == "expired" {
                anyhow::bail!("upload_expired");
            }
            if row.object_id != object_id
                || row.object_kind != object_kind
                || row.user_id != user_id
                || row.expected_version != expected_version
                || row.size != size
                || row.sha256 != sha256
            {
                anyhow::bail!("operation_id is already bound to a different upload manifest");
            }
            return Ok(row);
        }
        let (reserved_bytes, receiving_uploads): (i64, i64) = connection.query_row(
            "SELECT COALESCE(SUM(size),0),COUNT(*) FROM uploads WHERE user_id=?1 AND state='receiving'",
            [user_id],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )?;
        if receiving_uploads as u64 >= max_receiving_uploads_per_user as u64
            || (reserved_bytes.max(0) as u64).saturating_add(size)
                > max_staging_bytes_per_user
        {
            anyhow::bail!("staging_quota_exceeded");
        }
        let (total_reserved_bytes, total_receiving_uploads): (i64, i64) = connection.query_row(
            "SELECT COALESCE(SUM(size),0),COUNT(*) FROM uploads WHERE state='receiving'",
            [],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )?;
        if total_receiving_uploads as u64 >= max_receiving_uploads_total as u64
            || (total_reserved_bytes.max(0) as u64).saturating_add(size)
                > max_staging_bytes_total
        {
            anyhow::bail!("staging_quota_exceeded");
        }
        let now = unix_now();
        connection.execute(
            "INSERT INTO uploads(upload_id,op_id,object_id,object_kind,user_id,expected_version,size,sha256,chunk_size,state,created_at,updated_at)
             VALUES(?1,?1,?2,?3,?4,?5,?6,?7,?8,'receiving',?9,?9)",
            params![op_id, object_id, object_kind, user_id, expected_version, size as i64, sha256, chunk_size as i64, now],
        )?;
        Ok(UploadRow {
            upload_id: op_id.to_owned(),
            op_id: op_id.to_owned(),
            object_id: object_id.to_owned(),
            object_kind: object_kind.to_owned(),
            user_id: user_id.to_owned(),
            expected_version,
            size,
            sha256: sha256.to_owned(),
            chunk_size,
            state: "receiving".into(),
            receipt: None,
            created_at: now,
        })
    }

    pub fn upload(&self, upload_id: &str) -> Result<Option<UploadRow>> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        connection.query_row(
            "SELECT upload_id,op_id,object_id,object_kind,user_id,expected_version,size,sha256,chunk_size,state,receipt_json,created_at
             FROM uploads WHERE upload_id=?1",
            [upload_id],
            decode_upload,
        ).optional().context("read upload session")
    }

    pub fn abort_upload(&self, upload_id: &str) -> Result<String> {
        let mut connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        let transaction = connection.transaction()?;
        let state: Option<String> = transaction
            .query_row(
                "SELECT state FROM uploads WHERE upload_id=?1",
                [upload_id],
                |row| row.get(0),
            )
            .optional()?;
        let Some(state) = state else {
            anyhow::bail!("upload_not_found");
        };
        if state == "receiving" {
            transaction.execute(
                "UPDATE uploads SET state='aborted',cleanup_pending=1,updated_at=?2 WHERE upload_id=?1",
                params![upload_id, unix_now()],
            )?;
            transaction.execute(
                "DELETE FROM upload_chunks WHERE upload_id=?1",
                [upload_id],
            )?;
        }
        transaction.commit()?;
        Ok(state_for_abort(&state).to_owned())
    }

    pub fn record_aborted_upload(
        &self,
        op_id: &str,
        object_id: &str,
        object_kind: &str,
        user_id: &str,
        expected_version: i64,
        size: u64,
        sha256: &str,
        chunk_size: u64,
    ) -> Result<()> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        let now = unix_now();
        connection.execute(
            "INSERT OR IGNORE INTO uploads(upload_id,op_id,object_id,object_kind,user_id,expected_version,
                size,sha256,chunk_size,state,created_at,updated_at,cleanup_pending)
             VALUES(?1,?1,?2,?3,?4,?5,?6,?7,?8,'aborted',?9,?9,1)",
            params![
                op_id,
                object_id,
                object_kind,
                user_id,
                expected_version,
                size as i64,
                sha256,
                chunk_size as i64,
                now
            ],
        )?;
        Ok(())
    }

    pub fn expire_receiving_upload(&self, upload_id: &str, cutoff: i64) -> Result<bool> {
        let mut connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        let transaction = connection.transaction()?;
        let changed = transaction.execute(
            "UPDATE uploads SET state='expired',cleanup_pending=1,updated_at=?3
             WHERE upload_id=?1 AND state='receiving' AND updated_at<=?2",
            params![upload_id, cutoff, unix_now()],
        )?;
        if changed > 0 {
            transaction.execute(
                "DELETE FROM upload_chunks WHERE upload_id=?1",
                [upload_id],
            )?;
        }
        transaction.commit()?;
        Ok(changed > 0)
    }

    pub fn uploads_for_cleanup(&self, cutoff: i64, limit: usize) -> Result<Vec<UploadRow>> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        let mut statement = connection.prepare(
            "SELECT upload_id,op_id,object_id,object_kind,user_id,expected_version,size,sha256,chunk_size,state,receipt_json,created_at
             FROM uploads WHERE (state='receiving' AND updated_at<=?1)
                OR (state IN ('aborted','expired') AND cleanup_pending=1)
             ORDER BY updated_at LIMIT ?2",
        )?;
        let rows = statement.query_map(params![cutoff, limit as i64], decode_upload)?;
        Ok(rows.collect::<rusqlite::Result<Vec<_>>>()?)
    }

    pub fn mark_upload_cleanup_complete(&self, upload_id: &str) -> Result<()> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        connection.execute(
            "UPDATE uploads SET cleanup_pending=0 WHERE upload_id=?1 AND state IN ('aborted','expired')",
            [upload_id],
        )?;
        Ok(())
    }

    pub fn purge_expired_downloads(&self, now: i64) -> Result<usize> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        Ok(connection.execute("DELETE FROM downloads WHERE exp<=?1", [now])?)
    }

    pub fn chunk_hash(&self, upload_id: &str, start: u64) -> Result<Option<String>> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        connection
            .query_row(
                "SELECT sha256 FROM upload_chunks WHERE upload_id=?1 AND start_offset=?2",
                params![upload_id, start as i64],
                |row| row.get(0),
            )
            .optional()
            .context("read upload chunk")
    }

    pub fn record_chunk(&self, upload_id: &str, start: u64, end: u64, sha256: &str) -> Result<()> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        connection.execute(
            "INSERT INTO upload_chunks(upload_id,start_offset,end_offset,sha256) VALUES(?1,?2,?3,?4)
             ON CONFLICT(upload_id,start_offset) DO UPDATE SET end_offset=excluded.end_offset,sha256=excluded.sha256",
            params![upload_id, start as i64, end as i64, sha256],
        )?;
        connection.execute(
            "UPDATE uploads SET updated_at=?2 WHERE upload_id=?1",
            params![upload_id, unix_now()],
        )?;
        Ok(())
    }

    pub fn received_ranges(&self, upload: &UploadRow) -> Result<Vec<(u64, u64)>> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        let mut statement = connection.prepare(
            "SELECT start_offset,end_offset FROM upload_chunks WHERE upload_id=?1 ORDER BY start_offset",
        )?;
        let rows = statement.query_map([&upload.upload_id], |row| {
            Ok((row.get::<_, i64>(0)? as u64, row.get::<_, i64>(1)? as u64))
        })?;
        Ok(rows.collect::<rusqlite::Result<Vec<_>>>()?)
    }

    pub fn stage_receipt(&self, upload_id: &str, receipt: &StagedObjectReceipt) -> Result<()> {
        let json = serde_json::to_string(receipt)?;
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        connection.execute(
            "UPDATE uploads SET state='staged',receipt_json=?2,cleanup_pending=0,updated_at=?3 WHERE upload_id=?1",
            params![upload_id, json, unix_now()],
        )?;
        Ok(())
    }

    pub fn add_download(&self, row: &DownloadRow) -> Result<()> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        connection.execute(
            "INSERT INTO downloads(download_id,op_id,object_id,object_kind,user_id,size,sha256,exp,created_at)
             VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9)",
            params![
                row.download_id,
                row.op_id,
                row.object_id,
                row.object_kind,
                row.user_id,
                row.size as i64,
                row.sha256,
                row.exp,
                unix_now()
            ],
        )?;
        Ok(())
    }

    pub fn download(&self, download_id: &str) -> Result<Option<DownloadRow>> {
        let connection = self
            .connection
            .lock()
            .expect("transfer store mutex poisoned");
        connection.query_row(
            "SELECT download_id,op_id,object_id,object_kind,user_id,size,sha256,exp FROM downloads WHERE download_id=?1",
            [download_id],
            |row| Ok(DownloadRow {
                download_id: row.get(0)?, op_id: row.get(1)?, object_id: row.get(2)?,
                object_kind: row.get(3)?, user_id: row.get(4)?,
                size: row.get::<_,i64>(5)? as u64, sha256: row.get(6)?, exp: row.get(7)?,
            }),
        ).optional().context("read download ticket session")
    }
}

fn decode_upload(row: &rusqlite::Row<'_>) -> rusqlite::Result<UploadRow> {
    let receipt_json: Option<String> = row.get(10)?;
    let receipt = receipt_json
        .map(|value| serde_json::from_str(&value))
        .transpose()
        .map_err(|error| {
            rusqlite::Error::FromSqlConversionFailure(
                10,
                rusqlite::types::Type::Text,
                Box::new(error),
            )
        })?;
    Ok(UploadRow {
        upload_id: row.get(0)?,
        op_id: row.get(1)?,
        object_id: row.get(2)?,
        object_kind: row.get(3)?,
        user_id: row.get(4)?,
        expected_version: row.get(5)?,
        size: row.get::<_, i64>(6)? as u64,
        sha256: row.get(7)?,
        chunk_size: row.get::<_, i64>(8)? as u64,
        state: row.get(9)?,
        receipt,
        created_at: row.get(11)?,
    })
}

fn ensure_column(
    connection: &Connection,
    table: &str,
    column: &str,
    declaration: &str,
) -> Result<()> {
    let exists: bool = connection.query_row(
        "SELECT EXISTS(SELECT 1 FROM pragma_table_info(?1) WHERE name=?2)",
        params![table, column],
        |row| row.get(0),
    )?;
    if !exists {
        connection.execute(
            &format!("ALTER TABLE {table} ADD COLUMN {column} {declaration}"),
            [],
        )?;
    }
    Ok(())
}

fn migrate_user_id_to_text(connection: &Connection, table: &str) -> Result<()> {
    let declared_type: Option<String> = connection
        .query_row(
            "SELECT type FROM pragma_table_info(?1) WHERE name='user_id'",
            [table],
            |row| row.get(0),
        )
        .optional()?;
    if declared_type
        .as_deref()
        .is_some_and(|value| value.eq_ignore_ascii_case("TEXT"))
    {
        return Ok(());
    }

    let (create, columns) = if table == "uploads" {
        (
            "CREATE TABLE uploads_text (
                upload_id TEXT PRIMARY KEY, op_id TEXT NOT NULL UNIQUE,
                object_id TEXT NOT NULL, object_kind TEXT NOT NULL,
                user_id TEXT NOT NULL, expected_version INTEGER NOT NULL,
                size INTEGER NOT NULL, sha256 TEXT NOT NULL, chunk_size INTEGER NOT NULL,
                state TEXT NOT NULL, receipt_json TEXT, created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL, cleanup_pending INTEGER NOT NULL DEFAULT 0
            )",
            "upload_id,op_id,object_id,object_kind,CAST(user_id AS TEXT),expected_version,size,sha256,chunk_size,state,receipt_json,created_at,updated_at,cleanup_pending",
        )
    } else if table == "downloads" {
        (
            "CREATE TABLE downloads_text (
                download_id TEXT PRIMARY KEY, op_id TEXT NOT NULL,
                object_id TEXT NOT NULL, object_kind TEXT NOT NULL,
                user_id TEXT NOT NULL, size INTEGER NOT NULL, sha256 TEXT NOT NULL,
                exp INTEGER NOT NULL, created_at INTEGER NOT NULL
            )",
            "download_id,op_id,object_id,object_kind,CAST(user_id AS TEXT),size,sha256,exp,created_at",
        )
    } else {
        anyhow::bail!("unsupported transfer identity migration table {table}");
    };

    connection.pragma_update(None, "foreign_keys", "OFF")?;
    let migration = (|| -> Result<()> {
        connection.execute_batch(&format!(
            "BEGIN IMMEDIATE; {create}; INSERT INTO {table}_text SELECT {columns} FROM {table}; DROP TABLE {table}; ALTER TABLE {table}_text RENAME TO {table}; COMMIT;"
        ))?;
        Ok(())
    })();
    if migration.is_err() {
        let _ = connection.execute_batch("ROLLBACK;");
    }
    connection.pragma_update(None, "foreign_keys", "ON")?;
    migration?;
    if table == "uploads" {
        connection.execute(
            "CREATE INDEX IF NOT EXISTS idx_uploads_state ON uploads(state,updated_at)",
            [],
        )?;
    }
    Ok(())
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

fn state_for_abort(state: &str) -> &str {
    if state == "receiving" {
        "aborted"
    } else {
        state
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_db_path() -> std::path::PathBuf {
        std::env::temp_dir().join(format!("transfer-store-{}.sqlite", uuid::Uuid::new_v4()))
    }

    fn remove_db(path: &Path) {
        let _ = std::fs::remove_file(path);
        let _ = std::fs::remove_file(path.with_extension("sqlite-wal"));
        let _ = std::fs::remove_file(path.with_extension("sqlite-shm"));
    }

    fn create_upload(store: &TransferStore, operation_id: &str) -> Result<UploadRow> {
        store.ensure_upload(
            operation_id,
            "00000000-0000-4000-8000-000000000001",
            "video",
            "actor:7",
            0,
            10,
            &"a".repeat(64),
            4,
            100,
            4,
            100,
            8,
        )
    }

    #[test]
    fn abort_releases_receiving_quota_and_preserves_operation_tombstone() {
        let path = temp_db_path();
        let store = TransferStore::open(&path).unwrap();
        let operation = uuid::Uuid::new_v4().to_string();
        create_upload(&store, &operation).unwrap();

        assert_eq!(store.abort_upload(&operation).unwrap(), "aborted");
        assert_eq!(store.upload(&operation).unwrap().unwrap().state, "aborted");
        assert!(create_upload(&store, &operation)
            .unwrap_err()
            .to_string()
            .starts_with("upload_aborted"));
        assert_eq!(store.uploads_for_cleanup(i64::MAX, 8).unwrap().len(), 1);
        drop(store);
        remove_db(&path);
    }

    #[test]
    fn expired_receiving_upload_is_non_resumable_and_cleanup_pending() {
        let path = temp_db_path();
        let store = TransferStore::open(&path).unwrap();
        let operation = uuid::Uuid::new_v4().to_string();
        create_upload(&store, &operation).unwrap();

        assert!(store
            .expire_receiving_upload(&operation, i64::MAX)
            .unwrap());
        assert_eq!(store.upload(&operation).unwrap().unwrap().state, "expired");
        assert!(create_upload(&store, &operation)
            .unwrap_err()
            .to_string()
            .starts_with("upload_expired"));
        assert_eq!(store.uploads_for_cleanup(i64::MAX, 8).unwrap().len(), 1);
        drop(store);
        remove_db(&path);
    }

    #[test]
    fn abort_before_init_leaves_a_tombstone_that_blocks_late_session_creation() {
        let path = temp_db_path();
        let store = TransferStore::open(&path).unwrap();
        let operation = uuid::Uuid::new_v4().to_string();
        store
            .record_aborted_upload(
                &operation,
                "00000000-0000-4000-8000-000000000001",
                "video",
                "actor:7",
                0,
                10,
                &"a".repeat(64),
                4,
            )
            .unwrap();

        assert!(create_upload(&store, &operation)
            .unwrap_err()
            .to_string()
            .starts_with("upload_aborted"));
        drop(store);
        remove_db(&path);
    }
}
