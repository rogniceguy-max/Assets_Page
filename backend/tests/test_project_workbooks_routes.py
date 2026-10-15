"""The three read routes, driven through the host's real handler.

The projector tests cover what a workbook becomes. These cover the wiring around it: that the
routes are reachable, that a signed-out caller gets 401 rather than the router's catch-all 503,
that the unimplemented half answers 503 instead of pretending, and that a file id cannot be
crafted into naming something outside its project.

Exercised against a temporary project root built from the real 孟婆 workbook, so the sheet and
beat counts are the production ones rather than a fixture's.
"""
from __future__ import annotations

import base64
import io
import json
import shutil
import sys
import tempfile
import unittest
from email.parser import BytesParser
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from backend.project_workbooks.nas_adapter import NasSecurity, NasWorkbookServices, NasWorkflowServices  # noqa: E402
from backend.project_workbooks.projects_api import ProjectsApi  # noqa: E402
from backend.server import create_handler  # noqa: E402
from backend.service import ApiError  # noqa: E402

SAMPLES = Path(r"C:\Users\CHEN\Desktop\temp\mengpo-workflow-audit\sources")
MASTER = SAMPLES / "孟婆2_场景节拍表.xlsx"
SINGLE = SAMPLES / "孟婆2_场景节拍表_第26集.xlsx"

TOKEN = "employee-session"


class StubApp:
    def __init__(self, root: Path):
        self.db_path = root / "stub.db"

    def session(self, token):
        if token != TOKEN:
            raise ApiError(401, "SESSION_INVALID", "登录状态已失效，请重新登录。")
        return {"authenticated": True, "user": {"id": 4242, "username": "fixture-only"}}


class StubConnection:
    """`handle()` asks the socket for its timeout before reading; there is no socket here."""

    def gettimeout(self):
        return 20

    def settimeout(self, _value):
        return None


class WorkbookRouteTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if not MASTER.is_file() or not SINGLE.is_file():
            raise unittest.SkipTest("孟婆 samples are not present on this machine")

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "项目文档"
        self.root.mkdir()
        project = self.root / "孟婆"
        project.mkdir()
        shutil.copy(MASTER, project / MASTER.name)
        shutil.copy(SINGLE, project / SINGLE.name)
        # An Excel lock file and a non-workbook must not appear as documents.
        (project / "~$孟婆2_场景节拍表.xlsx").write_bytes(b"lock")
        (project / "剧本.docx").write_bytes(b"not a workbook")
        (self.root / "空项目").mkdir()

        self.app = StubApp(Path(self.temp.name))
        self.api = ProjectsApi(
            NasSecurity(self.app.session),
            NasWorkbookServices(self.root),
            workflow=NasWorkflowServices(self.root))

    def tearDown(self):
        self.temp.cleanup()

    def request(self, method, path, token=TOKEN, body=b"", content_type=None, extra_headers=None):
        values = {"Host": "localhost"}
        if token:
            values["Authorization"] = f"Bearer {token}"
        if content_type:
            values["Content-Type"] = content_type
        values.update(extra_headers or {})
        values["Content-Length"] = str(len(body))
        raw = (f"{method} {path} HTTP/1.1\r\n" +
               "".join(f"{k}: {v}\r\n" for k, v in values.items()) + "\r\n").encode("ascii") + body
        handler_type = create_handler(self.app, project_workbooks=self.api)
        handler = handler_type.__new__(handler_type)
        handler.rfile = io.BytesIO(raw)
        handler.wfile = io.BytesIO()
        handler.close_connection = True
        handler.connection = StubConnection()
        handler.handle_one_request()
        envelope, response_body = handler.wfile.getvalue().split(b"\r\n\r\n", 1)
        status_line, response_headers = envelope.split(b"\r\n", 1)
        return int(status_line.split()[1]), dict(BytesParser().parsebytes(response_headers).items()), response_body

    def projects(self):
        status, _, body = self.request("GET", "/api/v2/project-workbooks/projects")
        self.assertEqual(200, status, body[:200])
        return json.loads(body)["items"]

    # --- listing ---------------------------------------------------------------------------

    def test_projects_lists_every_directory_including_empty_ones(self):
        # 孟婆 has workbooks; 空项目 has none and is still a project rather than being hidden.
        self.assertEqual(sorted(["空项目", "孟婆"]),
                         sorted(item["name"] for item in self.projects()))
        self.assertTrue(all(item["id"].isdigit() for item in self.projects()))

    def test_projects_rejects_a_query_string(self):
        self.assertEqual(422, self.request("GET", "/api/v2/project-workbooks/projects?x=1")[0])

    def test_a_project_id_is_the_directory_inode(self):
        # The id has to survive a rename. The client joins the workbooks and the media of a
        # project through this value, so an id derived from the name would break every join
        # the moment someone renames a project — and the name is exactly the thing users are
        # invited to change.
        by_name = {item["name"]: item["id"] for item in self.projects()}
        for name in ("孟婆", "空项目"):
            self.assertEqual(str((self.root / name).stat().st_ino), by_name[name])

    def test_only_directories_in_the_root_are_projects(self):
        # The root is shared with the storage layer, so it will hold things that are not
        # projects: files, cache directories, and whatever a colleague drags in over SMB.
        # None of them may be listed, and a symlink in particular must not be followed —
        # that is a path out of the root wearing a project's name.
        (self.root / "说明.txt").write_text("not a project", encoding="utf-8")
        (self.root / ".muying_cache").mkdir()
        outside = Path(self.temp.name) / "outside"
        outside.mkdir()
        try:
            (self.root / "链接项目").symlink_to(outside, target_is_directory=True)
        except (OSError, NotImplementedError):
            pass  # Creating a symlink needs a privilege Windows does not always grant.

        self.assertEqual(sorted(["空项目", "孟婆"]), sorted(item["name"] for item in self.projects()))

    def test_an_unreadable_root_is_503_not_an_empty_project_list(self):
        # "No projects yet" and "the projects are right there and this process may not read
        # them" look identical in the response body, and they mean opposite things. Serving
        # the second as the first is how a misconfigured deployment stays invisible: the
        # sidebar simply shows nothing, and nothing is also what a new install shows.
        missing = Path(self.temp.name) / "does-not-exist"
        self.api = ProjectsApi(
            NasSecurity(self.app.session),
            NasWorkbookServices(missing),
            workflow=NasWorkflowServices(missing))
        self.assertEqual(503, self.request("GET", "/api/v2/project-workbooks/projects")[0])

    def test_files_lists_only_workbooks(self):
        project_id = next(item["id"] for item in self.projects() if item["name"] == "孟婆")
        status, _, body = self.request("GET", f"/api/v2/project-workbooks?project_id={project_id}")
        self.assertEqual(200, status, body[:200])
        items = json.loads(body)["items"]
        self.assertEqual(sorted([MASTER.name, SINGLE.name]), sorted(item["name"] for item in items))
        # The lock file and the docx are not documents.
        self.assertNotIn("~$孟婆2_场景节拍表.xlsx", [item["name"] for item in items])
        for item in items:
            for key in ("id", "name", "template_kind", "version", "project_id", "revision_of", "revision_label"):
                self.assertIn(key, item)

    def test_an_unknown_project_is_not_found(self):
        self.assertEqual(404, self.request("GET", "/api/v2/project-workbooks?project_id=999999")[0])

    # --- opening ---------------------------------------------------------------------------

    def test_opening_returns_every_sheet_of_the_real_workbook(self):
        project_id = next(item["id"] for item in self.projects() if item["name"] == "孟婆")
        _, _, body = self.request("GET", f"/api/v2/project-workbooks?project_id={project_id}")
        master = next(item for item in json.loads(body)["items"] if item["name"] == MASTER.name)

        status, _, document_body = self.request("GET", f"/api/v2/project-workbooks/{master['id']}")
        self.assertEqual(200, status, document_body[:200])
        document = json.loads(document_body)

        self.assertEqual(31, len(document["sheets"]))
        beats = [row for sheet in document["sheets"] if sheet["kind"] == "scene_beats"
                 for row in sheet["scene_rows"]]
        self.assertEqual(209, len(beats))
        self.assertIn("使用说明", [sheet["name"] for sheet in document["sheets"] if sheet["kind"] == "generic"])

    def test_a_crafted_file_id_cannot_name_a_file_outside_the_project(self):
        # The id is base64 over the project id and the file name; an id naming a path must be refused rather
        # than joined onto the project directory.
        project_id = next(item["id"] for item in self.projects() if item["name"] == "孟婆")
        crafted = base64.urlsafe_b64encode(f"{project_id}\x00../../../etc/passwd".encode()).decode().rstrip("=")
        self.assertEqual(422, self.request("GET", f"/api/v2/project-workbooks/{crafted}")[0])

    def test_a_malformed_file_id_is_rejected_as_a_bad_request(self):
        # Not a valid encoding at all, so it is a malformed request rather than a missing file.
        self.assertEqual(422, self.request("GET", "/api/v2/project-workbooks/bm90aGluZw")[0])

    def test_a_well_formed_but_absent_file_is_not_found(self):
        project_id = next(item["id"] for item in self.projects() if item["name"] == "孟婆")
        absent = base64.urlsafe_b64encode(f"{project_id}\x00nope.xlsx".encode()).decode().rstrip("=")
        self.assertEqual(404, self.request("GET", f"/api/v2/project-workbooks/{absent}")[0])

    # --- auth and the unimplemented half ----------------------------------------------------

    def test_a_missing_session_is_401_not_503(self):
        # Without the ApiError -> ApiFault translation the router's catch-all would report a
        # signed-out user as a broken server, which is a misleading thing to debug.
        self.assertEqual(401, self.request("GET", "/api/v2/project-workbooks/projects", token=None)[0])
        self.assertEqual(401, self.request("GET", "/api/v2/project-workbooks/projects", token="wrong")[0])

    def test_creating_a_template_creates_workbook_file(self):
        by_name = {item["name"]: item["id"] for item in self.projects()}
        project_id = by_name["空项目"]
        body = json.dumps({"template_kind": "scene_beats", "project_id": project_id,
                           "operation_id": "0123456789abcdef"}).encode("utf-8")
        status, _, response = self.request(
            "POST", "/api/v2/project-workbooks/create", body=body, content_type="application/json")
        self.assertEqual(201, status, response[:200])
        created = json.loads(response.decode("utf-8"))
        self.assertEqual("scene_beats", created["template_kind"])
        self.assertTrue((self.root / "空项目" / created["name"]).is_file())

    def test_creating_a_template_with_custom_name(self):
        by_name = {item["name"]: item["id"] for item in self.projects()}
        project_id = by_name["空项目"]
        body = json.dumps({"template_kind": "asset_review", "project_id": project_id,
                           "operation_id": "fedcba9876543210", "name": "核心角色表"}).encode("utf-8")
        status, _, response = self.request(
            "POST", "/api/v2/project-workbooks/create", body=body, content_type="application/json")
        self.assertEqual(201, status, response[:200])
        created = json.loads(response.decode("utf-8"))
        self.assertEqual("asset_review", created["template_kind"])
        self.assertEqual("核心角色表.xlsx", created["name"])
        self.assertTrue((self.root / "空项目" / "核心角色表.xlsx").is_file())

    def test_importing_a_workbook_creates_file(self):
        by_name = {item["name"]: item["id"] for item in self.projects()}
        project_id = by_name["空项目"]
        sample_xlsx = MASTER.read_bytes()
        status, _, response = self.request(
            "POST", "/api/v2/project-workbooks/import", body=sample_xlsx,
            content_type="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            extra_headers={"X-Project-Id": project_id, "X-Operation-Id": "0123456789abcdef",
                           "X-File-Name": "imported.xlsx"})
        self.assertEqual(201, status, response[:200])
        imported = json.loads(response.decode("utf-8"))
        self.assertEqual("imported.xlsx", imported["name"])
        self.assertTrue((self.root / "空项目" / "imported.xlsx").is_file())

    def test_upload_image_asset_and_relink_to_asset_row(self):
        from urllib.parse import quote
        by_name = {item["name"]: item["id"] for item in self.projects()}
        project_id = by_name["空项目"]
        fake_png = b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15c4"
        status, _, response = self.request(
            "POST", "/api/v2/project-workbooks/image-assets", body=fake_png,
            content_type="image/png",
            extra_headers={"X-Project-Id": project_id, "X-File-Name": "uploaded_hero.png",
                           "X-Folder-Path": quote("角色/孟婆")})
        self.assertEqual(201, status, response[:200])
        payload = json.loads(response.decode("utf-8"))
        self.assertEqual("uploaded_hero", payload["name"])
        self.assertEqual(project_id, payload["project_id"])
        self.assertTrue((self.root / "空项目" / "角色" / "孟婆" / "uploaded_hero.png").is_file())

    def test_the_host_sends_its_own_404_for_paths_it_does_not_own(self):
        self.assertEqual(404, self.request("GET", "/api/v2/not-a-workbook-route")[0])


if __name__ == "__main__":
    unittest.main()
