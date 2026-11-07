#!/usr/bin/env python3
"""Collect pinned upstream source/build materials without executing upstream code."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import gzip
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import tarfile
import tempfile
from urllib.parse import urlparse
import xml.etree.ElementTree as ET

ARCHIVE_NAME = 'Tbox_third-party-sources.tar.gz'


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def load_manifest(path):
    manifest = json.loads(path.read_text(encoding='utf-8'))
    if manifest.get('schemaVersion') != 1 or not manifest.get('archives') or not manifest.get('git'):
        raise ValueError('Incomplete source manifest.')
    seen = set()
    for item in manifest['archives'] + manifest['git']:
        name = item['path']; relative = PurePosixPath(name)
        if relative.is_absolute() or '..' in relative.parts or '\\' in name or name != relative.as_posix() or name in seen:
            raise ValueError('Unsafe or duplicate source path.')
        seen.add(name)
        if not re.fullmatch('[a-f0-9]{64}', item['sha256']):
            raise ValueError('Source archive must have a pinned SHA-256.')
        urls = item.get('urls', [item.get('url')])
        if not urls or any(not u or urlparse(u).scheme != 'https' or not urlparse(u).netloc or urlparse(u).username or urlparse(u).password for u in urls):
            raise ValueError('Only public HTTPS sources are accepted.')
        if 'commit' in item and (not re.fullmatch('[a-f0-9]{40}', item['commit']) or not re.fullmatch('[a-zA-Z0-9_.-]+/', item['prefix'])):
            raise ValueError('Git sources require a full commit and safe archive prefix.')
    return manifest


def matching(path, item):
    return path.is_file() and not path.is_symlink() and sha256(path) == item['sha256']


def validate_versions(root, manifest):
    versions = manifest['components']
    project = ET.parse(root / 'src/Tbox.Desktop/Tbox.Desktop.csproj').getroot()
    packages = {p.get('Include'): p.get('Version') for p in project.findall('.//PackageReference')}
    for package, component in [('LibVLCSharp.Avalonia', 'LibVLCSharp'), ('VideoLAN.LibVLC.Windows', 'VLC')]:
        if packages.get(package) != versions.get(component):
            raise ValueError('Source version differs from the packaged dependency: ' + package)
    archives = {item['path']: item for item in manifest['archives']}
    for component, filename, scripts in [
        ('FFmpeg', 'ffmpeg-{version}.tar.xz', ['prepare-windows-media.sh', 'prepare-macos-media.sh']),
        ('libwebp', 'libwebp-{version}.tar.gz', ['prepare-windows-media.sh', 'prepare-macos-media.sh']),
        ('VLC', 'vlc-{version}.tar.xz', ['prepare-macos-media.sh'])
    ]:
        name = filename.format(version=versions[component])
        item = archives.get('upstream/' + name)
        for script in scripts:
            recipe = (root / 'scripts' / script).read_text(encoding='utf-8')
            if not item or name not in recipe or item['sha256'] not in recipe:
                raise ValueError('Source version/hash differs from the build recipe: ' + script)


def fetch_archive(item, cache):
    target = cache / item['path']
    if matching(target, item):
        return target
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=target.parent) as temporary:
        candidate = Path(temporary) / 'download'
        for url in item['urls']:
            result = subprocess.run(['curl', '-fLsS', '--retry', '1', '--http1.1', '--connect-timeout', '20', '--max-time', '600', url, '-o', str(candidate)], capture_output=True)
            if result.returncode == 0 and matching(candidate, item):
                candidate.replace(target)
                return target
    raise ValueError('Unable to retrieve matching source: ' + item['path'])


def fetch_git(item, cache):
    target = cache / item['path']
    if matching(target, item):
        return target
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=target.parent) as temporary:
        working = Path(temporary)
        def git(*args):
            return subprocess.run(['git', '-C', str(working), *args], check=True, capture_output=True, text=True, timeout=300).stdout.strip()
        git('init', '-q')
        git('fetch', '--depth=1', item['url'], item['commit'])
        if git('rev-parse', 'FETCH_HEAD') != item['commit']:
            raise ValueError('Fetched another Git revision.')
        if any(line.startswith('160000 ') for line in git('ls-tree', '-r', 'FETCH_HEAD').splitlines()):
            raise ValueError('Submodules require explicit source entries.')
        raw = working / 'source.tar'
        git('archive', '--format=tar', '--prefix=' + item['prefix'], '--output=' + str(raw), 'FETCH_HEAD')
        candidate = working / 'source.tar.gz'
        with raw.open('rb') as source, candidate.open('wb') as output:
            with gzip.GzipFile(filename='', fileobj=output, mode='wb', mtime=0) as compressed:
                shutil.copyfileobj(source, compressed)
        if not matching(candidate, item):
            raise ValueError('Git source archive differs from the reviewed snapshot: ' + item['path'])
        candidate.replace(target)
    return target


def build_bundle(root, manifest_path, cache, output):
    manifest = load_manifest(manifest_path)
    items = manifest['archives'] + manifest['git']
    for item in items:
        if not matching(cache / item['path'], item):
            raise ValueError('Missing or modified source: ' + item['path'])
    output.mkdir(parents=True, exist_ok=True)
    archive = output / ARCHIVE_NAME
    record_path = output / 'third-party-build.json'
    if archive.exists() or record_path.exists():
        raise ValueError('Do not overwrite existing source release materials.')
    files = {item['path']: cache / item['path'] for item in items}
    files['source-manifest.json'] = manifest_path
    files['README.md'] = root / 'docs/third-party-sources.md'
    for name in ['prepare-windows-media.sh', 'prepare-macos-media.sh']:
        files['scripts/' + name] = root / 'scripts' / name
    for path in (root / 'src/Tbox.Desktop/ThirdParty').glob('*.txt'):
        files['licenses/' + path.name] = path
    temporary = archive.with_suffix('.partial')
    try:
        with temporary.open('wb') as output_stream:
            with gzip.GzipFile(filename='', fileobj=output_stream, mode='wb', compresslevel=1, mtime=0) as compressed:
                with tarfile.open(fileobj=compressed, mode='w|') as bundle:
                    for name, path in sorted(files.items()):
                        if not path.is_file() or path.is_symlink():
                            raise ValueError('Source material must be a regular file.')
                        info = bundle.gettarinfo(str(path), 'tbox-third-party-source/' + name)
                        info.uid = info.gid = info.mtime = 0
                        info.uname = info.gname = ''
                        info.mode = 0o644
                        with path.open('rb') as content:
                            bundle.addfile(info, content)
        temporary.replace(archive)
    finally:
        temporary.unlink(missing_ok=True)
    record = dict(name=ARCHIVE_NAME, size=archive.stat().st_size, sha256=sha256(archive),
                  manifestSha256=sha256(manifest_path), components=manifest['components'], inputCount=len(items))
    record_path.write_text(json.dumps(record, indent=2) + '\n', encoding='utf-8')
    return record


def main():
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser()
    parser.add_argument('--cache', type=Path, default=root / 'TEMP/third-party-source-cache')
    parser.add_argument('--output', type=Path, default=root / 'TEMP/third-party-release-assets')
    parser.add_argument('--offline', action='store_true')
    args = parser.parse_args()
    manifest_path = root / 'assets/third-party/media-sources.json'
    manifest = load_manifest(manifest_path)
    validate_versions(root, manifest)
    if not args.offline:
        def fetch(item):
            return fetch_git(item, args.cache) if 'commit' in item else fetch_archive(item, args.cache)
        with ThreadPoolExecutor(max_workers=4) as workers:
            list(workers.map(fetch, manifest['archives'] + manifest['git']))
    print(json.dumps(build_bundle(root, manifest_path, args.cache, args.output)))


if __name__ == '__main__':
    main()
