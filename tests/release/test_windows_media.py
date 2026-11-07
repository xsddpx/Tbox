import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('windows_media', Path(__file__).resolve().parents[2] / 'scripts/verify-windows-media.py')
media = importlib.util.module_from_spec(spec)
spec.loader.exec_module(media)


class WindowsMediaTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.runtime = self.root / 'runtime'
        self.recipe = self.root / 'build.sh'
        self.recipe.write_text('fixed recipe')
        for name in media.FILES:
            path = self.runtime / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'fixture content')
        self.record = dict(ffmpeg='8.1.2', libwebp='1.6.0', target='win-x64', license='LGPL-2.1-or-later',
            sources=[dict(name=k, sha256=v) for k, v in media.SOURCES.items()], systemImports=['KERNEL32.dll'],
            binarySha256=media.sha256(self.runtime / 'bin/ffmpeg.exe'), buildScriptSha256=media.sha256(self.recipe))
        self.save()

    def save(self):
        (self.runtime / 'BUILD-INFO.json').write_text(json.dumps(self.record))

    def test_accepts_matching_artifact(self):
        self.assertEqual(media.verify(self.runtime, self.recipe), self.record)

    def test_windows_git_checkout_preserves_build_recipe_hash(self):
        repository = Path(__file__).resolve().parents[2]
        checkout = self.root / 'checkout'
        subprocess.run(['git', 'init', '-q', str(checkout)], check=True, capture_output=True)
        def git(*args):
            subprocess.run(['git', '-C', str(checkout), *args], check=True, capture_output=True)
        git('config', 'core.autocrlf', 'true')
        (checkout / '.gitattributes').write_bytes((repository / '.gitattributes').read_bytes())
        original = repository / 'scripts/prepare-windows-media.sh'
        copied = checkout / original.name
        copied.write_bytes(original.read_bytes())
        git('add', '.')
        copied.unlink()
        git('checkout-index', '-f', '--', copied.name)
        self.assertEqual(media.sha256(copied), media.sha256(original))

    def test_rejects_changed_binary_or_recipe(self):
        for path in [self.runtime / 'bin/ffmpeg.exe', self.recipe]:
            with self.subTest(path=path.name):
                before = path.read_bytes()
                path.write_bytes(before + b' changed')
                with self.assertRaises(ValueError): media.verify(self.runtime, self.recipe)
                path.write_bytes(before)

    def test_rejects_missing_license_or_mixed_old_dll(self):
        path = self.runtime / 'libwebp-PATENTS.txt'
        content = path.read_bytes(); path.unlink()
        with self.assertRaises(ValueError): media.verify(self.runtime, self.recipe)
        path.write_bytes(content)
        (self.runtime / 'bin/avcodec-62.dll').write_bytes(b'old runtime')
        with self.assertRaises(ValueError): media.verify(self.runtime, self.recipe)

    def test_rejects_wrong_sources_and_external_dlls(self):
        self.record['sources'][0]['sha256'] = '0' * 64
        self.save()
        with self.assertRaises(ValueError): media.verify(self.runtime, self.recipe)
        self.record['sources'] = [dict(name=k, sha256=v) for k, v in media.SOURCES.items()]
        self.record['systemImports'].append('unknown.dll'); self.save()
        with self.assertRaises(ValueError): media.verify(self.runtime, self.recipe)

    def test_rejects_other_platform_or_version(self):
        for key, value in [('target', 'osx-arm64'), ('ffmpeg', '9.0'), ('license', 'GPL-3.0')]:
            with self.subTest(key=key):
                old = self.record[key]; self.record[key] = value; self.save()
                with self.assertRaises(ValueError): media.verify(self.runtime, self.recipe)
                self.record[key] = old
