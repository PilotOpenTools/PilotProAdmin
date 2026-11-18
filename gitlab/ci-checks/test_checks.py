"""Самопроверка скриптов проверок на временных git-репозиториях: python3 -m unittest test_checks"""
import os
import subprocess
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
ZERO = "0" * 40


def trigger(version, extra=""):
    return '[{"_comment_": "Version: %s", "when": {"x": 1}%s}]' % (version, extra)


class Repo:
    def __init__(self, root):
        self.root = root
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.email", "t@example.local")
        self.git("config", "user.name", "t")
        self.git("config", "commit.gpgsign", "false")

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.root, check=True, capture_output=True, text=True, encoding="utf-8")

    def put(self, path, text):
        full = os.path.join(self.root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8", newline="") as f:
            f.write(text)

    def commit(self, msg="c"):
        self.git("add", "-A")
        self.git("commit", "-q", "-m", msg)

    def run(self, script, **env):
        e = {k: v for k, v in os.environ.items() if not k.startswith("CI_")}
        e.update(PYTHONUTF8="1", **env)
        r = subprocess.run([sys.executable, os.path.join(HERE, script)], cwd=self.root, env=e,
                           capture_output=True, text=True, encoding="utf-8")
        return r.returncode, r.stdout + r.stderr


class ChecksTest(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.repo = Repo(self._tmp.name)

    def base_main(self):
        self.repo.put("A.json", trigger("1.0.0"))
        self.repo.commit("base")
        self.repo.git("update-ref", "refs/remotes/origin/main", "HEAD")
        self.repo.git("checkout", "-q", "-b", "feature")

    def new_branch(self, script="check_automations.py"):
        # push новой ветки: CI_COMMIT_BEFORE_SHA нулевой
        return self.repo.run(script, CI_COMMIT_BEFORE_SHA=ZERO, CI_COMMIT_BRANCH="feature")

    def test_new_branch_checks_whole_branch_not_last_commit(self):
        self.base_main()
        self.repo.put("A.json", "{ битый json")
        self.repo.commit("bad")
        self.repo.put("README.md", "x")
        self.repo.commit("harmless")
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertIn("некорректный JSON", out)

    def test_new_branch_version_bump_ok(self):
        self.base_main()
        self.repo.put("A.json", trigger("1.0.1"))
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 0, out)

    def test_version_not_bumped(self):
        self.base_main()
        self.repo.put("A.json", trigger("1.0.0", ', "z": 2'))
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertIn("версия не повышена", out)

    def test_version_only_from_first_trigger_comment(self):
        self.base_main()
        self.repo.put("A.json", '// Version: 9.9.9\n[{"when": {"x": 1}}]')
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertIn("нет «Version", out)

    def test_cs_only_change_requires_version_bump(self):
        self.base_main()
        self.repo.put("A/A.cs", "class A {}")
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertIn("повысьте версию", out)

    def test_cs_with_version_bump_ok(self):
        self.base_main()
        self.repo.put("A/A.cs", "class A {}")
        self.repo.put("A.json", trigger("1.1.0"))
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 0, out)

    def test_two_automations_in_branch(self):
        self.base_main()
        self.repo.put("A.json", trigger("1.0.1"))
        self.repo.commit()
        self.repo.put("B.json", trigger("1.0.0"))
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertIn("несколько автоматизаций", out)

    def test_no_remote_base_checks_all_files(self):
        self.repo.put("A.json", "{ битый json")
        self.repo.commit()
        rc, out = self.repo.run("check_automations.py", CI_COMMIT_BEFORE_SHA=ZERO, CI_COMMIT_BRANCH="main")
        self.assertEqual(rc, 1, out)

    def test_mr_base_sha_used(self):
        self.base_main()
        base = self.repo.git("rev-parse", "HEAD").stdout.strip()
        self.repo.put("A.json", trigger("1.0.0", ', "z": 2'))
        self.repo.commit()
        rc, out = self.repo.run("check_automations.py", CI_MERGE_REQUEST_DIFF_BASE_SHA=base)
        self.assertEqual(rc, 1, out)

    def test_reports(self):
        self.repo.put("keep.txt", "x")
        self.repo.commit("base")
        self.repo.git("update-ref", "refs/remotes/origin/main", "HEAD")
        self.repo.git("checkout", "-q", "-b", "feature")
        self.repo.put("ok.repx", '<?xml version="1.0"?><XtraReportsLayoutSerializer/>')
        self.repo.commit()
        self.assertEqual(self.new_branch("check_reports.py")[0], 0)
        self.repo.put("bad.repx", '<?xml version="1.0"?><Other/>')
        self.repo.commit()
        rc, out = self.new_branch("check_reports.py")
        self.assertEqual(rc, 1, out)
        self.assertIn("это не макет отчёта", out)

    def test_reports_entity_expansion_rejected(self):
        self.repo.put("keep.txt", "x")
        self.repo.commit("base")
        self.repo.git("update-ref", "refs/remotes/origin/main", "HEAD")
        self.repo.git("checkout", "-q", "-b", "feature")
        self.repo.put("e.repx", '<?xml version="1.0"?><!DOCTYPE r [<!ENTITY a "aaaa">]>'
                                '<XtraReportsLayoutSerializer>&a;</XtraReportsLayoutSerializer>')
        self.repo.commit()
        rc, out = self.new_branch("check_reports.py")
        self.assertEqual(rc, 1, out)
        self.assertIn("DOCTYPE", out)

    def put_bytes(self, path, data):
        full = os.path.join(self.repo.root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "wb") as f:
            f.write(data)

    def test_json_in_subfolder_rejected(self):
        self.base_main()
        self.repo.put("sub/D.json", "{ мусор")
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertIn("sub/D.json: JSON триггеров должен лежать в корне", out)

    def test_hidden_folder_and_deleted_files_ignored(self):
        self.repo.put("sub/D.json", "{}")
        self.base_main()
        os.remove(os.path.join(self.repo.root, "sub", "D.json"))
        self.repo.put(".vscode/settings.json", "{}")
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 0, out)

    def test_hidden_folder_cs_ignored_and_extension_case_insensitive(self):
        self.base_main()
        self.repo.put(".vscode/x.cs", "class X {}")
        self.repo.put("docs/Y.JSON", "{}")
        self.repo.put("lib/Z.CS", "class Z {}")
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertNotIn(".vscode", out)
        self.assertIn("docs/Y.JSON: JSON триггеров должен лежать в корне", out)
        self.assertIn("lib/Z.CS: C#-файл должен лежать в Имя/Имя.cs", out)

    def test_cs_outside_layout_rejected(self):
        self.base_main()
        self.repo.put("X.cs", "class X {}")
        self.repo.put("A/lib/B.cs", "class B {}")
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertIn("X.cs: C#-файл должен лежать в Имя/Имя.cs", out)
        self.assertIn("A/lib/B.cs: C#-файл должен лежать в Имя/Имя.cs", out)

    def test_subfolders_checked_only_inside_automation_dir(self):
        self.base_main()
        self.repo.put("sub/D.json", "{}")
        self.repo.put("auto/x/y.json", "{}")
        self.repo.commit()
        rc, out = self.repo.run("check_automations.py", CI_COMMIT_BEFORE_SHA=ZERO, CI_COMMIT_BRANCH="feature",
                                AUTOMATION_DIR="auto")
        self.assertEqual(rc, 1, out)
        self.assertIn("auto/x/y.json", out)
        self.assertNotIn("sub/D.json", out)

    def test_cs_without_activity_declaration_warns_only(self):
        self.base_main()
        self.repo.put("A/A.cs", "class A {}")
        self.repo.put("A.json", trigger("1.1.0"))
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 0, out)
        self.assertIn("нет объявления действия", out)
        self.repo.put("A/A.cs", 'class A { public string Name => "Do"; }')
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 0, out)
        self.assertNotIn("нет объявления действия", out)

    def test_cs_not_utf8_rejected(self):
        self.base_main()
        self.put_bytes("A/A.cs", b"class A { // \xff\xfe }")
        self.repo.put("A.json", trigger("1.1.0"))
        self.repo.commit()
        rc, out = self.new_branch()
        self.assertEqual(rc, 1, out)
        self.assertIn("файл не в UTF-8", out)

    def test_reports_in_subfolders_checked(self):
        self.repo.put("keep.txt", "x")
        self.repo.commit("base")
        self.repo.git("update-ref", "refs/remotes/origin/main", "HEAD")
        self.repo.git("checkout", "-q", "-b", "feature")
        self.repo.put("dir/sub dir/Отчёт.repx", '<?xml version="1.0"?><Other/>')
        self.repo.put("dir/UP.REPX", '<?xml version="1.0"?><Other/>')
        self.repo.commit()
        rc, out = self.new_branch("check_reports.py")
        self.assertEqual(rc, 1, out)
        self.assertIn("dir/sub dir/Отчёт.repx", out)
        self.assertIn("dir/UP.REPX", out)


if __name__ == "__main__":
    unittest.main()
