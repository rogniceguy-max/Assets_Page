#!/usr/bin/env python3
"""
MuYing Drive XLSX Import/Export Standalone Tool
==============================================
Standalone utility for AI agents and offline batch scripts to import and export
Excel (.xlsx) files directly to/from the Project Workbooks SQLite database.

The client application operates purely on SQLite and does not handle .xlsx directly.
This tool bridges the gap for AI workflows and data migrations.

Usage:
  # List all projects and workbooks in the database
  python scripts/xlsx_tool.py list --db <path_to_project_workbooks.db>

  # Export a workbook to .xlsx
  python scripts/xlsx_tool.py export --db <path_to_db> --project "孟婆" --workbook "节拍表" --output output.xlsx

  # Import an .xlsx file into a project as a new workbook
  python scripts/xlsx_tool.py import --db <path_to_db> --project "孟婆" --file input.xlsx --name "角色资产表"

  # Import an .xlsx file as a new revision of an existing workbook
  python scripts/xlsx_tool.py import --db <path_to_db> --project "孟婆" --file input.xlsx --revision-of <workbook_id>
"""

from __future__ import annotations

import argparse
import io
import os
import sys
from pathlib import Path

# Add project root to sys.path
PROJECT_ROOT = Path(__file__).resolve().parent.parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from backend.project_workbooks.sqlite_store import SqliteProjectWorkbookStore
from backend.project_workbooks.xlsx_compat import export_document


def resolve_project_id(store: SqliteProjectWorkbookStore, project_ident: str) -> str:
    """Resolve project name or ID to project ID."""
    projects = store.projects(None)
    for p in projects:
        if p["id"] == project_ident or p["name"] == project_ident:
            return p["id"]
    raise ValueError(f"Project not found: '{project_ident}'. Available projects: {[p['name'] for p in projects]}")


def resolve_workbook(store: SqliteProjectWorkbookStore, project_id: str, workbook_ident: str) -> dict:
    """Resolve workbook name or ID to workbook file dictionary."""
    files = store.list_files(None, project_id=project_id)
    for f in files:
        if f["id"] == workbook_ident or f["name"] == workbook_ident:
            return f
    raise ValueError(f"Workbook not found: '{workbook_ident}'. Available workbooks: {[f['name'] for f in files]}")


def cmd_list(args: argparse.Namespace) -> None:
    db_path = Path(args.db).resolve()
    if not db_path.is_file():
        print(f"Error: Database file not found at {db_path}", file=sys.stderr)
        sys.exit(1)

    store = SqliteProjectWorkbookStore(root=Path(args.docs_root) if args.docs_root else db_path.parent, db_path=db_path)
    projects = store.projects(None)
    if not projects:
        print("No projects found in database.")
        return

    print(f"Database: {db_path}")
    print(f"Total projects: {len(projects)}\n")

    for p in projects:
        if args.project and args.project not in (p["id"], p["name"]):
            continue
        print(f"📁 Project: {p['name']} (ID: {p['id']})")
        files = store.list_files(None, project_id=p["id"])
        if not files:
            print("   (No workbooks)")
        for f in files:
            template = f.get("template_kind", "unknown")
            rev = f" [rev of {f['revision_of']}]" if f.get("revision_of") else ""
            print(f"   📄 {f['name']} (ID: {f['id']}, Type: {template}, Version: {f.get('version', 1)}{rev})")
        print()


def cmd_export(args: argparse.Namespace) -> None:
    db_path = Path(args.db).resolve()
    if not db_path.is_file():
        print(f"Error: Database file not found at {db_path}", file=sys.stderr)
        sys.exit(1)

    store = SqliteProjectWorkbookStore(root=Path(args.docs_root) if args.docs_root else db_path.parent, db_path=db_path)
    project_id = resolve_project_id(store, args.project)
    file_info = resolve_workbook(store, project_id, args.workbook)

    xlsx_bytes = store._export_xlsx(file_info["id"])

    out_path = Path(args.output).resolve()
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(xlsx_bytes)
    print(f"Successfully exported workbook '{file_info['name']}' ({len(xlsx_bytes)} bytes) to:\n  {out_path}")


def cmd_import(args: argparse.Namespace) -> None:
    db_path = Path(args.db).resolve()
    if not db_path.is_file():
        print(f"Error: Database file not found at {db_path}", file=sys.stderr)
        sys.exit(1)

    in_path = Path(args.file).resolve()
    if not in_path.is_file():
        print(f"Error: Input file not found at {in_path}", file=sys.stderr)
        sys.exit(1)

    store = SqliteProjectWorkbookStore(root=Path(args.docs_root) if args.docs_root else db_path.parent, db_path=db_path)
    project_id = resolve_project_id(store, args.project)

    file_name = args.name or in_path.name
    if not file_name.lower().endswith(".xlsx"):
        file_name += ".xlsx"

    data = in_path.read_bytes()
    stream = io.BytesIO(data)

    result = store.import_xlsx_stream(
        actor=None,
        name=file_name,
        source=stream,
        length=len(data),
        project_id=project_id,
        operation_id="ai_script_import",
        revision_of=args.revision_of
    )
    print(f"Successfully imported '{file_name}' to project '{args.project}':")
    print(f"  Workbook ID: {result.get('id')}")
    print(f"  Name:        {result.get('name')}")
    print(f"  Type:        {result.get('template_kind')}")


def main() -> None:
    parser = argparse.ArgumentParser(
        description="MuYing Drive XLSX Import/Export Standalone Tool for AI & offline maintenance",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    # list
    p_list = subparsers.add_parser("list", help="List projects and workbooks in database")
    p_list.add_argument("--db", required=True, help="Path to project_workbooks.db SQLite database")
    p_list.add_argument("--docs-root", help="Path to project documents root directory (defaults to DB directory)")
    p_list.add_argument("--project", help="Filter by project name or ID")

    # export
    p_export = subparsers.add_parser("export", help="Export a workbook to .xlsx")
    p_export.add_argument("--db", required=True, help="Path to project_workbooks.db SQLite database")
    p_export.add_argument("--docs-root", help="Path to project documents root directory")
    p_export.add_argument("--project", required=True, help="Project name or ID")
    p_export.add_argument("--workbook", required=True, help="Workbook name or ID")
    p_export.add_argument("--output", required=True, help="Path to write the exported .xlsx")

    # import
    p_import = subparsers.add_parser("import", help="Import an .xlsx file into a project")
    p_import.add_argument("--db", required=True, help="Path to project_workbooks.db SQLite database")
    p_import.add_argument("--docs-root", help="Path to project documents root directory")
    p_import.add_argument("--project", required=True, help="Project name or ID")
    p_import.add_argument("--file", required=True, help="Path to the .xlsx file to import")
    p_import.add_argument("--name", help="Display name for the workbook (defaults to file name)")
    p_import.add_argument("--revision-of", help="Existing workbook ID if this is a new revision")

    args = parser.parse_args()

    if args.command == "list":
        cmd_list(args)
    elif args.command == "export":
        cmd_export(args)
    elif args.command == "import":
        cmd_import(args)


if __name__ == "__main__":
    main()
