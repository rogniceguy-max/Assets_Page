use std::{path::Path, sync::Mutex};

use anyhow::{Context, Result};
use rusqlite::{Connection, OptionalExtension, Row, params, params_from_iter};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum TaskKind {
    Upload,
    BatchUpload,
    Download,
    Delete,
}

impl TaskKind {
    fn as_str(&self) -> &'static str {
        match self {
            Self::Upload => "upload",
            Self::BatchUpload => "batch_upload",
            Self::Download => "download",
            Self::Delete => "delete",
        }
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum TaskState {
    Queued,
    WaitingStable,
    Hashing,
    Transferring,
    Verifying,
    Staged,
    Committing,
    Completed,
    Paused,
    Conflict,
    Failed,
    Cancelled,
}

impl TaskState {
    fn as_str(&self) -> &'static str {
        match self {
            Self::Queued => "queued",
            Self::WaitingStable => "waiting_stable",
            Self::Hashing => "hashing",
            Self::Transferring => "transferring",
            Self::Verifying => "verifying",
            Self::Staged => "staged",
            Self::Committing => "committing",
            Self::Completed => "completed",
            Self::Paused => "paused",
            Self::Conflict => "conflict",
            Self::Failed => "failed",
            Self::Cancelled => "cancelled",
        }
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
pub struct TaskSnapshot {
    pub task_id: String,
    pub operation_id: String,
    pub kind: String,
    pub source: String,
    pub state: String,
    pub display_name: String,
    pub folder_group: String,
    pub logical_path: Option<String>,
    pub file_id: Option<String>,
    pub revision_id: Option<String>,
    pub object_id: Option<String>,
    pub expected_version: Option<i64>,
    pub version: Option<i64>,
    pub batch_id: Option<String>,
    pub bytes_total: Option<u64>,
    pub bytes_done: u64,
    pub created_at: i64,
    pub updated_at: i64,
    pub attempt: u32,
    pub retryable: bool,
    pub error_code: Option<String>,
    pub message: Option<String>,
    pub root_id: Option<String>,
    pub commit_started: bool,
    /// Local task context used by the WPF client to restore an inline card after navigation
    /// or an agent restart. This remains on the local IPC path and is not control API data.
    pub source_path: Option<String>,
    pub destination_path: Option<String>,
    pub scope_id: Option<String>,
    pub project_id: Option<String>,
}

#[derive(Clone, Debug)]
pub struct NewTask {
    pub root_id: Option<String>,
    pub operation_id: String,
    pub kind: TaskKind,
    pub source_path: Option<String>,
    pub destination_path: Option<String>,
    pub logical_path: Option<String>,
    pub scope_id: Option<String>,
    pub project_id: Option<String>,
    pub display_name: String,
    pub folder_group: String,
    pub file_id: Option<String>,
    pub revision_id: Option<String>,
    pub object_id: Option<String>,
    pub expected_version: Option<i64>,
    pub media_type: Option<String>,
    pub source: String,
    pub transfer_url: String,
    pub ticket: String,
    pub bytes_total: Option<u64>,
    pub replace_if_hash: Option<String>,
    pub batch_parent_id: Option<String>,
}

#[derive(Clone, Debug)]
pub struct TaskRecord {
    pub snapshot: TaskSnapshot,
    pub source_path: Option<String>,
    pub destination_path: Option<String>,
    pub scope_id: Option<String>,
    pub project_id: Option<String>,
    pub file_id: Option<String>,
    pub revision_id: Option<String>,
    pub object_id: Option<String>,
    pub object_kind: Option<String>,
    pub expected_version: Option<i64>,
    pub media_type: Option<String>,
    pub source: String,
    pub transfer_url: String,
    pub ticket: String,
    pub batch_parent_id: Option<String>,
    pub root_id: Option<String>,
    pub replace_if_hash: Option<String>,
    pub control_state: Option<String>,
    pub commit_started: bool,
}

#[derive(Clone, Debug)]
pub struct SyncBaseline {
    pub root_id: String,
    pub logical_path: String,
    pub scope_id: String,
    pub project_id: Option<String>,
    pub file_id: String,
    pub revision_id: String,
    pub version: i64,
    pub size: u64,
    pub sha256: String,
    pub deleted: bool,
    pub conflicted: bool,
}

pub struct QueueStore {
    connection: Mutex<Connection>,
}

fn task_snapshot_from_row(row: &Row<'_>) -> rusqlite::Result<TaskSnapshot> {
    Ok(TaskSnapshot {
        task_id: row.get(0)?,
        operation_id: row.get(1)?,
        kind: row.get(2)?,
        state: row.get(3)?,
        display_name: row.get(4)?,
        folder_group: row.get(5)?,
        logical_path: row.get(6)?,
        file_id: row.get(7)?,
        revision_id: row.get(8)?,
        object_id: row.get(9)?,
        expected_version: row.get(10)?,
        batch_id: row.get(11)?,
        version: row.get(10)?,
        bytes_total: row.get::<_, Option<i64>>(12)?.map(|value| value as u64),
        bytes_done: row.get::<_, i64>(13)? as u64,
        created_at: row.get(14)?,
        updated_at: row.get(15)?,
        attempt: row.get::<_, i64>(16)? as u32,
        retryable: row.get::<_, bool>(17)?,
        error_code: row.get(18)?,
        message: row.get(19)?,
        source: row.get(20)?,
        root_id: row.get(21)?,
        commit_started: row.get(22)?,
        source_path: row.get(23)?,
        destination_path: row.get(24)?,
        scope_id: row.get(25)?,
        project_id: row.get(26)?,
    })
}

impl QueueStore {
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        let mut connection = Connection::open(path).context("open local transfer queue")?;
        connection.pragma_update(None, "journal_mode", "WAL")?;
        connection.pragma_update(None, "synchronous", "NORMAL")?;
        connection.execute_batch(
            "CREATE TABLE IF NOT EXISTS tasks (
                task_id TEXT PRIMARY KEY,
                operation_id TEXT NOT NULL UNIQUE,
                kind TEXT NOT NULL,
                state TEXT NOT NULL,
                source_path TEXT,
                destination_path TEXT,
                logical_path TEXT,
                scope_id TEXT,
                project_id TEXT,
                display_name TEXT NOT NULL,
                folder_group TEXT NOT NULL,
                file_id TEXT,
                revision_id TEXT,
                object_id TEXT,
                expected_version INTEGER,
                media_type TEXT,
                source TEXT NOT NULL DEFAULT 'wpf',
                transfer_url TEXT NOT NULL,
                ticket TEXT NOT NULL,
                bytes_total INTEGER,
                replace_if_hash TEXT,
                bytes_done INTEGER NOT NULL DEFAULT 0,
                attempt INTEGER NOT NULL DEFAULT 0,
                retryable INTEGER NOT NULL DEFAULT 0,
                error_code TEXT,
                message TEXT,
                batch_parent_id TEXT,
                root_id TEXT,
                control_state TEXT,
                object_kind TEXT,
                commit_started INTEGER NOT NULL DEFAULT 0,
                retry_at INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_transfer_tasks_state_created
                ON tasks(state, created_at);
            CREATE TABLE IF NOT EXISTS watcher_events (
                root_id TEXT NOT NULL,
                path TEXT NOT NULL,
                event_kind TEXT NOT NULL,
                other_path TEXT,
                observed_at INTEGER NOT NULL,
                PRIMARY KEY(root_id, path)
            );
            CREATE TABLE IF NOT EXISTS upload_checkpoints (
                operation_id TEXT PRIMARY KEY,
                upload_id TEXT NOT NULL,
                chunk_size INTEGER NOT NULL,
                received_bytes INTEGER NOT NULL DEFAULT 0,
                missing_ranges_json TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS batches (
                batch_id TEXT PRIMARY KEY,
                directories_json TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_baselines (
                root_id TEXT NOT NULL,
                logical_path TEXT NOT NULL,
                scope_id TEXT NOT NULL,
                project_id TEXT,
                file_id TEXT NOT NULL,
                revision_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                size INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                deleted INTEGER NOT NULL DEFAULT 0,
                conflicted INTEGER NOT NULL DEFAULT 0,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY(root_id,logical_path)
            );
            CREATE TABLE IF NOT EXISTS sync_local_fingerprints (
                root_id TEXT NOT NULL,
                logical_path TEXT NOT NULL,
                size INTEGER NOT NULL,
                modified_ns INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY(root_id,logical_path)
            );
            CREATE TABLE IF NOT EXISTS sync_cursors (
                client_id TEXT PRIMARY KEY,
                sequence INTEGER NOT NULL DEFAULT 0,
                updated_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS suppressed_paths (
                root_id TEXT NOT NULL,
                path TEXT NOT NULL,
                expires_at INTEGER NOT NULL,
                PRIMARY KEY(root_id,path)
            );",
        )?;
        let has_control_state: bool = connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM pragma_table_info('tasks') WHERE name='control_state')",
            [],
            |row| row.get(0),
        )?;
        if !has_control_state {
            connection.execute("ALTER TABLE tasks ADD COLUMN control_state TEXT", [])?;
        }
        let has_object_kind: bool = connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM pragma_table_info('tasks') WHERE name='object_kind')",
            [],
            |row| row.get(0),
        )?;
        if !has_object_kind {
            connection.execute("ALTER TABLE tasks ADD COLUMN object_kind TEXT", [])?;
        }
        let has_commit_started: bool = connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM pragma_table_info('tasks') WHERE name='commit_started')",
            [],
            |row| row.get(0),
        )?;
        if !has_commit_started {
            connection.execute(
                "ALTER TABLE tasks ADD COLUMN commit_started INTEGER NOT NULL DEFAULT 0",
                [],
            )?;
        }
        let has_retry_at: bool = connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM pragma_table_info('tasks') WHERE name='retry_at')",
            [],
            |row| row.get(0),
        )?;
        if !has_retry_at {
            connection.execute(
                "ALTER TABLE tasks ADD COLUMN retry_at INTEGER NOT NULL DEFAULT 0",
                [],
            )?;
        }
        connection.execute_batch(
            "CREATE TABLE IF NOT EXISTS agent_schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at INTEGER NOT NULL
            );",
        )?;
        let legacy_transfer_404_migration_applied: bool = connection.query_row(
            "SELECT EXISTS(SELECT 1 FROM agent_schema_migrations WHERE version=1)",
            [],
            |row| row.get(0),
        )?;
        if !legacy_transfer_404_migration_applied {
            let transaction = connection.transaction()?;
            transaction.execute(
                "UPDATE tasks SET retryable=1
                 WHERE state='failed'
                    AND kind='upload'
                    AND source='wpf'
                    AND error_code='transfer_failed'
                    AND message LIKE 'transfer_http_404:%'
                    AND bytes_done=0
                    AND commit_started=0
                    AND EXISTS (
                        SELECT 1 FROM upload_checkpoints c
                        WHERE c.operation_id=tasks.operation_id AND c.received_bytes=0
                    )",
                [],
            )?;
            transaction.execute(
                "INSERT INTO agent_schema_migrations(version,applied_at) VALUES(1,?1)",
                [unix_now()],
            )?;
            transaction.commit()?;
        }
        Ok(Self {
            connection: Mutex::new(connection),
        })
    }

    pub fn enqueue(&self, task: &NewTask) -> Result<String> {
        self.enqueue_many(std::slice::from_ref(task))?
            .into_iter()
            .next()
            .context("queue did not return an operation id")
    }

    pub fn enqueue_many(&self, tasks: &[NewTask]) -> Result<Vec<String>> {
        let now = unix_now();
        let mut operations = Vec::with_capacity(tasks.len());
        for task in tasks {
            operations.push(
                Uuid::parse_str(&task.operation_id)
                    .context("operation_id must be a UUID")?
                    .to_string(),
            );
        }
        let mut connection = self.connection.lock().expect("queue mutex poisoned");
        let transaction = connection.transaction()?;
        for (task, operation) in tasks.iter().zip(operations.iter()) {
            insert_task(&transaction, task, operation, now)?;
        }
        transaction.commit()?;
        Ok(operations)
    }

    pub fn enqueue_batch(
        &self,
        batch_id: &str,
        tasks: &[NewTask],
        directories_json: &str,
    ) -> Result<Vec<String>> {
        let batch = Uuid::parse_str(batch_id)
            .context("batch_id must be a UUID")?
            .to_string();
        let now = unix_now();
        let mut operations = Vec::with_capacity(tasks.len());
        for task in tasks {
            operations.push(
                Uuid::parse_str(&task.operation_id)
                    .context("operation_id must be a UUID")?
                    .to_string(),
            );
        }
        let mut connection = self.connection.lock().expect("queue mutex poisoned");
        let transaction = connection.transaction()?;
        transaction.execute(
            "INSERT INTO batches(batch_id,directories_json,created_at) VALUES(?1,?2,?3)
             ON CONFLICT(batch_id) DO NOTHING",
            params![batch, directories_json, now],
        )?;
        for (task, operation) in tasks.iter().zip(operations.iter()) {
            let mut child = task.clone();
            child.batch_parent_id = Some(batch.clone());
            insert_task(&transaction, &child, operation, now)?;
        }
        transaction.commit()?;
        Ok(operations)
    }

    pub fn batch_directories_json(&self, batch_id: &str) -> Result<Option<String>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection
            .query_row(
                "SELECT directories_json FROM batches WHERE batch_id=?1",
                [batch_id],
                |row| row.get(0),
            )
            .optional()
            .context("read batch directory manifest")
    }

    pub fn update_authorization(
        &self,
        operation_id: &str,
        file_id: Option<&str>,
        revision_id: &str,
        object_id: &str,
        object_kind: &str,
        expected_version: i64,
        transfer_url: &str,
        ticket: &str,
    ) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "UPDATE tasks SET file_id=COALESCE(?2,file_id),revision_id=?3,object_id=?4,object_kind=?5,
                expected_version=?6,transfer_url=?7,ticket=?8,updated_at=?9 WHERE operation_id=?1",
            params![
                operation_id,
                file_id,
                revision_id,
                object_id,
                object_kind,
                expected_version,
                transfer_url,
                ticket,
                unix_now()
            ],
        )?;
        Ok(())
    }

    pub fn set_bytes_total(&self, operation_id: &str, size: u64) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "UPDATE tasks SET bytes_total=?2,updated_at=?3 WHERE operation_id=?1",
            params![operation_id, size as i64, unix_now()],
        )?;
        Ok(())
    }

    pub fn set_version(&self, operation_id: &str, version: i64) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "UPDATE tasks SET expected_version=?2,updated_at=?3 WHERE operation_id=?1",
            params![operation_id, version, unix_now()],
        )?;
        Ok(())
    }

    pub fn task_state(&self, operation_id: &str) -> Result<Option<String>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection
            .query_row(
                "SELECT state FROM tasks WHERE operation_id=?1",
                [operation_id],
                |row| row.get(0),
            )
            .optional()
            .context("read task state")
    }

    pub fn task_record(&self, operation_id: &str) -> Result<Option<TaskRecord>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let exists = connection
            .query_row(
                "SELECT 1 FROM tasks WHERE operation_id=?1",
                [operation_id],
                |_| Ok(()),
            )
            .optional()?;
        if exists.is_none() {
            return Ok(None);
        }
        Ok(Some(query_task_record(&connection, operation_id)?))
    }

    pub fn set_control_state(&self, operation_id: &str, control_state: &str) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "UPDATE tasks SET control_state=?2,updated_at=?3 WHERE operation_id=?1",
            params![operation_id, control_state, unix_now()],
        )?;
        Ok(())
    }

    pub fn recover_interrupted(&self) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "UPDATE tasks SET state='queued',updated_at=?1,message='resuming after agent restart'
             WHERE state IN ('hashing','waiting_stable','transferring','verifying','staged','committing')",
            [unix_now()],
        )?;
        Ok(())
    }

    pub fn has_active_path(&self, root_id: &str, logical_path: &str) -> Result<bool> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        Ok(connection.query_row(
            "SELECT 1 FROM tasks WHERE root_id=?1 AND logical_path=?2 AND state IN ('queued','waiting_stable','hashing','transferring','verifying','staged','committing') LIMIT 1",
            params![root_id,logical_path],|_|Ok(())).optional()?.is_some())
    }

    pub fn conflict_task(&self, task: &NewTask, message: &str) -> Result<String> {
        let operation = self.enqueue(task)?;
        self.update_state(
            &operation,
            TaskState::Conflict,
            None,
            Some("sync_conflict"),
            Some(message),
            false,
            0,
        )?;
        Ok(operation)
    }

    pub fn update_state(
        &self,
        operation_id: &str,
        state: TaskState,
        bytes_done: Option<u64>,
        error_code: Option<&str>,
        message: Option<&str>,
        retryable: bool,
        attempt_delta: u32,
    ) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "UPDATE tasks SET state=?2, bytes_done=COALESCE(?3,bytes_done),
                error_code=?4,message=?5,retryable=?6,attempt=attempt+?7,retry_at=0,updated_at=?8
             WHERE operation_id=?1",
            params![
                operation_id,
                state.as_str(),
                bytes_done.map(|value| value as i64),
                error_code,
                message,
                retryable,
                attempt_delta as i64,
                unix_now()
            ],
        )?;
        Ok(())
    }

    pub fn update_worker_state(
        &self,
        operation_id: &str,
        state: TaskState,
        bytes_done: Option<u64>,
        error_code: Option<&str>,
        message: Option<&str>,
        retryable: bool,
        attempt_delta: u32,
    ) -> Result<bool> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let changed = connection.execute(
            "UPDATE tasks SET state=?2, bytes_done=COALESCE(?3,bytes_done),
                commit_started=CASE WHEN ?2 IN ('completed','conflict','cancelled') THEN 0 ELSE commit_started END,
                error_code=?4,message=?5,retryable=?6,attempt=attempt+?7,retry_at=0,updated_at=?8
             WHERE operation_id=?1 AND state NOT IN ('cancelled','completed','paused','conflict')",
            params![
                operation_id,
                state.as_str(),
                bytes_done.map(|value| value as i64),
                error_code,
                message,
                retryable,
                attempt_delta as i64,
                unix_now()
            ],
        )?;
        Ok(changed > 0)
    }

    pub fn mark_commit_started(&self, operation_id: &str) -> Result<bool> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let changed = connection.execute(
            "UPDATE tasks SET state='committing',commit_started=1,retry_at=0,updated_at=?2
             WHERE operation_id=?1 AND state NOT IN ('cancelled','completed','paused','conflict')",
            params![operation_id, unix_now()],
        )?;
        Ok(changed > 0)
    }

    pub fn clear_commit_started_before_send(&self, operation_id: &str) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "UPDATE tasks SET state='hashing',commit_started=0,updated_at=?2
             WHERE operation_id=?1 AND state='committing' AND commit_started=1",
            params![operation_id, unix_now()],
        )?;
        Ok(())
    }

    pub fn clear_commit_started_after_rejection(&self, operation_id: &str) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "UPDATE tasks SET commit_started=0,updated_at=?2
             WHERE operation_id=?1 AND state NOT IN ('completed','cancelled')",
            params![operation_id, unix_now()],
        )?;
        Ok(())
    }

    pub fn set_user_task_state(&self, operation_id: &str, state: TaskState) -> Result<bool> {
        let allowed = match &state {
            TaskState::Paused => "state IN ('queued','hashing','waiting_stable','transferring','verifying')",
            TaskState::Queued => "state IN ('queued','paused','failed','cancelled')",
            TaskState::Cancelled => "state NOT IN ('completed','committing','cancelled','conflict') AND commit_started=0",
            _ => anyhow::bail!("unsupported user task state transition"),
        };
        let control_state = match &state {
            TaskState::Paused => Some("user_paused"),
            TaskState::Queued => None,
            _ => None,
        };
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let changed = connection.execute(
            &format!("UPDATE tasks SET state=?2,control_state=?3,error_code=NULL,message=NULL,retryable=0,retry_at=0,updated_at=?4 WHERE operation_id=?1 AND {allowed}"),
            params![operation_id, state.as_str(), control_state, unix_now()],
        )?;
        Ok(changed > 0)
    }

    pub fn set_shutdown_paused(&self, operation_id: &str) -> Result<bool> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let changed = connection.execute(
            "UPDATE tasks SET state='paused',control_state='shutdown_paused',updated_at=?2
             WHERE operation_id=?1 AND state IN ('queued','hashing','waiting_stable','transferring','verifying','waiting_network','offline')
                AND commit_started=0",
            params![operation_id, unix_now()],
        )?;
        Ok(changed > 0)
    }

    pub fn resume_shutdown_paused(&self, operation_id: &str) -> Result<bool> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let changed = connection.execute(
            "UPDATE tasks SET state='queued',control_state=NULL,error_code=NULL,message=NULL,
                retryable=0,retry_at=0,updated_at=?2
             WHERE operation_id=?1 AND state='paused' AND control_state='shutdown_paused' AND commit_started=0",
            params![operation_id, unix_now()],
        )?;
        Ok(changed > 0)
    }

    pub fn resume_shutdown_paused_sync_tasks(&self, root_ids: &[String]) -> Result<usize> {
        if root_ids.is_empty() {
            return Ok(0);
        }
        let mut connection = self.connection.lock().expect("queue mutex poisoned");
        let transaction = connection.transaction()?;
        let mut sql = String::from(
            "UPDATE tasks SET state='queued',control_state=NULL,error_code=NULL,message=NULL,
                retryable=0,retry_at=0,updated_at=?1
             WHERE source='sync' AND state='paused' AND control_state='shutdown_paused' AND root_id IN (",
        );
        let mut values = vec![rusqlite::types::Value::Integer(unix_now())];
        for (index, root_id) in root_ids.iter().enumerate() {
            if index > 0 {
                sql.push(',');
            }
            sql.push('?');
            values.push(rusqlite::types::Value::Text(root_id.clone()));
        }
        sql.push(')');
        let changed = transaction.execute(&sql, params_from_iter(values.iter()))?;
        transaction.commit()?;
        Ok(changed)
    }

    pub fn resume_shutdown_paused_asset_downloads(&self, project_id: &str) -> Result<usize> {
        if project_id.trim().is_empty() || project_id.len() > 512 || project_id.chars().any(char::is_control) {
            anyhow::bail!("project ID is invalid");
        }
        let connection = self.connection.lock().expect("queue mutex poisoned");
        Ok(connection.execute(
            "UPDATE tasks SET state='queued',control_state=NULL,error_code=NULL,message=NULL,
                retryable=0,retry_at=0,updated_at=?2
             WHERE source='asset_image_sync' AND kind='download' AND project_id=?1
                AND state='paused' AND control_state='shutdown_paused'",
            params![project_id, unix_now()],
        )?)
    }

    pub fn schedule_retry(&self, operation_id: &str, delay_seconds: u64) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let now = unix_now();
        connection.execute(
            "UPDATE tasks SET state='queued',retry_at=?2,updated_at=?3 WHERE operation_id=?1 AND state='failed' AND retryable=1",
            params![
                operation_id,
                now.saturating_add(delay_seconds.min(i64::MAX as u64) as i64),
                now
            ],
        )?;
        Ok(())
    }

    pub fn save_checkpoint(
        &self,
        operation_id: &str,
        upload_id: &str,
        chunk_size: u64,
        received_bytes: u64,
        missing_ranges_json: &str,
    ) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "INSERT INTO upload_checkpoints(operation_id,upload_id,chunk_size,received_bytes,missing_ranges_json,updated_at)
             VALUES (?1,?2,?3,?4,?5,?6)
             ON CONFLICT(operation_id) DO UPDATE SET upload_id=excluded.upload_id,
                chunk_size=excluded.chunk_size,received_bytes=excluded.received_bytes,
                missing_ranges_json=excluded.missing_ranges_json,updated_at=excluded.updated_at",
            params![operation_id, upload_id, chunk_size as i64, received_bytes as i64, missing_ranges_json, unix_now()],
        )?;
        Ok(())
    }

    pub fn checkpoint(&self, operation_id: &str) -> Result<Option<(String, u64, u64, String)>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection
            .query_row(
                "SELECT upload_id,chunk_size,received_bytes,missing_ranges_json FROM upload_checkpoints WHERE operation_id=?1",
                [operation_id],
                |row| Ok((row.get(0)?, row.get::<_, i64>(1)? as u64, row.get::<_, i64>(2)? as u64, row.get(3)?)),
            )
            .optional()
            .context("read transfer checkpoint")
    }

    pub fn delete_checkpoint(&self, operation_id: &str) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "DELETE FROM upload_checkpoints WHERE operation_id=?1",
            [operation_id],
        )?;
        Ok(())
    }

    pub fn list_tasks(&self) -> Result<Vec<TaskSnapshot>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let mut statement = connection.prepare(
            "SELECT task_id,operation_id,kind,state,display_name,folder_group,logical_path,file_id,revision_id,object_id,
                expected_version,batch_parent_id,bytes_total,bytes_done,created_at,updated_at,attempt,retryable,error_code,message,source,root_id,commit_started,
                source_path,destination_path,scope_id,project_id
             FROM tasks ORDER BY created_at DESC LIMIT 1000",
        )?;
        let rows = statement.query_map([], task_snapshot_from_row)?;
        Ok(rows.collect::<rusqlite::Result<Vec<_>>>()?)
    }

    /// Returns every task that could prevent a safe app update. Unlike the UI history
    /// list, this is not truncated to the latest 1000 terminal and active rows.
    pub fn list_tasks_for_update(&self) -> Result<(usize, Vec<TaskSnapshot>)> {
        const MAX_UPDATE_TASKS: usize = 1000;
        const ACTIVE_FILTER: &str = "state NOT IN ('completed','conflict','cancelled','canceled','failed','paused') OR commit_started<>0";
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let total: i64 = connection.query_row(
            &format!("SELECT COUNT(*) FROM tasks WHERE {ACTIVE_FILTER}"),
            [],
            |row| row.get(0),
        )?;
        let total = usize::try_from(total).unwrap_or(usize::MAX);
        if total > MAX_UPDATE_TASKS {
            return Ok((total, Vec::new()));
        }

        let mut statement = connection.prepare(&format!(
            "SELECT task_id,operation_id,kind,state,display_name,folder_group,logical_path,file_id,revision_id,object_id,
                expected_version,batch_parent_id,bytes_total,bytes_done,created_at,updated_at,attempt,retryable,error_code,message,source,root_id,commit_started,
                source_path,destination_path,scope_id,project_id
             FROM tasks WHERE {ACTIVE_FILTER} ORDER BY created_at DESC"
        ))?;
        let rows = statement.query_map([], task_snapshot_from_row)?;
        Ok((total, rows.collect::<rusqlite::Result<Vec<_>>>()?))
    }

    pub fn claim_next(&self, available_sync_roots: &[String]) -> Result<Option<TaskRecord>> {
        let mut connection = self.connection.lock().expect("queue mutex poisoned");
        let transaction = connection.transaction()?;
        let mut statement = String::from(
            "SELECT operation_id FROM tasks WHERE state='queued' AND retry_at<=?1",
        );
        let mut values = vec![rusqlite::types::Value::Integer(unix_now())];
        if available_sync_roots.is_empty() {
            statement.push_str(" AND source<>'sync'");
        } else {
            statement.push_str(" AND (source<>'sync' OR root_id IN (");
            for (index, root_id) in available_sync_roots.iter().enumerate() {
                if index > 0 {
                    statement.push(',');
                }
                statement.push('?');
                values.push(rusqlite::types::Value::Text(root_id.clone()));
            }
            statement.push_str("))");
        }
        statement.push_str(" ORDER BY retry_at,created_at LIMIT 1");
        let operation_id = transaction
            .query_row(&statement, params_from_iter(values.iter()), |row| {
                row.get::<_, String>(0)
            })
            .optional()?;
        let Some(operation_id) = operation_id else {
            transaction.commit()?;
            return Ok(None);
        };
        transaction.execute(
            "UPDATE tasks SET state='hashing',attempt=attempt+1,retry_at=0,updated_at=?2 WHERE operation_id=?1 AND state='queued'",
            params![operation_id, unix_now()],
        )?;
        let record = transaction.query_row(
            "SELECT task_id,operation_id,kind,state,display_name,folder_group,logical_path,file_id,revision_id,object_id,
                    expected_version,batch_parent_id,bytes_total,bytes_done,created_at,updated_at,attempt,retryable,error_code,message,
                    source_path,destination_path,scope_id,project_id,file_id,revision_id,object_id,expected_version,media_type,
                    source,transfer_url,ticket,batch_parent_id,root_id,replace_if_hash,control_state,object_kind,commit_started
             FROM tasks WHERE operation_id=?1",
            [operation_id],
            |row| {
                Ok(TaskRecord {
                    snapshot: TaskSnapshot {
                        task_id: row.get(0)?, operation_id: row.get(1)?, kind: row.get(2)?,source:row.get(29)?,
                        state: row.get(3)?, display_name: row.get(4)?, folder_group: row.get(5)?,
                        logical_path: row.get(6)?,file_id:row.get(7)?,revision_id:row.get(8)?,object_id:row.get(9)?,
                        expected_version:row.get(10)?,batch_id:row.get(11)?,bytes_total:row.get::<_,Option<i64>>(12)?.map(|v|v as u64),
                        version:row.get(10)?,
                        bytes_done:row.get::<_,i64>(13)? as u64,created_at:row.get(14)?,updated_at:row.get(15)?,
                        attempt:row.get::<_,i64>(16)? as u32,retryable:row.get(17)?,error_code:row.get(18)?,message:row.get(19)?,
                        root_id:row.get(33)?,
                        commit_started:row.get(37)?,
                        source_path:row.get(20)?,
                        destination_path:row.get(21)?,
                        scope_id:row.get(22)?,
                        project_id:row.get(23)?,
                    },
                    source_path:row.get(20)?,destination_path:row.get(21)?,scope_id:row.get(22)?,project_id:row.get(23)?,
                    file_id:row.get(24)?,revision_id:row.get(25)?,object_id:row.get(26)?,expected_version:row.get(27)?,
                    media_type:row.get(28)?,source:row.get(29)?,transfer_url:row.get(30)?,ticket:row.get(31)?,
                    batch_parent_id:row.get(32)?,root_id:row.get(33)?,replace_if_hash:row.get(34)?,control_state:row.get(35)?,
                    object_kind:row.get(36)?,
                    commit_started:row.get(37)?,
                })
            },
        )?;
        transaction.commit()?;
        Ok(Some(record))
    }

    pub fn claim_batch_sibling(&self, batch_id: &str) -> Result<Option<TaskRecord>> {
        let mut connection = self.connection.lock().expect("queue mutex poisoned");
        let transaction = connection.transaction()?;
        let operation_id = transaction.query_row(
            "SELECT operation_id FROM tasks WHERE batch_parent_id=?1 AND state='queued' AND retry_at<=?2 ORDER BY retry_at,created_at LIMIT 1",
            params![batch_id, unix_now()],
            |row| row.get::<_,String>(0),
        ).optional()?;
        let Some(operation_id) = operation_id else {
            transaction.commit()?;
            return Ok(None);
        };
        transaction.execute("UPDATE tasks SET state='hashing',attempt=attempt+1,retry_at=0,updated_at=?2 WHERE operation_id=?1 AND state='queued'",params![operation_id,unix_now()])?;
        let record = query_task_record(&transaction, &operation_id)?;
        transaction.commit()?;
        Ok(Some(record))
    }

    pub fn record_watcher_event(
        &self,
        root_id: &str,
        path: &str,
        kind: &str,
        other_path: Option<&str>,
    ) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "INSERT INTO watcher_events(root_id,path,event_kind,other_path,observed_at) VALUES (?1,?2,?3,?4,?5)
             ON CONFLICT(root_id,path) DO UPDATE SET event_kind=excluded.event_kind,other_path=excluded.other_path,observed_at=excluded.observed_at",
            params![root_id, path, kind, other_path, unix_now()],
        )?;
        Ok(())
    }

    pub fn pending_watcher_events(
        &self,
        older_than: i64,
    ) -> Result<Vec<(String, String, String, Option<String>)>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let mut statement = connection.prepare(
            "SELECT root_id,path,event_kind,other_path FROM watcher_events WHERE observed_at<=?1 ORDER BY observed_at LIMIT 1000",
        )?;
        let rows = statement.query_map([older_than], |row| {
            Ok((row.get(0)?, row.get(1)?, row.get(2)?, row.get(3)?))
        })?;
        Ok(rows.collect::<rusqlite::Result<Vec<_>>>()?)
    }

    pub fn remove_watcher_event(&self, root_id: &str, path: &str) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "DELETE FROM watcher_events WHERE root_id=?1 AND path=?2",
            params![root_id, path],
        )?;
        Ok(())
    }

    pub fn baseline(&self, root_id: &str, logical_path: &str) -> Result<Option<SyncBaseline>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.query_row(
            "SELECT root_id,logical_path,scope_id,project_id,file_id,revision_id,version,size,sha256,deleted,conflicted
             FROM sync_baselines WHERE root_id=?1 AND logical_path=?2",
            params![root_id,logical_path],
            |row| Ok(SyncBaseline {
                root_id:row.get(0)?,logical_path:row.get(1)?,scope_id:row.get(2)?,project_id:row.get(3)?,
                file_id:row.get(4)?,revision_id:row.get(5)?,version:row.get(6)?,size:row.get::<_,i64>(7)? as u64,
                sha256:row.get(8)?,deleted:row.get(9)?,conflicted:row.get(10)?,
            }),
        ).optional().context("read local file baseline")
    }

    pub fn local_fingerprint(
        &self,
        root_id: &str,
        logical_path: &str,
    ) -> Result<Option<(u64, i64)>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.query_row(
            "SELECT size,modified_ns FROM sync_local_fingerprints WHERE root_id=?1 AND logical_path=?2",
            params![root_id,logical_path],
            |row| Ok((row.get::<_,i64>(0)? as u64,row.get(1)?)),
        ).optional().context("read local sync fingerprint")
    }

    pub fn set_local_fingerprint(
        &self,
        root_id: &str,
        logical_path: &str,
        size: u64,
        modified_ns: i64,
    ) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "INSERT INTO sync_local_fingerprints(root_id,logical_path,size,modified_ns,updated_at) VALUES(?1,?2,?3,?4,?5)
             ON CONFLICT(root_id,logical_path) DO UPDATE SET size=excluded.size,modified_ns=excluded.modified_ns,updated_at=excluded.updated_at",
            params![root_id,logical_path,size as i64,modified_ns,unix_now()],
        )?;
        Ok(())
    }

    pub fn move_local_fingerprint(
        &self,
        root_id: &str,
        old_path: &str,
        new_path: &str,
    ) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute("UPDATE sync_local_fingerprints SET logical_path=?3,updated_at=?4 WHERE root_id=?1 AND logical_path=?2",
            params![root_id,old_path,new_path,unix_now()])?;
        Ok(())
    }

    pub fn move_baseline_prefix(
        &self,
        root_id: &str,
        old_prefix: &str,
        new_prefix: &str,
    ) -> Result<()> {
        let mut connection = self.connection.lock().expect("queue mutex poisoned");
        let tx = connection.transaction()?;
        let baselines = {
            let mut statement=tx.prepare("SELECT logical_path FROM sync_baselines WHERE root_id=?1 AND (logical_path=?2 OR substr(logical_path,1,?3)=?4)")?;
            let prefix = format!("{}/", old_prefix.trim_end_matches('/'));
            let rows = statement
                .query_map(params![root_id, old_prefix, prefix.len(), prefix], |row| {
                    row.get::<_, String>(0)
                })?;
            rows.collect::<rusqlite::Result<Vec<_>>>()?
        };
        for old_path in baselines {
            let suffix = old_path.strip_prefix(old_prefix).unwrap_or("");
            let new_path = format!("{}{}", new_prefix.trim_end_matches('/'), suffix);
            tx.execute("UPDATE sync_baselines SET logical_path=?3,updated_at=?4 WHERE root_id=?1 AND logical_path=?2",params![root_id,old_path,new_path,unix_now()])?;
            tx.execute("UPDATE sync_local_fingerprints SET logical_path=?3,updated_at=?4 WHERE root_id=?1 AND logical_path=?2",params![root_id,old_path,new_path,unix_now()])?;
        }
        tx.commit()?;
        Ok(())
    }

    pub fn move_baseline_prefix_to_root(
        &self,
        old_root_id: &str,
        old_prefix: &str,
        new_root_id: &str,
        new_prefix: &str,
        target_scope_id: &str,
        target_project_id: Option<&str>,
    ) -> Result<()> {
        let mut connection = self.connection.lock().expect("queue mutex poisoned");
        let transaction = connection.transaction()?;
        let old_prefix = old_prefix.trim_end_matches('/');
        let old_parent_prefix = format!("{old_prefix}/");
        let baselines = {
            let mut statement = transaction.prepare(
                "SELECT logical_path,scope_id,project_id,file_id,revision_id,version,size,sha256,deleted,conflicted
                 FROM sync_baselines WHERE root_id=?1 AND (logical_path=?2 OR substr(logical_path,1,?3)=?4)",
            )?;
            let rows = statement.query_map(
                params![old_root_id, old_prefix, old_parent_prefix.len(), old_parent_prefix],
                |row| {
                    Ok(SyncBaseline {
                        root_id: old_root_id.to_owned(),
                        logical_path: row.get(0)?,
                        scope_id: row.get(1)?,
                        project_id: row.get(2)?,
                        file_id: row.get(3)?,
                        revision_id: row.get(4)?,
                        version: row.get(5)?,
                        size: row.get::<_, i64>(6)? as u64,
                        sha256: row.get(7)?,
                        deleted: row.get(8)?,
                        conflicted: row.get(9)?,
                    })
                },
            )?;
            rows.collect::<rusqlite::Result<Vec<_>>>()?
        };
        for mut baseline in baselines {
            let suffix = baseline
                .logical_path
                .strip_prefix(old_prefix)
                .unwrap_or("")
                .to_owned();
            baseline.root_id = new_root_id.to_owned();
            baseline.logical_path = format!("{}{}", new_prefix.trim_end_matches('/'), suffix);
            baseline.scope_id = target_scope_id.to_owned();
            baseline.project_id = target_project_id.map(str::to_owned);
            baseline.version = baseline.version.saturating_add(1);
            transaction.execute(
                "INSERT INTO sync_baselines(root_id,logical_path,scope_id,project_id,file_id,revision_id,version,size,sha256,deleted,conflicted,updated_at)
                 VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12)
                 ON CONFLICT(root_id,logical_path) DO UPDATE SET scope_id=excluded.scope_id,project_id=excluded.project_id,
                    file_id=excluded.file_id,revision_id=excluded.revision_id,version=excluded.version,size=excluded.size,
                    sha256=excluded.sha256,deleted=excluded.deleted,conflicted=excluded.conflicted,updated_at=excluded.updated_at",
                params![baseline.root_id,baseline.logical_path,baseline.scope_id,baseline.project_id,baseline.file_id,
                    baseline.revision_id,baseline.version,baseline.size as i64,baseline.sha256,baseline.deleted,baseline.conflicted,unix_now()],
            )?;
            transaction.execute(
                "DELETE FROM sync_baselines WHERE root_id=?1 AND logical_path=?2",
                params![old_root_id, format!("{}{}", old_prefix, suffix)],
            )?;
            let fingerprint: Option<(i64, i64)> = transaction
                .query_row(
                    "SELECT size,modified_ns FROM sync_local_fingerprints WHERE root_id=?1 AND logical_path=?2",
                    params![old_root_id, format!("{}{}", old_prefix, suffix)],
                    |row| Ok((row.get(0)?, row.get(1)?)),
                )
                .optional()?;
            if let Some((size, modified_ns)) = fingerprint {
                transaction.execute(
                    "INSERT INTO sync_local_fingerprints(root_id,logical_path,size,modified_ns,updated_at)
                     VALUES(?1,?2,?3,?4,?5) ON CONFLICT(root_id,logical_path) DO UPDATE SET
                     size=excluded.size,modified_ns=excluded.modified_ns,updated_at=excluded.updated_at",
                    params![new_root_id, baseline.logical_path, size, modified_ns, unix_now()],
                )?;
                transaction.execute(
                    "DELETE FROM sync_local_fingerprints WHERE root_id=?1 AND logical_path=?2",
                    params![old_root_id, format!("{}{}", old_prefix, suffix)],
                )?;
            }
        }
        transaction.commit()?;
        Ok(())
    }

    pub fn remove_local_fingerprint(&self, root_id: &str, logical_path: &str) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "DELETE FROM sync_local_fingerprints WHERE root_id=?1 AND logical_path=?2",
            params![root_id, logical_path],
        )?;
        Ok(())
    }

    pub fn baselines_under_prefix(
        &self,
        root_id: &str,
        logical_prefix: &str,
    ) -> Result<Vec<SyncBaseline>> {
        let rows = self.baselines_for_root(root_id)?;
        let prefix = logical_prefix.trim_end_matches('/');
        Ok(rows
            .into_iter()
            .filter(|row| {
                row.logical_path == prefix
                    || row
                        .logical_path
                        .strip_prefix(prefix)
                        .is_some_and(|rest| rest.starts_with('/'))
            })
            .collect())
    }

    pub fn upsert_baseline(&self, baseline: &SyncBaseline) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "INSERT INTO sync_baselines(root_id,logical_path,scope_id,project_id,file_id,revision_id,version,size,sha256,deleted,conflicted,updated_at)
             VALUES(?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12)
             ON CONFLICT(root_id,logical_path) DO UPDATE SET scope_id=excluded.scope_id,project_id=excluded.project_id,
               file_id=excluded.file_id,revision_id=excluded.revision_id,version=excluded.version,size=excluded.size,
               sha256=excluded.sha256,deleted=excluded.deleted,conflicted=excluded.conflicted,updated_at=excluded.updated_at",
            params![baseline.root_id,baseline.logical_path,baseline.scope_id,baseline.project_id,baseline.file_id,
                baseline.revision_id,baseline.version,baseline.size as i64,baseline.sha256,baseline.deleted,baseline.conflicted,unix_now()],
        )?;
        Ok(())
    }

    pub fn retarget_deleted_baseline(
        &self,
        root_id: &str,
        old_path: &str,
        file_id: &str,
        new_path: Option<&str>,
        target_scope_id: Option<&str>,
        target_project_id: Option<&str>,
        revision_id: Option<&str>,
        version: Option<i64>,
    ) -> Result<bool> {
        let mut connection = self.connection.lock().expect("queue mutex poisoned");
        let transaction = connection.transaction()?;
        let source_file_id: Option<String> = transaction
            .query_row(
                "SELECT file_id FROM sync_baselines WHERE root_id=?1 AND logical_path=?2",
                params![root_id, old_path],
                |row| row.get(0),
            )
            .optional()?;
        let Some(source_file_id) = source_file_id else {
            transaction.commit()?;
            return Ok(false);
        };
        if source_file_id != file_id {
            transaction.commit()?;
            return Ok(false);
        }

        let mut changed = false;
        if let Some(new_path) = new_path {
            let target_file_id: Option<String> = transaction
                .query_row(
                    "SELECT file_id FROM sync_baselines WHERE root_id=?1 AND logical_path=?2",
                    params![root_id, new_path],
                    |row| row.get(0),
                )
                .optional()?;
            if target_file_id.as_deref().is_none() {
                transaction.execute(
                    "UPDATE sync_baselines SET logical_path=?3,scope_id=COALESCE(?4,scope_id),
                        project_id=?5,revision_id=COALESCE(?6,revision_id),version=COALESCE(?7,version),
                        deleted=1,updated_at=?8 WHERE root_id=?1 AND logical_path=?2",
                    params![
                        root_id,
                        old_path,
                        new_path,
                        target_scope_id,
                        target_project_id,
                        revision_id,
                        version,
                        unix_now()
                    ],
                )?;
                changed = true;
            } else if target_file_id.as_deref() == Some(file_id) {
                if old_path != new_path {
                    transaction.execute(
                        "DELETE FROM sync_baselines WHERE root_id=?1 AND logical_path=?2",
                        params![root_id, old_path],
                    )?;
                }
                transaction.execute(
                    "UPDATE sync_baselines SET scope_id=COALESCE(?3,scope_id),project_id=?4,
                        revision_id=COALESCE(?5,revision_id),version=COALESCE(?6,version),deleted=1,
                        updated_at=?7 WHERE root_id=?1 AND logical_path=?2",
                    params![
                        root_id,
                        new_path,
                        target_scope_id,
                        target_project_id,
                        revision_id,
                        version,
                        unix_now()
                    ],
                )?;
                changed = true;
            } else {
                // A newer file now owns the target path. Retire only this tombstone so
                // it cannot keep blocking reuse of its former path.
                transaction.execute(
                    "DELETE FROM sync_baselines WHERE root_id=?1 AND logical_path=?2",
                    params![root_id, old_path],
                )?;
                changed = true;
            }
        } else {
            transaction.execute(
                "DELETE FROM sync_baselines WHERE root_id=?1 AND logical_path=?2",
                params![root_id, old_path],
            )?;
            changed = true;
        }
        transaction.execute(
            "DELETE FROM sync_local_fingerprints WHERE root_id=?1 AND logical_path=?2",
            params![root_id, old_path],
        )?;
        transaction.commit()?;
        Ok(changed)
    }

    pub fn sync_cursor(&self, client_id: &str) -> Result<i64> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        Ok(connection
            .query_row(
                "SELECT sequence FROM sync_cursors WHERE client_id=?1",
                [client_id],
                |row| row.get(0),
            )
            .optional()?
            .unwrap_or(0))
    }

    pub fn set_sync_cursor(&self, client_id: &str, sequence: i64) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "INSERT INTO sync_cursors(client_id,sequence,updated_at) VALUES(?1,?2,?3)
             ON CONFLICT(client_id) DO UPDATE SET sequence=MAX(sync_cursors.sequence,excluded.sequence),updated_at=excluded.updated_at",
            params![client_id,sequence,unix_now()],
        )?;
        Ok(())
    }

    pub fn baselines_for_root(&self, root_id: &str) -> Result<Vec<SyncBaseline>> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let mut statement=connection.prepare("SELECT root_id,logical_path,scope_id,project_id,file_id,revision_id,version,size,sha256,deleted,conflicted FROM sync_baselines WHERE root_id=?1")?;
        let rows = statement.query_map([root_id], |row| {
            Ok(SyncBaseline {
                root_id: row.get(0)?,
                logical_path: row.get(1)?,
                scope_id: row.get(2)?,
                project_id: row.get(3)?,
                file_id: row.get(4)?,
                revision_id: row.get(5)?,
                version: row.get(6)?,
                size: row.get::<_, i64>(7)? as u64,
                sha256: row.get(8)?,
                deleted: row.get(9)?,
                conflicted: row.get(10)?,
            })
        })?;
        Ok(rows.collect::<rusqlite::Result<Vec<_>>>()?)
    }

    pub fn suppress_path(&self, root_id: &str, path: &str, expires_at: i64) -> Result<()> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        connection.execute(
            "INSERT INTO suppressed_paths(root_id,path,expires_at) VALUES(?1,?2,?3)
            ON CONFLICT(root_id,path) DO UPDATE SET expires_at=excluded.expires_at",
            params![root_id, path, expires_at],
        )?;
        Ok(())
    }

    pub fn path_is_suppressed(&self, root_id: &str, path: &str) -> Result<bool> {
        let connection = self.connection.lock().expect("queue mutex poisoned");
        let now = unix_now();
        connection.execute("DELETE FROM suppressed_paths WHERE expires_at<?1", [now])?;
        Ok(connection
            .query_row(
                "SELECT 1 FROM suppressed_paths WHERE root_id=?1 AND path=?2 AND expires_at>=?3",
                params![root_id, path, now],
                |_| Ok(()),
            )
            .optional()?
            .is_some())
    }
}

