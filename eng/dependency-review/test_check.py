# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.

import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

import check


ROOT = Path(__file__).resolve().parents[2]
POLICY = json.loads((Path(__file__).parent / "constraints.json").read_text())
PACKAGES = (
    "<Project><PropertyGroup>"
    "<Npgsql8Version>8.0.9</Npgsql8Version><Npgsql9Version>9.0.5</Npgsql9Version>"
    "</PropertyGroup><ItemGroup>"
    + "".join(f'<PackageVersion Include="{name}" Version="{rule["version"]}"/>'
              for name, rule in POLICY["packages"].items())
    + '<PackageVersion Include="Dapper" Version="2.1.86"/>'
    "</ItemGroup></Project>"
)


class ConstraintTests(unittest.TestCase):
    def test_constraints_baseline(self):
        self.assertEqual([], check.check_packages(PACKAGES, POLICY))

    def test_each_documented_hold_is_enforced(self):
        for name, rule in POLICY["packages"].items():
            with self.subTest(package=name):
                old = f'Include="{name}" Version="{rule["version"]}"'
                self.assertEqual(1, PACKAGES.count(old))
                changed = PACKAGES.replace(old, f'Include="{name}" Version="99.0.0"')
                self.assertEqual(1, len(check.check_packages(changed, POLICY)))

    def test_coupled_majors(self):
        changed = PACKAGES
        for name in POLICY["properties"]:
            changed = re.sub(rf"<{name}>[^<]+", f"<{name}>10.0.0", changed)
        self.assertEqual(2, len(check.check_packages(changed, POLICY)))

    def test_patch_and_unrelated_updates_are_allowed(self):
        changed = PACKAGES.replace("<Npgsql8Version>8.0.9", "<Npgsql8Version>8.0.99")
        changed = changed.replace('Include="Dapper" Version="2.1.86"', 'Include="Dapper" Version="3.0.0"')
        self.assertEqual([], check.check_packages(changed, POLICY))

    def test_missing_duplicate_and_indirect_values_fail_closed(self):
        for value in ("", "<Npgsql8Version>8.0.9</Npgsql8Version>" * 2,
                      "<Npgsql8Version>$(OtherVersion)</Npgsql8Version>"):
            with self.subTest(value=value):
                changed = PACKAGES.replace("<Npgsql8Version>8.0.9</Npgsql8Version>", value)
                self.assertEqual(1, len(check.check_packages(changed, POLICY)))

    def test_source_boundaries(self):
        for value in (
            "https://pkgs.dev.azure.com/dnceng/public/_packaging/a",
            "https://dnceng.pkgs.visualstudio.com/public/_packaging/a",
        ):
            with self.subTest(value=value):
                self.assertTrue(check.approved_source(value))
        for value in (
            "http://pkgs.dev.azure.com/dnceng/public/a",
            "https://pkgs.dev.azure.com/other/a",
            "https://pkgs.dev.azure.com/dnceng-evil/a",
            "https://pkgs.dev.azure.com.evil/dnceng/a",
            "https://user@pkgs.dev.azure.com/dnceng/a",
            "https://api.nuget.org/v3/index.json",
            "file:///tmp/packages",
        ):
            with self.subTest(value=value):
                self.assertFalse(check.approved_source(value))

    def test_audit_source_is_not_a_download_source(self):
        text = '<configuration><auditSources><add value="https://data.nuget.org/v3/index.json"/></auditSources></configuration>'
        self.assertEqual([], check.check_nuget(text))
        text = text.replace("auditSources", "packageSources")
        self.assertEqual(1, len(check.check_nuget(text)))

    def test_yarn_checks_resolutions_not_selector_versions(self):
        text = '"serialize-javascript@^7.1.1":\n  version "7.0.5"\n  resolved "https://pkgs.dev.azure.com/dnceng/public/npm/pkg.tgz#hash"\n'
        self.assertEqual([], check.check_yarn(text))
        self.assertEqual(1, len(check.check_yarn(text.replace("pkgs.dev.azure.com/dnceng", "registry.npmjs.org"))))
        self.assertEqual(1, len(check.check_yarn('  resolved not-a-quoted-url')))

    def test_cli_missing_inputs_are_errors_not_success(self):
        with tempfile.TemporaryDirectory() as directory:
            result = subprocess.run([sys.executable, str(Path(check.__file__)), "--root", directory],
                                    capture_output=True, text=True, check=False)
        self.assertEqual(2, result.returncode)
        self.assertIn("could not complete", result.stderr)


if __name__ == "__main__":
    unittest.main()
