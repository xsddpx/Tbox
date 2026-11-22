import importlib.util
import plistlib
import stat
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile, ZipInfo


spec = importlib.util.spec_from_file_location(
    'lid_package', Path(__file__).resolve().parents[2] / 'scripts/verify-macos-lid-package.py'
)
lid_package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lid_package)


class LidPackageTests(unittest.TestCase):
    registration = 'Tbox.app/Contents/Library/LaunchDaemons/com.xsddpx.tbox.lid-sleep.plist'
    program = 'Contents/MacOS/TboxLidSleep'
    helper = f'Tbox.app/{program}'

    def write_package(self, path, *, private_entry=None):
        entries = {
            'Tbox.app/': (stat.S_IFDIR | 0o755, b''),
            'Tbox.app/Contents/': (stat.S_IFDIR | 0o755, b''),
            'Tbox.app/Contents/Info.plist': (stat.S_IFREG | 0o644, b''),
            'Tbox.app/Contents/Library/': (stat.S_IFDIR | 0o755, b''),
            'Tbox.app/Contents/Library/LaunchDaemons/': (stat.S_IFDIR | 0o755, b''),
            'Tbox.app/Contents/MacOS/': (stat.S_IFDIR | 0o755, b''),
            self.registration: (stat.S_IFREG | 0o644, plistlib.dumps({'BundleProgram': self.program})),
            self.helper: (stat.S_IFREG | 0o755, b''),
        }
        with ZipFile(path, 'w') as archive:
            for name, (mode, contents) in entries.items():
                entry = ZipInfo(name)
                entry.external_attr = (mode & ~0o077 if name == private_entry else mode) << 16
                archive.writestr(entry, contents)

    def test_accepts_bundle_with_readable_launch_plist_and_executable_helper(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'Tbox.zip'
            self.write_package(path)
            lid_package.verify(path)

    def test_rejects_owner_only_bundle_directory_plist_and_helper(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'Tbox.zip'
            for private_entry in ('Tbox.app/', self.registration, self.helper):
                with self.subTest(private_entry=private_entry):
                    self.write_package(path, private_entry=private_entry)
                    with self.assertRaisesRegex(ValueError, 'ZIP permissions'):
                        lid_package.verify(path)