fn unix_now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

fn query_task_record(connection: &Connection, operation_id: &str) -> Result<TaskRecord> {
    Ok(connection.query_row(
        "SELECT task_id,operation_id,kind,state,display_name,folder_group,logical_path,file_id,revision_id,object_id,
                expected_version,batch_parent_id,bytes_total,bytes_done,created_at,updated_at,attempt,retryable,error_code,message,
                source_path,destination_path,scope_id,project_id,file_id,revision_id,object_id,expected_version,media_type,
                source,transfer_url,ticket,batch_parent_id,root_id,replace_if_hash,control_state,object_kind,commit_started
         FROM tasks WHERE operation_id=?1",
        [operation_id],
        |row|Ok(TaskRecord{
            snapshot:TaskSnapshot{
                task_id:row.get(0)?,operation_id:row.get(1)?,kind:row.get(2)?,state:row.get(3)?,display_name:row.get(4)?,folder_group:row.get(5)?,
                logical_path:row.get(6)?,file_id:row.get(7)?,revision_id:row.get(8)?,object_id:row.get(9)?,expected_version:row.get(10)?,
                batch_id:row.get(11)?,version:row.get(10)?,bytes_total:row.get::<_,Option<i64>>(12)?.map(|v|v as u64),bytes_done:row.get::<_,i64>(13)? as u64,
                created_at:row.get(14)?,updated_at:row.get(15)?,attempt:row.get::<_,i64>(16)? as u32,retryable:row.get(17)?,error_code:row.get(18)?,
                message:row.get(19)?,source:row.get(29)?,root_id:row.get(33)?,commit_started:row.get(37)?,
                source_path:row.get(20)?,destination_path:row.get(21)?,scope_id:row.get(22)?,project_id:row.get(23)?,
            },
            source_path:row.get(20)?,destination_path:row.get(21)?,scope_id:row.get(22)?,project_id:row.get(23)?,file_id:row.get(24)?,
            revision_id:row.get(25)?,object_id:row.get(26)?,expected_version:row.get(27)?,media_type:row.get(28)?,source:row.get(29)?,
            transfer_url:row.get(30)?,ticket:row.get(31)?,batch_parent_id:row.get(32)?,root_id:row.get(33)?,replace_if_hash:row.get(34)?,control_state:row.get(35)?,object_kind:row.get(36)?,commit_started:row.get(37)?,
        })
    )?)
}

