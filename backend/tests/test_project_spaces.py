"""Creating a project: the templates, the name rules, and what a failure leaves behind.

The load-bearing test is the first two. A project is created with two workbooks whose only
job is to be recognised by :mod:`backend.project_workbooks.projector` — which decides what
a workbook is by reading its header row and never its filename. So the template and the
parser are two halves of one contract that nothing else enforces: get the headers subtly
wrong and every new project opens as an untitled generic grid, with no error anywhere. These
tests feed each generated template back through the projector, so they cannot drift apart
without the suite saying so.
"""
from __future__ import annotations

import json
import os
import socket
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[3]))

from backend import project_helper  # noqa: E402
from backend.project_spaces import (  # noqa: E402
    ProjectHelperFailed, ProjectHelperUnavailable, ProjectProvisioner, ProjectSpaceService)
from backend.project_workbooks import projector  # noqa: E402
from backend.service import ApiError  # noqa: E402


def _write(path: Path, data: bytes) -> Path:
    path.write_bytes(data)
    return path


class TemplateClassificationTests(unittest.TestCase):
    """The headers the helper writes must be the headers the projector looks for."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def test_the_beat_template_is_recognised_as_a_beat_sheet(self):
        path = _write(self.root / "节拍表.xlsx",
                      project_helper._template_bytes(project_helper.BEAT_HEADERS, "节拍表"))
        self.assertEqual("scene_beats", projector.classify(path))

    def test_the_asset_template_is_recognised_as_an_asset_sheet(self):
        path = _write(self.root / "资产表.xlsx",
                      project_helper._template_bytes(project_helper.ASSET_HEADERS, "资产表"))
        self.assertEqual("asset_review", projector.classify(path))

    def test_the_two_templates_are_not_recognised_as_each_other(self):
        # The projector picks asset_review over scene_beats when a row carries 类别 or 名称.
        # A beat sheet that gained one of those columns would silently become an asset table,
        # so the distinction is asserted rather than assumed from the two tests above.
        self.assertNotIn("类别", project_helper.BEAT_HEADERS)
        self.assertNotIn("名称", project_helper.BEAT_HEADERS)
        self.assertIn("类别", project_helper.ASSET_HEADERS)

    def test_a_generated_template_opens_as_a_readable_document(self):
        # Not just classified: the whole read path has to accept it, because that is what a
        # user does with a new project a second after creating it.
        path = _write(self.root / "节拍表.xlsx",
                      project_helper._template_bytes(project_helper.BEAT_HEADERS, "节拍表"))
        document = projector.project_document(
            path, file_id="f", name="节拍表.xlsx", project_id="1")
        self.assertEqual(1, len(document["sheets"]))
        self.assertEqual("scene_beats", document["sheets"][0]["kind"])
        self.assertEqual([], document["sheets"][0]["scene_rows"])


class ProjectNameTests(unittest.TestCase):
    def test_ordinary_names_pass(self):
        for name in ("孟婆", "川渝霸王龙，穿成虐文大冤种！", "Project 2", "2026-10 花店"):
            self.assertEqual(name, project_helper._validate_name(name))

    def test_surrounding_whitespace_is_trimmed_rather_than_refused(self):
        # Deliberately friendlier than the client's rename rule: a name pasted into a create
        # field with a trailing space is accepted as the name without it, because that is
        # what the user meant. A rename target keeps the client's refusal, where the space
        # is invisible and Windows would quietly drop it.
        self.assertEqual("孟婆", project_helper._validate_name("  孟婆  "))
        self.assertEqual("孟婆", project_helper._validate_name("孟婆 "))
        self.assertEqual("孟婆", project_helper._validate_name("孟婆	"))

    def test_names_that_would_escape_or_break_the_directory_are_refused(self):
        for name in ("", "   ", ".", "..", "a/b", "a\\b", "a:b", "a*b", "a?b", 'a"b',
                     "a<b", "a>b", "a|b", "line\nbreak", "trailing.",
                     "CON", "con.txt", "LPT1.log", "x" * 200):
            with self.assertRaises(project_helper.HelperFailure, msg=repr(name)) as caught:
                project_helper._validate_name(name)
            self.assertEqual("INVALID_NAME", caught.exception.code, repr(name))

    def test_a_non_string_is_refused_rather_than_coerced(self):
        for value in (None, 1, ["孟婆"], {"name": "孟婆"}):
            with self.assertRaises(project_helper.HelperFailure):
                project_helper._validate_name(value)


class CreateProjectTests(unittest.TestCase):
    """Against a temporary root, with the ownership lookup pointed at this machine."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        # The ownership lookup is the only part of creation that needs the Linux account
        # databases, so it is the one thing replaced here. Everything else — the staging
        # directory, the templates, the rename — runs for real.
        self.ownership = mock.patch.object(
            project_helper, "_ownership", lambda: (os.getpid(), os.getpid()))
        self.ownership.start()
        self.addCleanup(self.ownership.stop)

    def tearDown(self):
        self.temp.cleanup()

    def _create(self, name):
        with mock.patch.object(project_helper, "PROJECT_ROOT", self.root):
            return project_helper.create_project({"name": name})

    def test_a_project_arrives_complete(self):
        result = self._create("孟婆")
        self.assertTrue(result["ok"])
        created = self.root / "孟婆"
        self.assertTrue(created.is_dir())
        names = sorted(path.name for path in created.iterdir())
        self.assertEqual([], names)

    def test_the_modes_are_set_explicitly_rather_than_left_to_the_creation_call(self):
        # Asserted because the call is not what decides. On the NAS a directory asked for as
        # 0770 and a file asked for as 0660 both came back as 0770 — the file executable —
        # so the mode is applied again afterwards, and this is what says so.
        applied = []
        with mock.patch.object(project_helper, "PROJECT_ROOT", self.root), \
             mock.patch.object(project_helper, "_apply_mode",
                               lambda path, mode: applied.append((Path(path).name, mode))):
            project_helper.create_project({"name": "孟婆"})

        self.assertIn((".creating-孟婆", project_helper.DIRECTORY_MODE), applied)

    def test_nothing_is_left_behind_when_a_template_fails(self):
        with mock.patch.object(project_helper, "PROJECT_ROOT", self.root), \
             mock.patch("os.rename", side_effect=OSError("disk error")):
            with self.assertRaises(OSError):
                project_helper.create_project({"name": "孟婆"})

        self.assertEqual([], sorted(path.name for path in self.root.iterdir()),
                         "a failed creation must leave nothing behind, not even a staging folder")

    def test_a_name_already_taken_is_refused_and_left_alone(self):
        (self.root / "孟婆").mkdir()
        (self.root / "孟婆" / "剧本.docx").write_bytes(b"x")
        with self.assertRaises(project_helper.HelperFailure) as caught:
            self._create("孟婆")
        self.assertEqual("NAME_TAKEN", caught.exception.code)
        self.assertEqual(["剧本.docx"], sorted(p.name for p in (self.root / "孟婆").iterdir()))

    def test_a_dangling_symlink_on_the_name_still_blocks_it(self):
        # os.path.exists would say no, and the rename would then follow the link.
        try:
            (self.root / "孟婆").symlink_to(self.root / "nowhere")
        except (OSError, NotImplementedError):
            self.skipTest("creating a symlink needs a privilege Windows does not always grant.")
        with self.assertRaises(project_helper.HelperFailure) as caught:
            self._create("孟婆")
        self.assertEqual("NAME_TAKEN", caught.exception.code)

    def test_a_missing_root_is_reported_rather_than_created(self):
        with mock.patch.object(project_helper, "PROJECT_ROOT", self.root / "absent"):
            with self.assertRaises(project_helper.HelperFailure) as caught:
                project_helper.create_project({"name": "孟婆"})
        self.assertEqual("ROOT_UNAVAILABLE", caught.exception.code)

    def test_add_sheet_creates_episode_sheet_in_workbook(self):
        self._create("孟婆")
        with mock.patch.object(project_helper, "PROJECT_ROOT", self.root):
            project_helper.ensure_workbooks({"project_name": "孟婆"})
            res = project_helper.add_sheet({
                "project_name": "孟婆",
                "file_name": "节拍表.xlsx",
                "sheet_name": "第2集",
            })
        self.assertTrue(res["ok"])
        self.assertEqual("第2集", res["sheet_name"])
        import openpyxl
        wb = openpyxl.load_workbook(self.root / "孟婆" / "节拍表.xlsx")
        self.assertIn("第2集", wb.sheetnames)
        self.assertEqual(project_helper.BEAT_HEADERS, [c.value for c in wb["第2集"][1]])
        wb.close()

    def test_add_sheet_duplicate_refused(self):
        self._create("孟婆")
        with mock.patch.object(project_helper, "PROJECT_ROOT", self.root):
            project_helper.ensure_workbooks({"project_name": "孟婆"})
            project_helper.add_sheet({
                "project_name": "孟婆",
                "file_name": "节拍表.xlsx",
                "sheet_name": "第2集",
            })
            with self.assertRaises(project_helper.HelperFailure) as caught:
                project_helper.add_sheet({
                    "project_name": "孟婆",
                    "file_name": "节拍表.xlsx",
                    "sheet_name": "第2集",
                })
            self.assertEqual("SHEET_EXISTS", caught.exception.code)

    def test_ensure_workbooks_creates_missing_tables(self):
        proj = self.root / "孟婆"
        proj.mkdir()
        with mock.patch.object(project_helper, "PROJECT_ROOT", self.root):
            res = project_helper.ensure_workbooks({"project_name": "孟婆"})
        self.assertTrue(res["ok"])
        self.assertIn("节拍表.xlsx", res["created"])
        self.assertIn("资产表.xlsx", res["created"])
        self.assertTrue((proj / "节拍表.xlsx").is_file())
        self.assertTrue((proj / "资产表.xlsx").is_file())

    def test_ensure_asset_folder_creates_category_and_name_dir(self):
        self._create("孟婆")
        with mock.patch.object(project_helper, "PROJECT_ROOT", self.root):
            res = project_helper.ensure_asset_folder({
                "project_name": "孟婆",
                "category": "角色",
                "name": "孟溪",
            })
        self.assertTrue(res["ok"])
        self.assertTrue((self.root / "孟婆" / "角色" / "孟溪").is_dir())


