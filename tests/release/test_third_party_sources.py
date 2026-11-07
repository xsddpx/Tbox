import copy
import importlib.util
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
import shutil

spec = importlib.util.spec_from_file_location('sources', Path(__file__).resolve().parents[2] / 'scripts/release/prepare-third-party-sources.py')
sources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sources)


class ThirdPartySourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.cache = self.root / 'cache'
        self.output = self.root / 'output'
        self.manifest_path = self.root / 'manifest.json'
        for name in ['upstream/example.tar.gz', 'git/example.tar.gz']:
            path = self.cache / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
        self.manifest = dict(schemaVersion=1, components={'VLC': '3.0.24'},
            archives=[dict(path='upstream/example.tar.gz', sha256=sources.sha256(self.cache / 'upstream/example.tar.gz'), urls=['https://example.invalid/source'])],
            git=[dict(path='git/example.tar.gz', sha256=sources.sha256(self.cache / 'git/example.tar.gz'), url='https://example.invalid/repo.git', commit='a' * 40, prefix='example/')])
        self.save()
        for name in ['docs/third-party-sources.md', 'scripts/prepare-windows-media.sh', 'scripts/prepare-macos-media.sh', 'scripts/verify-macos-deployment-target.py', 'src/Tbox.Desktop/ThirdParty/LICENSE.txt']:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text('public build material\n', encoding='utf-8')

    def save(self):
        self.manifest_path.write_text(json.dumps(self.manifest), encoding='utf-8')

    def build(self, output=None):
        return sources.build_bundle(self.root, self.manifest_path, self.cache, output or self.output)

    def test_bundle_contains_only_declared_public_materials(self):
        (self.cache / 'private.pfx').write_bytes(b'not-a-secret-test-fixture')
        record = self.build()
        self.assertEqual(record['inputCount'], 2)
        self.assertEqual(record['sha256'], sources.sha256(self.output / sources.ARCHIVE_NAME))
        with tarfile.open(self.output / sources.ARCHIVE_NAME) as archive:
            names = archive.getnames()
            self.assertIn('tbox-third-party-source/licenses/LICENSE.txt', names)
            self.assertIn('tbox-third-party-source/source-manifest.json', names)
            self.assertIn('tbox-third-party-source/scripts/verify-macos-deployment-target.py', names)
            self.assertFalse(any('private' in name for name in names))
            self.assertTrue(all(member.isfile() for member in archive.getmembers()))

    def test_bundle_is_reproducible_and_refuses_overwrite(self):
        first = self.build()
        second = self.build(self.root / 'another-output')
        self.assertEqual(first, second)
        with self.assertRaises(ValueError): self.build()

    def test_missing_or_tampered_input_prevents_output(self):
        path = self.cache / 'upstream/example.tar.gz'
        path.write_bytes(b'changed')
        with self.assertRaises(ValueError): self.build()
        path.unlink()
        with self.assertRaises(ValueError): self.build()
        self.assertFalse(self.output.exists())

    def test_unlisted_local_path_or_duplicate_is_rejected(self):
        for value in ['../private.pfx', '/absolute/path', 'a\\b', 'a/../b', 'git/example.tar.gz']:
            with self.subTest(path=value):
                before = copy.deepcopy(self.manifest)
                self.manifest['archives'][0]['path'] = value
                self.save()
                with self.assertRaises(ValueError): sources.load_manifest(self.manifest_path)
                self.manifest = before

    def test_unpinned_revision_and_credential_url_are_rejected(self):
        for field, value in [('commit', 'main'), ('commit', 'b56ffce0'), ('url', 'https://token@example.invalid/repo'), ('prefix', '../private/')]:
            with self.subTest(field=field, value=value):
                before = copy.deepcopy(self.manifest)
                self.manifest['git'][0][field] = value
                self.save()
                with self.assertRaises(ValueError): sources.load_manifest(self.manifest_path)
                self.manifest = before

    def test_verified_cache_needs_no_network(self):
        self.assertEqual(sources.fetch_archive(self.manifest['archives'][0], self.cache), self.cache / 'upstream/example.tar.gz')
        self.assertEqual(sources.fetch_git(self.manifest['git'][0], self.cache), self.cache / 'git/example.tar.gz')

    def test_runtime_upgrade_requires_corresponding_source_update(self):
        repository = Path(__file__).resolve().parents[2]
        manifest = sources.load_manifest(repository / 'assets/third-party/media-sources.json')
        for name in ['src/Tbox.Desktop/Tbox.Desktop.csproj', 'scripts/prepare-windows-media.sh', 'scripts/prepare-macos-media.sh']:
            target = self.root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(repository / name, target)
        sources.validate_versions(self.root, manifest)
        manifest['components']['VLC'] = 'different-version'
        with self.assertRaises(ValueError): sources.validate_versions(self.root, manifest)
        manifest = sources.load_manifest(repository / 'assets/third-party/media-sources.json')
        for item in manifest['archives']:
            if item['path'].startswith('upstream/ffmpeg-'): item['sha256'] = '0' * 64
        with self.assertRaises(ValueError): sources.validate_versions(self.root, manifest)