fn insert_task(connection: &Connection, task: &NewTask, operation: &str, now: i64) -> Result<()> {
    connection.execute(
        "INSERT INTO tasks (
            task_id,operation_id,kind,state,source_path,destination_path,logical_path,scope_id,project_id,
            display_name,folder_group,file_id,revision_id,object_id,expected_version,media_type,source,transfer_url,ticket,
            bytes_total,batch_parent_id,root_id,replace_if_hash,created_at,updated_at
         ) VALUES (?1,?1,?2,'queued',?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15,?16,?17,?18,?19,?20,?21,?22,?22)
         ON CONFLICT(operation_id) DO NOTHING",
        params![
            operation,
            task.kind.as_str(),
            task.source_path,
            task.destination_path,
            task.logical_path,
            task.scope_id,
            task.project_id,
            task.display_name,
            task.folder_group,
            task.file_id,
            task.revision_id,
            task.object_id,
            task.expected_version,
            task.media_type,
            task.source,
            task.transfer_url,
            task.ticket,
            task.bytes_total.map(|value| value as i64),
            task.batch_parent_id,
            task.root_id,
            task.replace_if_hash,
            now
        ],
    )?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn baseline(path: &str, file_id: &str) -> SyncBaseline {
        SyncBaseline {
            root_id: "root".into(),
            logical_path: path.into(),
            scope_id: "scope".into(),
            project_id: Some("project".into()),
            file_id: file_id.into(),
            revision_id: "revision-1".into(),
            version: 3,
            size: 12,
            sha256: "a".repeat(64),
            deleted: true,
            conflicted: false,
        }
    }

    fn remove_test_db(path: &Path) {
        let _ = std::fs::remove_file(path);
        let _ = std::fs::remove_file(path.with_extension("sqlite-wal"));
        let _ = std::fs::remove_file(path.with_extension("sqlite-shm"));
    }

    fn upload_task(operation_id: String, source: &str, root_id: Option<&str>) -> NewTask {
        NewTask {
            root_id: root_id.map(str::to_owned),
            operation_id,
            kind: TaskKind::Upload,
            source_path: Some("C:\\sync\\file.mp4".into()),
            destination_path: None,
            logical_path: Some("episode/file.mp4".into()),
            scope_id: Some("project:project".into()),
            project_id: Some("project".into()),
            display_name: "file.mp4".into(),
            folder_group: "episode".into(),
            file_id: None,
            revision_id: None,
            object_id: None,
            expected_version: None,
            media_type: Some("video/mp4".into()),
            source: source.into(),
            transfer_url: String::new(),
            ticket: String::new(),
            bytes_total: Some(1),
            replace_if_hash: None,
            batch_parent_id: None,
        }
    }

    #[test]
    fn legacy_transfer_404_tasks_become_retryable_once() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let retryable_404 = Uuid::new_v4().to_string();
        let sync_404 = Uuid::new_v4().to_string();
        let missing_checkpoint_404 = Uuid::new_v4().to_string();
        let partial_404 = Uuid::new_v4().to_string();
        let non_upload_404 = Uuid::new_v4().to_string();
        let unrelated_error = Uuid::new_v4().to_string();
        {
            let queue = QueueStore::open(&path).unwrap();
            queue
                .enqueue(&upload_task(retryable_404.clone(), "wpf", None))
                .unwrap();
            queue
                .enqueue(&upload_task(sync_404.clone(), "sync", None))
                .unwrap();
            queue
                .enqueue(&upload_task(missing_checkpoint_404.clone(), "wpf", None))
                .unwrap();
            queue
                .enqueue(&upload_task(partial_404.clone(), "wpf", None))
                .unwrap();
            let mut delete_task = upload_task(non_upload_404.clone(), "wpf", None);
            delete_task.kind = TaskKind::Delete;
            queue.enqueue(&delete_task).unwrap();
            queue
                .enqueue(&upload_task(unrelated_error.clone(), "wpf", None))
                .unwrap();
            queue
                .save_checkpoint(&retryable_404, "upload-target", 1024, 0, "[]")
                .unwrap();
            queue
                .save_checkpoint(&sync_404, "upload-sync", 1024, 0, "[]")
                .unwrap();
            queue
                .save_checkpoint(&partial_404, "upload-partial", 1024, 3, "[]")
                .unwrap();
            queue
                .update_worker_state(
                    &retryable_404,
                    TaskState::Failed,
                    Some(0),
                    Some("transfer_failed"),
                    Some("transfer_http_404: old upload route"),
                    false,
                    0,
                )
                .unwrap();
            queue
                .update_worker_state(
                    &sync_404,
                    TaskState::Failed,
                    Some(0),
                    Some("transfer_failed"),
                    Some("transfer_http_404: sync source"),
                    false,
                    0,
                )
                .unwrap();
            queue
                .update_worker_state(
                    &missing_checkpoint_404,
                    TaskState::Failed,
                    Some(0),
                    Some("transfer_failed"),
                    Some("transfer_http_404: no upload checkpoint"),
                    false,
                    0,
                )
                .unwrap();
            queue
                .update_worker_state(
                    &partial_404,
                    TaskState::Failed,
                    Some(3),
                    Some("transfer_failed"),
                    Some("transfer_http_404: partial transfer"),
                    false,
                    0,
                )
                .unwrap();
            queue
                .update_worker_state(
                    &non_upload_404,
                    TaskState::Failed,
                    Some(0),
                    Some("transfer_failed"),
                    Some("transfer_http_404: unrelated delete"),
                    false,
                    0,
                )
                .unwrap();
            queue
                .update_worker_state(
                    &unrelated_error,
                    TaskState::Failed,
                    Some(0),
                    Some("transfer_failed"),
                    Some("transfer_http_500: server error"),
                    false,
                    0,
                )
                .unwrap();
        }

        // Simulate a queue created before this migration was introduced.
        {
            let connection = Connection::open(&path).unwrap();
            connection
                .execute("DELETE FROM agent_schema_migrations WHERE version=1", [])
                .unwrap();
        }

        {
            let queue = QueueStore::open(&path).unwrap();
            let tasks = queue.list_tasks().unwrap();
            let retryable = tasks
                .iter()
                .find(|task| task.operation_id == retryable_404)
                .unwrap();
            let sync = tasks
                .iter()
                .find(|task| task.operation_id == sync_404)
                .unwrap();
            let no_checkpoint = tasks
                .iter()
                .find(|task| task.operation_id == missing_checkpoint_404)
                .unwrap();
            let partial = tasks
                .iter()
                .find(|task| task.operation_id == partial_404)
                .unwrap();
            let delete = tasks
                .iter()
                .find(|task| task.operation_id == non_upload_404)
                .unwrap();
            let server_error = tasks
                .iter()
                .find(|task| task.operation_id == unrelated_error)
                .unwrap();
            assert!(retryable.retryable);
            assert!(!sync.retryable);
            assert!(!no_checkpoint.retryable);
            assert!(!partial.retryable);
            assert!(!delete.retryable);
            assert!(!server_error.retryable);
        }

        // A subsequent restart must not re-enable a task after it fails again.
        {
            let connection = Connection::open(&path).unwrap();
            connection
                .execute(
                    "UPDATE tasks SET retryable=0 WHERE operation_id=?1",
                    [&retryable_404],
                )
                .unwrap();
        }
        {
            let queue = QueueStore::open(&path).unwrap();
            let task = queue
                .list_tasks()
                .unwrap()
                .into_iter()
                .find(|task| task.operation_id == retryable_404)
                .unwrap();
            assert!(!task.retryable);
        }

        remove_test_db(&path);
    }

    #[test]
    fn legacy_transfer_404_migration_runs_after_commit_started_column_upgrade() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let operation_id = Uuid::new_v4().to_string();
        {
            let connection = Connection::open(&path).unwrap();
            connection
                .execute_batch(
                    "CREATE TABLE tasks (
                        task_id TEXT PRIMARY KEY,
                        operation_id TEXT NOT NULL UNIQUE,
                        kind TEXT NOT NULL,
                        state TEXT NOT NULL,
                        source_path TEXT,
                        destination_path TEXT,
                        logical_path TEXT,
                        scope_id TEXT,
                        project_id TEXT,
                        display_name TEXT NOT NULL,
                        folder_group TEXT NOT NULL,
                        file_id TEXT,
                        revision_id TEXT,
                        object_id TEXT,
                        expected_version INTEGER,
                        media_type TEXT,
                        source TEXT NOT NULL DEFAULT 'wpf',
                        transfer_url TEXT NOT NULL,
                        ticket TEXT NOT NULL,
                        bytes_total INTEGER,
                        replace_if_hash TEXT,
                        bytes_done INTEGER NOT NULL DEFAULT 0,
                        attempt INTEGER NOT NULL DEFAULT 0,
                        retryable INTEGER NOT NULL DEFAULT 0,
                        error_code TEXT,
                        message TEXT,
                        batch_parent_id TEXT,
                        root_id TEXT,
                        created_at INTEGER NOT NULL,
                        updated_at INTEGER NOT NULL
                    );
                    CREATE TABLE upload_checkpoints (
                        operation_id TEXT PRIMARY KEY,
                        upload_id TEXT NOT NULL,
                        chunk_size INTEGER NOT NULL,
                        received_bytes INTEGER NOT NULL DEFAULT 0,
                        missing_ranges_json TEXT NOT NULL,
                        updated_at INTEGER NOT NULL
                    );",
                )
                .unwrap();
            let now = unix_now();
            connection
                .execute(
                    "INSERT INTO tasks(
                        task_id,operation_id,kind,state,source_path,logical_path,display_name,folder_group,
                        source,transfer_url,ticket,bytes_total,bytes_done,retryable,error_code,message,created_at,updated_at
                     ) VALUES(?1,?1,'upload','failed','C:\\episode\\cut.mp4','episode/cut.mp4','cut.mp4','episode',
                        'wpf','https://nas.example/transfer/v1','ticket',4096,0,0,'transfer_failed',
                        'transfer_http_404: old chunk URL',?2,?2)",
                    params![operation_id, now],
                )
                .unwrap();
            connection
                .execute(
                    "INSERT INTO upload_checkpoints(operation_id,upload_id,chunk_size,received_bytes,missing_ranges_json,updated_at)
                     VALUES(?1,'existing-upload',1024,0,'[]',?2)",
                    params![operation_id, now],
                )
                .unwrap();
        }

        let queue = QueueStore::open(&path).unwrap();
        let task = queue
            .list_tasks()
            .unwrap()
            .into_iter()
            .find(|task| task.operation_id == operation_id)
            .unwrap();
        assert!(task.retryable);
        assert!(!task.commit_started);
        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn unconfigured_sync_tasks_wait_without_blocking_regular_transfers() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        queue
            .enqueue(&upload_task(Uuid::new_v4().to_string(), "sync", Some("root-a")))
            .unwrap();
        queue
            .enqueue(&upload_task(Uuid::new_v4().to_string(), "wpf", None))
            .unwrap();

        let regular = queue.claim_next(&[]).unwrap().unwrap();
        assert_eq!(regular.source, "wpf");
        assert!(queue.claim_next(&[]).unwrap().is_none());

        let sync = queue.claim_next(&["root-a".into()]).unwrap().unwrap();
        assert_eq!(sync.source, "sync");
        assert_eq!(sync.root_id.as_deref(), Some("root-a"));
        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn configured_sync_roots_resume_shutdown_pauses_but_not_user_pauses() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        let shutdown_paused = Uuid::new_v4().to_string();
        let user_paused = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(shutdown_paused.clone(), "sync", Some("root-a"))).unwrap();
        queue.enqueue(&upload_task(user_paused.clone(), "sync", Some("root-a"))).unwrap();
        assert!(queue.set_shutdown_paused(&shutdown_paused).unwrap());
        assert!(queue.set_user_task_state(&user_paused, TaskState::Paused).unwrap());

        assert_eq!(1, queue.resume_shutdown_paused_sync_tasks(&["root-a".into()]).unwrap());
        let tasks = queue.list_tasks().unwrap();
        let resumed = tasks.iter().find(|task| task.operation_id == shutdown_paused).unwrap();
        let manually_paused = tasks.iter().find(|task| task.operation_id == user_paused).unwrap();
        assert_eq!("queued", resumed.state);
        assert_eq!("paused", manually_paused.state);
        assert_eq!(Some("root-a"), resumed.root_id.as_deref());
        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn project_asset_downloads_resume_shutdown_pauses_but_not_user_pauses() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        let shutdown_paused = Uuid::new_v4().to_string();
        let user_paused = Uuid::new_v4().to_string();
        let mut shutdown_task = upload_task(shutdown_paused.clone(), "asset_image_sync", None);
        shutdown_task.kind = TaskKind::Download;
        shutdown_task.destination_path = Some("C:\\cache\\asset.webp".into());
        shutdown_task.project_id = Some("project-a".into());
        let mut user_task = shutdown_task.clone();
        user_task.operation_id = user_paused.clone();
        queue.enqueue(&shutdown_task).unwrap();
        queue.enqueue(&user_task).unwrap();
        assert!(queue.set_shutdown_paused(&shutdown_paused).unwrap());
        assert!(queue.set_user_task_state(&user_paused, TaskState::Paused).unwrap());

        assert_eq!(1, queue.resume_shutdown_paused_asset_downloads("project-a").unwrap());
        let tasks = queue.list_tasks().unwrap();
        let resumed = tasks.iter().find(|task| task.operation_id == shutdown_paused).unwrap();
        let manually_paused = tasks.iter().find(|task| task.operation_id == user_paused).unwrap();
        assert_eq!("queued", resumed.state);
        assert_eq!("paused", manually_paused.state);
        assert_eq!(Some("project-a"), resumed.project_id.as_deref());
        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn deleted_retarget_moves_tombstone_off_the_reusable_old_path() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        queue
            .upsert_baseline(&baseline("Episode/01/beat.mp4", "deleted-file"))
            .unwrap();
        queue
            .set_local_fingerprint("root", "Episode/01/beat.mp4", 12, 99)
            .unwrap();

        assert!(queue
            .retarget_deleted_baseline(
                "root",
                "Episode/01/beat.mp4",
                "deleted-file",
                Some("Episode/02/beat.mp4"),
                Some("scope"),
                Some("project"),
                Some("revision-2"),
                Some(4),
            )
            .unwrap());
        assert!(queue
            .baseline("root", "Episode/01/beat.mp4")
            .unwrap()
            .is_none());
        let moved = queue
            .baseline("root", "Episode/02/beat.mp4")
            .unwrap()
            .unwrap();
        assert_eq!(moved.file_id, "deleted-file");
        assert!(moved.deleted);
        assert_eq!(moved.version, 4);
        assert_eq!(moved.revision_id, "revision-2");
        assert!(queue
            .local_fingerprint("root", "Episode/01/beat.mp4")
            .unwrap()
            .is_none());
        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn deleted_retarget_does_not_remove_a_reused_old_path() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        queue
            .upsert_baseline(&baseline("Episode/01/beat.mp4", "new-file"))
            .unwrap();

        assert!(!queue
            .retarget_deleted_baseline(
                "root",
                "Episode/01/beat.mp4",
                "deleted-file",
                Some("Episode/02/beat.mp4"),
                Some("scope"),
                Some("project"),
                Some("revision-2"),
                Some(4),
            )
            .unwrap());
        assert_eq!(
            queue
                .baseline("root", "Episode/01/beat.mp4")
                .unwrap()
                .unwrap()
                .file_id,
            "new-file"
        );
        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn cancellation_cannot_win_after_nas_commit_is_claimed() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        let operation_id = Uuid::new_v4().to_string();
        queue
            .enqueue(&NewTask {
                root_id: None,
                operation_id: operation_id.clone(),
                kind: TaskKind::Upload,
                source_path: Some("C:\\sync\\file.mp4".into()),
                destination_path: None,
                logical_path: None,
                scope_id: Some("scope".into()),
                project_id: None,
                display_name: "file.mp4".into(),
                folder_group: "group".into(),
                file_id: None,
                revision_id: None,
                object_id: None,
                expected_version: None,
                media_type: Some("video/mp4".into()),
                source: "wpf".into(),
                transfer_url: String::new(),
                ticket: String::new(),
                bytes_total: Some(1),
                replace_if_hash: None,
                batch_parent_id: None,
            })
            .unwrap();
        assert!(queue.mark_commit_started(&operation_id).unwrap());
        assert!(!queue
            .set_user_task_state(&operation_id, TaskState::Cancelled)
            .unwrap());
        assert!(queue.task_record(&operation_id).unwrap().unwrap().commit_started);
        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn update_task_list_keeps_old_active_and_ambiguous_tasks_visible() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        let old_active_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(old_active_id.clone(), "wpf", None)).unwrap();

        let paused_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(paused_id.clone(), "wpf", None)).unwrap();
        assert!(queue.set_shutdown_paused(&paused_id).unwrap());

        let ambiguous_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(ambiguous_id.clone(), "wpf", None)).unwrap();
        assert!(queue.mark_commit_started(&ambiguous_id).unwrap());
        assert!(queue.update_worker_state(&ambiguous_id, TaskState::Failed, None,
            Some("network_error"), Some("response lost"), true, 0).unwrap());

        let safe_failed_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(safe_failed_id.clone(), "wpf", None)).unwrap();
        assert!(queue.update_worker_state(&safe_failed_id, TaskState::Failed, None,
            Some("local_error"), Some("not committed"), false, 0).unwrap());

        // More than the regular UI history cap of terminal rows must not hide the older
        // active task or an unresolved commit from update preparation.
        for _ in 0..1001 {
            let terminal_id = Uuid::new_v4().to_string();
            queue.enqueue(&upload_task(terminal_id.clone(), "wpf", None)).unwrap();
            assert!(queue.update_worker_state(&terminal_id, TaskState::Completed, Some(1),
                None, None, false, 0).unwrap());
        }

        let (total, tasks) = queue.list_tasks_for_update().unwrap();
        let ids: std::collections::HashSet<_> = tasks.iter().map(|task| task.operation_id.as_str()).collect();
        assert_eq!(total, 2);
        assert_eq!(tasks.len(), total);
        assert!(ids.contains(old_active_id.as_str()));
        assert!(ids.contains(ambiguous_id.as_str()));
        assert!(!ids.contains(paused_id.as_str()));
        assert!(!ids.contains(safe_failed_id.as_str()));
        assert!(tasks.iter().any(|task| task.operation_id == ambiguous_id && task.commit_started));

        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn resume_shutdown_pause_never_overrides_user_pause_or_cancel() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();

        let shutdown_paused_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(shutdown_paused_id.clone(), "wpf", None)).unwrap();
        assert!(queue.set_shutdown_paused(&shutdown_paused_id).unwrap());
        assert!(queue.resume_shutdown_paused(&shutdown_paused_id).unwrap());
        assert_eq!(queue.task_record(&shutdown_paused_id).unwrap().unwrap().snapshot.state, "queued");

        let user_paused_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(user_paused_id.clone(), "wpf", None)).unwrap();
        assert!(queue.set_user_task_state(&user_paused_id, TaskState::Paused).unwrap());
        assert!(!queue.resume_shutdown_paused(&user_paused_id).unwrap());
        let user_paused = queue.task_record(&user_paused_id).unwrap().unwrap();
        assert_eq!(user_paused.snapshot.state, "paused");
        assert_eq!(user_paused.control_state.as_deref(), Some("user_paused"));

        let cancelled_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(cancelled_id.clone(), "wpf", None)).unwrap();
        assert!(queue.set_shutdown_paused(&cancelled_id).unwrap());
        assert!(queue.set_user_task_state(&cancelled_id, TaskState::Cancelled).unwrap());
        assert!(!queue.resume_shutdown_paused(&cancelled_id).unwrap());
        assert_eq!(queue.task_record(&cancelled_id).unwrap().unwrap().snapshot.state, "cancelled");

        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn update_safely_pauses_legacy_network_wait_states() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();

        for state in ["waiting_network", "offline"] {
            let operation_id = Uuid::new_v4().to_string();
            queue.enqueue(&upload_task(operation_id.clone(), "wpf", None)).unwrap();
            queue.connection.lock().unwrap().execute(
                "UPDATE tasks SET state=?2 WHERE operation_id=?1",
                params![operation_id, state],
            ).unwrap();

            assert!(queue.set_shutdown_paused(&operation_id).unwrap());
            let paused = queue.task_record(&operation_id).unwrap().unwrap();
            assert_eq!(paused.snapshot.state, "paused");
            assert_eq!(paused.control_state.as_deref(), Some("shutdown_paused"));
            assert!(queue.resume_shutdown_paused(&operation_id).unwrap());
            assert_eq!(queue.task_record(&operation_id).unwrap().unwrap().snapshot.state, "queued");
        }

        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn ambiguous_commit_remains_visible_until_retry_resolves_it() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        let operation_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(operation_id.clone(), "sync", Some("root-a"))).unwrap();
        assert!(queue.mark_commit_started(&operation_id).unwrap());
        queue.update_worker_state(&operation_id, TaskState::Failed, None,
            Some("network_error"), Some("response lost"), true, 0).unwrap();
        let failed = queue.task_record(&operation_id).unwrap().unwrap();
        assert!(failed.commit_started);
        assert!(!queue.set_user_task_state(&operation_id, TaskState::Cancelled).unwrap());

        queue.update_worker_state(&operation_id, TaskState::Conflict, None,
            Some("version_conflict"), Some("the server rejected the generation"), false, 0).unwrap();
        let resolved = queue.task_record(&operation_id).unwrap().unwrap();
        assert!(!resolved.commit_started);
        drop(queue);
        remove_test_db(&path);
    }

    #[test]
    fn explicit_nonretryable_rejection_releases_commit_guard_for_cancel() {
        let path = std::env::temp_dir().join(format!("transfer-queue-{}.sqlite", Uuid::new_v4()));
        let queue = QueueStore::open(&path).unwrap();
        let operation_id = Uuid::new_v4().to_string();
        queue.enqueue(&upload_task(operation_id.clone(), "sync", Some("root-a"))).unwrap();
        assert!(queue.mark_commit_started(&operation_id).unwrap());
        queue.update_worker_state(&operation_id, TaskState::Failed, None,
            Some("transfer_http_422"), Some("file hash mismatch"), false, 0).unwrap();
        assert!(queue.clear_commit_started_after_rejection(&operation_id).is_ok());
        assert!(!queue.task_record(&operation_id).unwrap().unwrap().commit_started);
        assert!(queue.set_user_task_state(&operation_id, TaskState::Cancelled).unwrap());
        drop(queue);
        remove_test_db(&path);
    }
}