class StubSession:
    def __init__(self, ok=True):
        self.ok = ok

    def __call__(self, token):
        if not self.ok or token != "good-token":
            raise ApiError(401, "SESSION_INVALID", "请先登录。")
        return {"authenticated": True, "user": {"id": 7, "username": "fixture"}}


class StubProvisioner:
    def __init__(self, result=None, error=None):
        self.result = result
        self.error = error
        self.calls = []

    def create_project(self, name):
        self.calls.append(name)
        if self.error is not None:
            raise self.error
        return self.result or {"ok": True, "name": name}

    def delete_project(self, name):
        self.calls.append(name)
        if self.error is not None:
            raise self.error
        return self.result or {"ok": True, "name": name, "deleted": True}


class ProjectSpaceServiceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "孟婆").mkdir()

    def tearDown(self):
        self.temp.cleanup()

    def _service(self, provisioner=None):
        return ProjectSpaceService(self.root, StubSession(), provisioner)

    def test_listing_identifies_a_project_by_its_inode(self):
        items = self._service().list_projects(None)
        self.assertEqual([{"id": str((self.root / "孟婆").stat().st_ino), "name": "孟婆"}], items)

    def test_an_unsigned_caller_is_rejected_before_anything_happens(self):
        probe = StubProvisioner()
        service = self._service(probe)
        with self.assertRaises(ApiError) as caught:
            service.authorize(None)
        self.assertEqual(401, caught.exception.status)
        self.assertEqual([], probe.calls, "the helper must not be reached without a session")

    def test_creating_returns_the_id_a_later_listing_will_report(self):
        service = self._service(StubProvisioner())

        def provision(name):
            (self.root / name).mkdir()
            return {"ok": True, "name": name}

        service._provisioner = StubProvisioner()
        service._provisioner.create_project = provision
        created = service.create_project(None, "川渝霸王龙")
        self.assertEqual("川渝霸王龙", created["name"])
        self.assertEqual(str((self.root / "川渝霸王龙").stat().st_ino), created["id"])

    def test_a_taken_name_is_a_conflict_and_a_bad_name_is_unprocessable(self):
        for code, status in (("NAME_TAKEN", 409), ("INVALID_NAME", 422), ("INTERNAL_ERROR", 500)):
            service = self._service(StubProvisioner(error=ProjectHelperFailed(code, "x")))
            with self.assertRaises(ApiError) as caught:
                service.create_project(None, "孟婆")
            self.assertEqual(status, caught.exception.status, code)

    def test_an_unreachable_helper_is_a_503_not_a_500(self):
        # Nothing was attempted, so this is "the feature is offline", not "your request was
        # wrong" — and the difference is what tells an operator where to look.
        service = self._service(StubProvisioner(error=ProjectHelperUnavailable("socket absent")))
        with self.assertRaises(ApiError) as caught:
            service.create_project(None, "孟婆")
        self.assertEqual(503, caught.exception.status)

    def test_creating_without_a_root_configured_is_a_503(self):
        service = ProjectSpaceService(None, StubSession())
        with self.assertRaises(ApiError) as caught:
            service.create_project(None, "孟婆")
        self.assertEqual(503, caught.exception.status)

    def test_delete_project_removes_project(self):
        service = self._service(StubProvisioner())
        inode = str((self.root / "孟婆").stat().st_ino)
        result = service.delete_project(None, inode)
        self.assertTrue(result["ok"])
        self.assertTrue(result["deleted"])
        self.assertEqual("孟婆", result["name"])


@unittest.skipUnless(hasattr(socket, "AF_UNIX"),
                     "Unix sockets do not exist on this platform; the helper runs on the NAS.")
class ProjectProvisionerTests(unittest.TestCase):
    def test_no_socket_configured_is_unavailable_rather_than_a_crash(self):
        with self.assertRaises(ProjectHelperUnavailable):
            ProjectProvisioner(None).create_project("孟婆")

    def test_a_missing_socket_file_is_unavailable(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ProjectHelperUnavailable):
                ProjectProvisioner(Path(directory) / "absent.sock").create_project("孟婆")


if __name__ == "__main__":
    unittest.main()
