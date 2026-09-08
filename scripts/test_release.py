"""Release guards must reject mismatched versions and substituted package bytes."""

import io
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import release


class ReleaseTests(unittest.TestCase):
    def package(self, *, commit="a" * 40, version="1.0.0", dependency="", signature=False):
        data = io.BytesIO()
        with zipfile.ZipFile(data, "w") as package:
            package.writestr("WuKongEasySDK.nuspec", f'''<package xmlns="urn:test"><metadata>
                <id>WuKongEasySDK</id><version>{version}</version>
                <repository url="https://github.com/WuKongIM/WuKongEasySDK-CSharp" commit="{commit}" />
                <dependencies>{dependency}</dependencies></metadata></package>''')
            for name in ("README.md", "lib/net8.0/WuKongEasySDK.dll", "lib/net8.0/WuKongEasySDK.xml"):
                package.writestr(name, "fixture")
            if signature:
                package.writestr(".signature.p7s", "NuGet signature")
        return data.getvalue()

    def test_registry_signature_does_not_change_payload(self):
        self.assertEqual(release.package_files(self.package(), "1.0.0", "a" * 40),
                         release.package_files(self.package(signature=True), "1.0.0", "a" * 40))

    def test_wrong_commit_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "commit mismatch"):
            release.package_files(self.package(commit="b" * 40), "1.0.0", "a" * 40)

    def test_wrong_version_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "version"):
            release.package_files(self.package(version="1.0.1"), "1.0.0", "a" * 40)

    def test_runtime_dependency_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "dependencies"):
            release.package_files(self.package(dependency='<dependency id="unexpected" />'), "1.0.0", "a" * 40)

    def metadata(self, changelog, ref="refs/heads/main"):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "src/WuKongEasySDK").mkdir(parents=True)
            (root / "src/WuKongEasySDK/WuKongEasySDK.csproj").write_text(
                '<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>')
            (root / "CHANGELOG.md").write_text(changelog)
            with patch.object(release, "ROOT", root), patch.dict(os.environ, GITHUB_REF=ref):
                return release.release_version()

    def test_exact_version_notes(self):
        self.assertEqual(self.metadata("## [Unreleased]\n\n## [1.0.0]\n\n- Initial SDK.\n"),
                         ("1.0.0", "- Initial SDK."))

    def test_missing_empty_or_duplicate_notes_are_rejected(self):
        for notes in ("## [Unreleased]\n- Initial SDK.", "## [1.0.0]\n",
                      "## [1.0.0]\n- One.\n## [1.0.0]\n- Two."):
            with self.subTest(notes=notes), self.assertRaises(ValueError):
                self.metadata(notes)

    def test_wrong_release_tag_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Tag"):
            self.metadata("## [1.0.0]\n- Initial SDK.", "refs/tags/v1.0.1")


if __name__ == "__main__":
    unittest.main()
