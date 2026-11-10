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
        self.directory = self.root / 'release-assets'
        self.directory.mkdir()
        self.notes = self.root / 'docs/releases/v2.3.4.md'
        self.notes.parent.mkdir(parents=True)
        self.notes.write_text('公开版本说明', encoding='utf-8')
        for name in ['Tbox_2.3.4_osx-arm64.zip', 'Tbox_2.3.4.0_win-x64.msix', 'Tbox-Development.cer', 'Tbox_third-party-sources.tar.gz']:
            (self.directory / name).write_bytes(name.encode())
        for platform, name in [('osx-arm64', 'Tbox_2.3.4_osx-arm64.zip'), ('win-x64', 'Tbox_2.3.4.0_win-x64.msix')]:
            path = self.directory / name
            (self.directory / (platform + '-build.json')).write_text(json.dumps({'name': name, 'size': path.stat().st_size, 'sha256': release.sha256(path), 'packagedAt': '2026-11-07T12:00:00+08:00'}))
        self.source_manifest = self.root / 'assets/third-party/media-sources.json'
        self.source_manifest.parent.mkdir(parents=True)
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
        return release.collect(self.directory, 'v2.3.4', self.source_manifest)

    def remote_asset(self, name):
        path = self.directory / name
        return {'name': name, 'size': path.stat().st_size, 'state': 'uploaded', 'digest': 'sha256:' + release.sha256(path)}

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

    def test_collection_keeps_signed_artifacts_and_audits_reusable(self):
        before = set(self.directory.iterdir())
        first = self.collected()
        self.assertEqual(first, self.collected())
        self.assertEqual(before, set(self.directory.iterdir()))
        for name, asset in first['assets'].items():
            self.assertEqual(release.sha256(self.directory / name), asset['sha256'])
        release.validate_assets({'assets': [self.remote_asset(name) for name in first['assets']]}, first['assets'])

    def test_rejects_incomplete_or_tampered_upload(self):
        expected = self.collected()['assets']
        valid = [self.remote_asset(name) for name in expected]
        variations = [valid[:-1], valid + [valid[0]]]
        for field, value in [('digest', 'sha256:' + '0' * 64), ('size', 0), ('state', 'starter')]:
            altered = copy.deepcopy(valid)
            altered[0][field] = value
            variations.append(altered)
        for assets in variations:
            with self.subTest(assets=assets), self.assertRaises(ValueError):
                release.validate_assets({'assets': assets}, expected)

    def transaction(self, *, legacy_support=True):
        state = {}
        calls = []
        commit = 'a' * 40
        if legacy_support:
            state['v1.1.50'] = {'id': 1, 'tag_name': 'v1.1.50', 'draft': False,
                                'assets': [self.remote_asset(name) for name in release.SUPPORT_FILES]}

        def fake_api(path, method='GET', body=None):
            if method == 'POST':
                tag = body['tag_name']
                self.assertNotIn(tag, state)
                calls.append(('POST', tag))
                state[tag] = {**body, 'id': len(state) + 1, 'assets': [],
                              'html_url': 'https://example.invalid/' + tag}
                return copy.deepcopy(state[tag])
            current = next(value for value in state.values() if path.endswith(f"/{value['id']}"))
            if method == 'PATCH':
                calls.append(('PATCH', current['tag_name']))
                current.update(body)
            return copy.deepcopy(current)

        def upload(*args):
            self.assertEqual(('gh', 'release', 'upload'), args[:3])
            self.assertNotIn('--clobber', args)
            tag = args[3]
            names = [Path(path).name for path in args[6:]]
            calls.append(('UPLOAD', tag, names))
            for name in names:
                self.assertNotIn(name, [asset['name'] for asset in state[tag]['assets']])
                state[tag]['assets'].append(self.remote_asset(name))

        def publish(*, run='128', prerelease=False, audit_fails=False, upload_handler=upload):
            def audit(*args):
                calls.append(('AUDIT',))
                if audit_fails:
                    raise RuntimeError('Simulated persistence failure')

            with patch.dict(release.os.environ, {'GITHUB_RUN_ID': run}), \
                 patch.object(release, 'source_commit', return_value=commit), \
                 patch.object(release, 'releases', side_effect=lambda: copy.deepcopy(state)), \
                 patch.object(release, 'api', side_effect=fake_api), \
                 patch.object(release, 'command', side_effect=upload_handler):
                release.publish(self.root, self.directory, 'v2.3.4', prerelease, audit=audit)

        return state, calls, publish, upload

    def test_publishes_only_the_two_installers_after_audit(self):
        state, calls, publish, _ = self.transaction()
        publish()
        app = state['v2.3.4']
        self.assertEqual([('POST', 'v2.3.4'), ('UPLOAD', 'v2.3.4', ['Tbox_2.3.4.0_win-x64.msix', 'Tbox_2.3.4_osx-arm64.zip']),
                          ('AUDIT',), ('PATCH', 'v2.3.4')], calls)
        self.assertEqual('a' * 40, app['target_commitish'])
        self.assertEqual('Tbox v2.3.4', app['name'])
        self.assertNotIn('Actions 128', app['body'])
        self.assertIn('/blob/v2.3.4/docs/releases/INSTALL.md', app['body'])
        self.assertIn('/download/v1.1.50/Tbox-Development.cer', app['body'])
        self.assertIn('/download/v1.1.50/Tbox_third-party-sources.tar.gz', app['body'])
        for asset in app['assets']:
            self.assertIn(asset['digest'].removeprefix('sha256:'), app['body'])
        self.assertFalse(app['draft'])
        self.assertEqual('true', app['make_latest'])

    def test_prerelease_never_promoted_to_latest(self):
        state, _, publish, _ = self.transaction()
        publish(prerelease=True)
        self.assertTrue(state['v2.3.4']['prerelease'])
        self.assertEqual('false', state['v2.3.4']['make_latest'])

    def test_resumes_uploaded_draft_after_audit_failure(self):
        state, calls, publish, _ = self.transaction()
        with self.assertRaisesRegex(RuntimeError, 'persistence failure'):
            publish(audit_fails=True)
        self.assertTrue(state['v2.3.4']['draft'])
        self.assertEqual('Tbox v2.3.4 · Actions 128', state['v2.3.4']['name'])
        self.assertNotIn(('PATCH', 'v2.3.4'), calls)
        calls.clear()
        publish()
        self.assertEqual([('AUDIT',), ('PATCH', 'v2.3.4')], calls)
        calls.clear()
        publish()
        self.assertEqual([('AUDIT',)], calls)

    def test_resumes_partial_upload_without_replacing_existing_file(self):
        state, calls, publish, upload = self.transaction()

        def partial(*args):
            upload(*args[:-1])
            raise subprocess.CalledProcessError(1, 'mock upload')

        with self.assertRaises(subprocess.CalledProcessError):
            publish(upload_handler=partial)
        self.assertTrue(state['v2.3.4']['draft'])
        self.assertEqual(1, len(state['v2.3.4']['assets']))
        calls.clear()
        publish()
        self.assertEqual([('UPLOAD', 'v2.3.4', ['Tbox_2.3.4_osx-arm64.zip']),
                          ('AUDIT',), ('PATCH', 'v2.3.4')], calls)

    def test_refuses_other_run_and_tampered_draft(self):
        state, calls, publish, _ = self.transaction()
        with self.assertRaises(RuntimeError):
            publish(audit_fails=True)
        calls.clear()
        with self.assertRaisesRegex(ValueError, 'does not match this transaction'):
            publish(run='129')
        state['v2.3.4']['assets'][0]['digest'] = 'sha256:' + '0' * 64
        with self.assertRaisesRegex(ValueError, 'SHA-256 differs'):
            publish()
        self.assertEqual([], calls)
        self.assertTrue(state['v2.3.4']['draft'])

    def test_archives_new_support_materials_separately_and_reuses_them(self):
        state, calls, publish, _ = self.transaction(legacy_support=False)
        with self.assertRaises(RuntimeError):
            publish(audit_fails=True)
        support_tag = next(tag for tag in state if tag.startswith('support-'))
        self.assertTrue(state[support_tag]['draft'])
        self.assertTrue(state['v2.3.4']['draft'])
        self.assertNotIn(('PATCH', support_tag), calls)
        calls.clear()
        publish()
        self.assertEqual([('AUDIT',), ('PATCH', support_tag), ('PATCH', 'v2.3.4')], calls)
        self.assertEqual(set(release.SUPPORT_FILES), {asset['name'] for asset in state[support_tag]['assets']})
        self.assertTrue(state[support_tag]['prerelease'])
        self.assertEqual('false', state[support_tag]['make_latest'])
        self.assertIn(f'/download/{support_tag}/Tbox_third-party-sources.tar.gz', state['v2.3.4']['body'])

    def test_retries_audit_push_and_reuses_verified_remote_record(self):
        manifest = self.collected()
        calls = []
        pushed = False
        attempts = 0

        def git(*args):
            nonlocal pushed, attempts
            calls.append(args)
            if args[1] == 'ls-remote':
                return 'matching-ref' if pushed else ''
            if args[1] == 'show':
                return self.history.read_text() if args[2].startswith('FETCH_HEAD:') else '# History'
            if args[1] == 'push':
                attempts += 1
                if attempts == 1:
                    raise subprocess.CalledProcessError(1, 'mock git push')
                pushed = True
            return ''

        with patch.object(release, 'command', side_effect=git), patch.object(release.time, 'sleep'), \
             patch.object(release, 'api', return_value={'html_url': 'https://example.invalid/pr'}):
            release.record_history(self.root, 'v2.3.4', manifest)
            self.assertEqual(2, attempts)
            release.record_history(self.root, 'v2.3.4', manifest)
        self.assertEqual(2, attempts)
        self.assertEqual(1, sum(args[1] == 'switch' for args in calls))
        rows = release.audit_rows(manifest)
        self.assertTrue(release.has_audit(self.history.read_text(), 'v2.3.4', rows))
        with self.assertRaisesRegex(ValueError, 'differs from the signed packages'):
            release.has_audit(self.history.read_text().replace(rows[0], ''), 'v2.3.4', rows)


if __name__ == '__main__':
    unittest.main()
