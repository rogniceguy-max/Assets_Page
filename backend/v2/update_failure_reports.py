"""Bounded storage for anonymous, allowlisted update startup failure reports."""

from __future__ import annotations

import re
import sqlite3
from contextlib import closing
from datetime import datetime, timedelta, timezone
from pathlib import Path

from . import database


REPORT_FIELDS = {
    "schema_version", "update_id", "from_version", "target_version",
    "stage", "error_code", "occurred_at_utc",
}
UPDATE_ID_RE = re.compile(r"^[a-f0-9]{32}$")
VERSION_RE = re.compile(r"^(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})$")
ALLOWED_STAGES = {"health_check", "launch", "rollback", "recovery"}
ALLOWED_ERROR_CODES = {
    "HEALTH_CHECK_PROCESS_START_FAILED", "HEALTH_CHECK_PROCESS_EXITED",
    "HEALTH_CHECK_MARKER_MISSING", "HEALTH_CHECK_MARKER_MISMATCH",
    "HEALTH_CHECK_MARKER_READ_FAILED", "HEALTH_CHECK_TIMEOUT",
    "HEALTH_CHECK_EXIT_TIMEOUT", "HEALTH_CHECK_PROCESS_STOP_FAILED",
    "ROLLBACK_FAILED", "INTERRUPTED_RECOVERY_REQUIRED", "CLIENT_LAUNCH_FAILED",
}
MAX_REPORTS = 1000
RETENTION_DAYS = 30


class InvalidUpdateFailureReport(ValueError):
    """A failure report contains an unexpected or invalid field."""


def validate_report(payload: dict[str, object]) -> dict[str, object]:
    if set(payload) != REPORT_FIELDS:
        raise InvalidUpdateFailureReport("report fields do not match the supported schema")
    if type(payload["schema_version"]) is not int or payload["schema_version"] != 1:
        raise InvalidUpdateFailureReport("unsupported report schema")

    update_id = payload["update_id"]
    from_version = payload["from_version"]
    target_version = payload["target_version"]
    stage = payload["stage"]
    error_code = payload["error_code"]
    occurred_at = payload["occurred_at_utc"]
    if not isinstance(update_id, str) or not UPDATE_ID_RE.fullmatch(update_id):
        raise InvalidUpdateFailureReport("invalid update id")
    if not isinstance(from_version, str) or not VERSION_RE.fullmatch(from_version):
        raise InvalidUpdateFailureReport("invalid source version")
    if not isinstance(target_version, str) or not VERSION_RE.fullmatch(target_version):
        raise InvalidUpdateFailureReport("invalid target version")
    if not isinstance(stage, str) or stage not in ALLOWED_STAGES:
        raise InvalidUpdateFailureReport("invalid failure stage")
    if not isinstance(error_code, str) or error_code not in ALLOWED_ERROR_CODES:
        raise InvalidUpdateFailureReport("invalid failure code")
    if not isinstance(occurred_at, str) or len(occurred_at) > 40:
        raise InvalidUpdateFailureReport("invalid occurrence time")
    try:
        parsed_time = datetime.fromisoformat(occurred_at.replace("Z", "+00:00"))
    except ValueError as error:
        raise InvalidUpdateFailureReport("invalid occurrence time") from error
    if parsed_time.tzinfo is None or parsed_time.utcoffset() != timedelta(0):
        raise InvalidUpdateFailureReport("occurrence time must be UTC")
    now = datetime.now(timezone.utc)
    parsed_time = parsed_time.astimezone(timezone.utc)
    if parsed_time < now - timedelta(days=30) or parsed_time > now + timedelta(hours=24):
        raise InvalidUpdateFailureReport("occurrence time is outside the retention window")

    return {
        "schema_version": 1,
        "update_id": update_id,
        "from_version": from_version,
        "target_version": target_version,
        "stage": stage,
        "error_code": error_code,
        "occurred_at_utc": parsed_time.isoformat(timespec="seconds").replace("+00:00", "Z"),
    }


class UpdateFailureReportStore:
    def __init__(self, db_path: Path):
        self.db_path = db_path
        with closing(database.connect(db_path)) as connection, connection:
            connection.execute(
                """CREATE TABLE IF NOT EXISTS update_failure_reports (
                    update_id TEXT NOT NULL,
                    from_version TEXT NOT NULL,
                    target_version TEXT NOT NULL,
                    stage TEXT NOT NULL,
                    error_code TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    received_at_utc TEXT NOT NULL,
                    PRIMARY KEY (update_id, stage, error_code)
                )"""
            )
            connection.execute(
                "CREATE INDEX IF NOT EXISTS idx_update_failure_reports_received "
                "ON update_failure_reports(received_at_utc)"
            )

    def record(self, report: dict[str, object]) -> None:
        now = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
        cutoff = (datetime.now(timezone.utc) - timedelta(days=RETENTION_DAYS)).isoformat(timespec="seconds").replace("+00:00", "Z")
        with closing(database.connect(self.db_path)) as connection, connection:
            connection.execute(
                """INSERT OR IGNORE INTO update_failure_reports(
                    update_id, from_version, target_version, stage, error_code,
                    occurred_at_utc, received_at_utc
                ) VALUES (?, ?, ?, ?, ?, ?, ?)""",
                (report["update_id"], report["from_version"], report["target_version"], report["stage"],
                 report["error_code"], report["occurred_at_utc"], now),
            )
            connection.execute("DELETE FROM update_failure_reports WHERE received_at_utc < ?", (cutoff,))
            connection.execute(
                """DELETE FROM update_failure_reports
                   WHERE rowid IN (
                       SELECT rowid FROM update_failure_reports
                       ORDER BY received_at_utc DESC, rowid DESC LIMIT -1 OFFSET ?
                   )""",
                (MAX_REPORTS,),
            )
