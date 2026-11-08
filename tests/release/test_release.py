import copy
import importlib.util
import json
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('release', ROOT / 'scripts/release/release.py')
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.directory = self.root / 'assets'
        self.directory.mkdir()
        self.notes = self.root / 'docs/releases/v2.3.4.md'
        self.notes.parent.mkdir(parents=True)
        self.notes.write_text('公开版本说明', encoding='utf-8')
        self.install = self.notes.parent / 'INSTALL.md'
        self.install.write_text('安装说明', encoding='utf-8')
        for name in ['Tbox_2.3.4_osx-arm64.zip', 'Tbox_2.3.4.0_win-x64.msix', 'Tbox-Development.cer', 'Tbox_third-party-sources.tar.gz']:
            (self.directory / name).write_bytes(name.encode())
        for platform, name in [('osx-arm64', 'Tbox_2.3.4_osx-arm64.zip'), ('win-x64', 'Tbox_2.3.4.0_win-x64.msix')]:
            path = self.directory / name
            (self.directory / (platform + '-build.json')).write_text(json.dumps({'name': name, 'size': path.stat().st_size, 'sha256': release.sha256(path), 'packagedAt': '2026-11-07T12:00:00+08:00'}))
        self.source_manifest = self.root / 'source-manifest.json'
        self.source_manifest.write_text('{"pinned": "source inputs"}')
        archive = self.directory / 'Tbox_third-party-sources.tar.gz'
        (self.directory / 'third-party-build.json').write_text(json.dumps({'name': archive.name, 'size': archive.stat().st_size, 'sha256': release.sha256(archive), 'manifestSha256': release.sha256(self.source_manifest)}))
        self.source = self.root / 'src/Tbox.Desktop'
        (self.source / 'Platform/macos').mkdir(parents=True)
        (self.source / 'Platform/windows').mkdir(parents=True)
        (self.source / 'Tbox.Desktop.csproj').write_text('<Project><PropertyGroup><Version>2.3.4</Version><AssemblyVersion>2.3.4.0</AssemblyVersion><FileVersion>2.3.4.0</FileVersion></PropertyGroup></Project>')
        self.plist = self.source / 'Platform/macos/Info.plist'
        self.plist.write_bytes(plistlib.dumps({'CFBundleIdentifier': 'com.xsddpx.tbox', 'CFBundleVersion': '2.3.4.0', 'CFBundleShortVersionString': '2.3.4'}))
        self.manifest = self.source / 'Platform/windows/Package.appxmanifest'
        self.manifest.write_text('<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Identity Name="Tbox" Publisher="CN=Tbox Development" Version="2.3.4.0" ProcessorArchitecture="x64"/></Package>')
        self.history = self.root / 'docs/release-history.md'
        self.history.write_text('# History')

    def collected(self):
        return release.collect(self.directory, 'v2.3.4', self.install, self.source_manifest)

    def remote_assets(self):
        return [{'name': p.name, 'size': p.stat().st_size, 'state': 'uploaded', 'digest': 'sha256:' + release.sha256(p)} for p in sorted(self.directory.iterdir())]

    def test_accepts_consistent_new_version(self):
        self.assertEqual('2.3.4', release.preflight(self.root, 'v2.3.4'))

    def test_accepts_public_release_repository(self):
        with patch.object(release, 'api', return_value={'private': False}) as api:
            release.remote_preflight()
        api.assert_called_once_with('repos/xsddpx/Tbox')

    def test_rejects_private_or_unknown_release_visibility(self):
        for repository in [{'private': True}, {}, {'private': None}]:
            with self.subTest(repository=repository), patch.object(release, 'api', return_value=repository) as api:
                with self.assertRaisesRegex(ValueError, 'Release repository must be public'):
                    release.remote_preflight()
                api.assert_called_once_with('repos/xsddpx/Tbox')

    def test_rejects_unsafe_or_unsupported_tags(self):
        for tag in ['v2.3.4;echo secret', 'v2.3.4\n', '../v2.3.4', 'v2.3.4-beta', 'v02.3.4', 'v0.1.0', 'v2.3.65536', '2.3.4']:
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                release.version_from_tag(tag)

    def test_rejects_mismatched_project(self):
        with self.assertRaises(ValueError):
            release.preflight(self.root, 'v2.3.5')

    def test_rejects_changed_mac_identity(self):
        data = plistlib.loads(self.plist.read_bytes())
        data['CFBundleIdentifier'] = 'different.app'
        self.plist.write_bytes(plistlib.dumps(data))
        with self.assertRaises(ValueError):
            release.preflight(self.root, 'v2.3.4')

    def test_rejects_changed_windows_publisher(self):
        self.manifest.write_text(self.manifest.read_text().replace('CN=Tbox Development', 'CN=Other'))
        with self.assertRaises(ValueError):
            release.preflight(self.root, 'v2.3.4')

    def test_rejects_either_platform_already_packaged(self):
        for version in ['2.3.4', '2.3.4.0']:
            self.history.write_text(f'| {version} | date | hash |')
            with self.subTest(version=version), self.assertRaises(ValueError):
                release.preflight(self.root, 'v2.3.4')

    def test_requires_public_release_notes(self):
        self.notes.unlink()
        with self.assertRaises(ValueError):
            release.preflight(self.root, 'v2.3.4')

    def test_refuses_partial_build(self):
        (self.directory / 'Tbox_2.3.4.0_win-x64.msix').unlink()
        with self.assertRaises(ValueError):
            self.collected()

    def test_refuses_extra_private_files(self):
        (self.directory / 'signing.pfx').write_bytes(b'not-a-secret-test-fixture')
        with self.assertRaises(ValueError):
            self.collected()
        self.assertFalse((self.directory / 'release-manifest.json').exists())

    def test_refuses_empty_package(self):
        (self.directory / 'Tbox_2.3.4_osx-arm64.zip').write_bytes(b'')
        with self.assertRaises(ValueError):
            self.collected()

    def test_build_record_uses_original_packaging_time(self):
        for platform, version, suffix in [('osx-arm64', '2.3.4', '.zip'), ('win-x64', '2.3.4.0', '.msix')]:
            path = self.directory / f'Tbox_{version}_{platform}{suffix}'
            self.history.write_text(f'| {version} | platform | 2026-11-07 13:01:02 +0800 | `{release.sha256(path)}` |')
            release.build_record(self.root, self.directory, 'v2.3.4', platform)
            record = json.loads((self.directory / (platform + '-build.json')).read_text())
            self.assertEqual('2026-11-07T13:01:02+08:00', record['packagedAt'])

    def test_changed_artifact_rejected_against_build_record(self):
        (self.directory / 'Tbox_2.3.4_osx-arm64.zip').write_bytes(b'changed')
        with self.assertRaises(ValueError):
            self.collected()

    def test_missing_or_changed_source_materials_prevent_publication(self):
        source = self.directory / 'Tbox_third-party-sources.tar.gz'
        original = source.read_bytes()
        source.unlink()
        with self.assertRaises(ValueError): self.collected()
        source.write_bytes(b'changed')
        with self.assertRaises(ValueError): self.collected()
        source.write_bytes(original)
        self.source_manifest.write_text('{"pinned": "other inputs"}')
        with self.assertRaises(ValueError): self.collected()

    def test_checksums_match_artifacts(self):
        manifest = self.collected()
        for a in manifest['assets']:
            self.assertEqual(release.sha256(self.directory / a['name']), a['sha256'])
            self.assertIn(a['sha256'] + '  ' + a['name'], (self.directory / 'SHA256SUMS').read_text())
        release.validate_assets({'assets': self.remote_assets()}, list(self.directory.iterdir()))

    def test_rejects_incomplete_or_tampered_upload(self):
        self.collected()
        valid = self.remote_assets()
        variations = [valid[:-1], valid + [valid[0]]]
        for field, value in [('digest', 'sha256:' + '0' * 64), ('size', 0), ('state', 'starter')]:
            altered = copy.deepcopy(valid)
            altered[0][field] = value
            variations.append(altered)
        for assets in variations:
            with self.subTest(assets=assets), self.assertRaises(ValueError):
                release.validate_assets({'assets': assets}, list(self.directory.iterdir()))

    def test_rejects_existing_draft_without_modifying_it(self):
        with patch.object(release, 'command', return_value=json.dumps([[{'tag_name': 'v2.3.4', 'draft': True}]])):
            with self.assertRaises(ValueError):
                release.assert_unused_tag('v2.3.4')

    def transaction(self, *, bad_upload=False, audit_fails=False, upload_fails=False, prerelease=False):
        self.collected()
        calls = []
        assets = self.remote_assets()
        if bad_upload:
            assets[0]['digest'] = None
        def fake_api(path, method='GET', body=None, **kwargs):
            calls.append((method, body))
            if method == 'POST':
                return {'id': 7, 'draft': True}
            return {'id': 7, 'draft': method != 'PATCH', 'prerelease': prerelease, 'assets': assets, 'html_url': 'https://example.invalid/release'}
        def audit(*args):
            calls.append(('AUDIT', None))
            if audit_fails:
                raise RuntimeError('Simulated persistence failure')
        def upload(*args, **kwargs):
            calls.append(('UPLOAD', None))
            if upload_fails:
                raise subprocess.CalledProcessError(1, 'mock upload')
        with patch.object(release, 'assert_unused_tag'), patch.object(release, 'api', side_effect=fake_api), patch.object(release, 'command', side_effect=upload):
            if bad_upload or audit_fails or upload_fails:
                with self.assertRaises((ValueError, RuntimeError, subprocess.CalledProcessError)):
                    release.publish(self.root, self.directory, 'v2.3.4', prerelease, audit=audit)
                self.assertFalse(any(method == 'PATCH' for method, body in calls))
            else:
                release.publish(self.root, self.directory, 'v2.3.4', prerelease, audit=audit)
        return calls

    def test_publishes_only_after_complete_upload_and_audit(self):
        with patch.dict(release.os.environ, {'GITHUB_SHA': 'tested-commit'}):
            calls = self.transaction()
        self.assertEqual(['POST', 'UPLOAD', 'GET', 'AUDIT', 'PATCH'], [c[0] for c in calls])
        self.assertTrue(calls[0][1]['draft'])
        self.assertEqual('tested-commit', calls[0][1]['target_commitish'])
        self.assertIn('Tbox_2.3.4.0_win-x64.msix', calls[0][1]['body'])
        self.assertIn('Tbox_2.3.4_osx-arm64.zip', calls[0][1]['body'])
        self.assertIn('Source code (zip)', calls[0][1]['body'])
        self.assertFalse(calls[-1][1]['draft'])
        self.assertEqual('true', calls[-1][1]['make_latest'])

    def test_prerelease_never_promoted_to_latest(self):
        calls = self.transaction(prerelease=True)
        self.assertTrue(calls[-1][1]['prerelease'])
        self.assertEqual('false', calls[-1][1]['make_latest'])

    def test_upload_failure_keeps_draft(self):
        self.transaction(upload_fails=True)

    def test_hash_mismatch_keeps_draft(self):
        self.transaction(bad_upload=True)

    def test_history_failure_keeps_draft(self):
        self.transaction(audit_fails=True)


if __name__ == '__main__':
    unittest.main()
