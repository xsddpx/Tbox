#!/usr/bin/env python3
"""Release policy and transaction. No network calls at import time; stdlib only."""
import argparse
from datetime import datetime, timezone, timedelta
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

PUBLIC_REPO = 'xsddpx/Tbox-releases'
SOURCE_REPO = 'xsddpx/Tbox'
NS = {'m': 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'}


def version_from_tag(tag):
    if not re.fullmatch(r'v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)', tag):
        raise ValueError('Use a tag such as v1.2.0 (no suffix or shell characters).')
    parts = [int(n) for n in tag[1:].split('.')]
    if not 1 <= parts[0] <= 65535 or any(n > 65535 for n in parts):
        raise ValueError('Version is outside MSIX limits.')
    return tag[1:]


def preflight(root, tag):
    version = version_from_tag(tag)
    project = ET.parse(root / 'src/Tbox.Desktop/Tbox.Desktop.csproj').getroot()
    for name, expected in [('Version', version), ('AssemblyVersion', version + '.0'), ('FileVersion', version + '.0')]:
        if project.findtext('.//' + name) != expected:
            raise ValueError(f'Tag differs from {name}.')
    with (root / 'src/Tbox.Desktop/Platform/macos/Info.plist').open('rb') as stream:
        info = plistlib.load(stream)
    if (info.get('CFBundleShortVersionString'), info.get('CFBundleVersion'), info.get('CFBundleIdentifier')) != (version, version + '.0', 'com.xsddpx.tbox'):
        raise ValueError('Mac bundle identity/version differs from release.')
    manifest = ET.parse(root / 'src/Tbox.Desktop/Platform/windows/Package.appxmanifest').getroot()
    identity = manifest.find('m:Identity', NS)
    if identity is None or identity.attrib != {'Name': 'Tbox', 'Publisher': 'CN=Tbox Development', 'Version': version + '.0', 'ProcessorArchitecture': 'x64'}:
        raise ValueError('Windows package identity/version differs from release.')
    history = (root / 'docs/release-history.md').read_text(encoding='utf-8')
    if re.search(r'^\|\s*' + re.escape(version) + r'(?:\.0)?\s*\|', history, re.M):
        raise ValueError('Version already packaged. Increment all version files before releasing.')
    notes = root / 'docs/releases' / (tag + '.md')
    if not notes.is_file() or notes.is_symlink() or not notes.read_text(encoding='utf-8').strip():
        raise ValueError('Commit public release notes to docs/releases/<tag>.md before tagging.')
    return version


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def build_record(root, directory, tag, platform):
    version = version_from_tag(tag)
    package_version = version if platform == 'osx-arm64' else version + '.0'
    suffix = '.zip' if platform == 'osx-arm64' else '.msix'
    path = directory / f'Tbox_{package_version}_{platform}{suffix}'
    digest = sha256(path)
    rows = [line for line in (root / 'docs/release-history.md').read_text(encoding='utf-8').splitlines()
            if re.match(r'^\|\s*' + re.escape(package_version) + r'\s*\|', line)
            and digest.lower() in line.lower()]
    if len(rows) != 1:
        raise ValueError('Exactly one matching package audit is required.')
    stamp = re.search(r'\d{4}-\d\d-\d\d \d\d:\d\d:\d\d [+-]\d\d:?\d\d', rows[0])
    if not stamp:
        raise ValueError('Package audit has no timestamp.')
    record = {'name': path.name, 'size': path.stat().st_size, 'sha256': digest,
              'packagedAt': datetime.strptime(stamp[0], '%Y-%m-%d %H:%M:%S %z').isoformat()}
    (directory / f'{platform}-build.json').write_text(json.dumps(record) + '\n', encoding='utf-8')


def collect(directory, tag, install_notes, source_manifest):
    version = version_from_tag(tag)
    expected = {f'Tbox_{version}_osx-arm64.zip', f'Tbox_{version}.0_win-x64.msix', 'Tbox-Development.cer', 'Tbox_third-party-sources.tar.gz'}
    records = {'osx-arm64-build.json', 'win-x64-build.json', 'third-party-build.json'}
    if {p.name for p in directory.iterdir()} != expected | records:
        raise ValueError('Expected the two installers, public certificate and corresponding source materials.')
    for path in directory.iterdir():
        if path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size <= 2 * 1024**3:
            raise ValueError('Missing, empty, linked or oversized artifact.')
    times = {}
    source_record = json.loads((directory / 'third-party-build.json').read_text(encoding='utf-8'))
    source_name = 'Tbox_third-party-sources.tar.gz'
    if source_record.get('name') != source_name or source_record.get('sha256') != sha256(directory / source_name) or source_record.get('size') != (directory / source_name).stat().st_size or source_record.get('manifestSha256') != sha256(source_manifest):
        raise ValueError('Source material differs from the reviewed manifest or build audit.')
    for name in records:
        if name == 'third-party-build.json':
            continue
        record = json.loads((directory / name).read_text(encoding='utf-8'))
        platform = name.removesuffix('-build.json')
        expected_name = f'Tbox_{version}_osx-arm64.zip' if platform == 'osx-arm64' else f'Tbox_{version}.0_win-x64.msix'
        if record['name'] != expected_name or record['sha256'] != sha256(directory / expected_name) or record['size'] != (directory / expected_name).stat().st_size:
            raise ValueError('Package differs from the signed build audit.')
        if datetime.fromisoformat(record['packagedAt']).tzinfo is None:
            raise ValueError('Build timestamp must include timezone.')
        times[expected_name] = record['packagedAt']
    for name in records:
        (directory / name).unlink()
    (directory / 'INSTALL.md').write_bytes(install_notes.read_bytes())
    assets = [{'name': p.name, 'size': p.stat().st_size, 'sha256': sha256(p)} for p in sorted(directory.iterdir())]
    for asset in assets:
        if asset['name'] in times:
            asset['packagedAt'] = times[asset['name']]
    manifest = {'version': version, 'createdAt': datetime.now(timezone.utc).isoformat(), 'assets': assets}
    (directory / 'release-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    (directory / 'SHA256SUMS').write_text(''.join(f"{a['sha256']}  {a['name']}\n" for a in assets), encoding='utf-8')
    return manifest


def validate_assets(release, paths):
    expected = {p.name: p for p in paths}
    actual = release.get('assets', [])
    if len(actual) != len(expected) or {a['name'] for a in actual} != expected.keys():
        raise ValueError('Release does not contain the complete asset set.')
    for asset in actual:
        path = expected[asset['name']]
        if asset.get('state') != 'uploaded' or asset.get('size') != path.stat().st_size or asset.get('digest') != 'sha256:' + sha256(path):
            raise ValueError('Uploaded asset is incomplete or its SHA-256 differs: ' + path.name)


def command(*args, env=None, input=None):
    return subprocess.run(args, input=input, text=True, check=True, stdout=subprocess.PIPE, env=env).stdout.strip()


def api(path, method='GET', body=None, token=None):
    env = os.environ.copy()
    if token:
        env['GH_TOKEN'] = token
    args = ['gh', 'api', '--method', method, path]
    if body is not None:
        args += ['--input', '-']
    output = command(*args, env=env, input=json.dumps(body) if body is not None else None)
    return json.loads(output) if output else None


def remote_preflight():
    # Separate checks prevent a typo from exposing source history or publishing elsewhere.
    if api(f'repos/{PUBLIC_REPO}').get('private') is not False:
        raise ValueError('Download repository must be public.')
    if api(f'repos/{SOURCE_REPO}', token=os.environ['TBOX_SOURCE_TOKEN']).get('private') is not True:
        raise ValueError('Source repository visibility changed; review before publishing.')


def assert_unused_tag(tag):
    pages = json.loads(command('gh', 'api', '--paginate', '--slurp', f'repos/{PUBLIC_REPO}/releases?per_page=100'))
    if any(r.get('tag_name') == tag for page in pages for r in page):
        raise ValueError('Public release or draft already exists. Do not overwrite; inspect the failed run or use a new version.')


def record_history(root, tag, manifest):
    # Keep a committed audit before publication, without rewriting the immutable release tag.
    branch = 'codex/release-record-' + tag
    command('git', 'fetch', 'origin', 'main')
    command('git', 'switch', '-c', branch, 'origin/main')
    history = root / 'docs/release-history.md'
    marker = f'## 自动发布 {tag}'
    before = history.read_text(encoding='utf-8')
    if marker in before:
        raise ValueError('Release audit already exists; refuse to overwrite.')
    lines = [f'\n\n{marker}\n', '持久证书签名；CI 安装/运行检查不代替用户设备的完整功能验收。\n',
             '| 版本 | 平台 | 打包时间（Asia/Shanghai） | SHA-256 |', '| --- | --- | --- | --- |']
    for a in manifest['assets']:
        stamp = datetime.fromisoformat(a.get('packagedAt', manifest['createdAt'])).astimezone(timezone(timedelta(hours=8))).strftime('%Y-%m-%d %H:%M:%S %z')
        if a['name'] == f"Tbox_{manifest['version']}_osx-arm64.zip":
            lines.append(f"| {manifest['version']} | macOS osx-arm64 | {stamp} | `{a['sha256']}` |")
        if a['name'] == f"Tbox_{manifest['version']}.0_win-x64.msix":
            lines.append(f"| {manifest['version']}.0 | Windows win-x64 | {stamp} | `{a['sha256']}` |")
    history.write_text(before.rstrip() + '\n'.join(lines) + '\n', encoding='utf-8')
    command('git', 'config', 'user.name', 'github-actions[bot]')
    command('git', 'config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com')
    command('git', 'add', 'docs/release-history.md')
    command('git', 'commit', '-m', '[codex] docs: 记录自动发布 ' + tag)
    command('git', 'push', 'origin', f'HEAD:refs/heads/{branch}')
    # Some personal repositories disallow Actions-created PRs. The committed audit
    # remains durable even then; do not weaken repository settings automatically.
    try:
        pr = api(f'repos/{SOURCE_REPO}/pulls', 'POST', {
            'title': '[codex] 记录自动发布 ' + tag, 'head': branch, 'base': 'main',
            'body': f'记录 {tag} 两平台安装包的时间与 SHA-256。产物已通过签名、运行及上传完整性检查。仅更新发布历史。'
        }, token=os.environ['TBOX_SOURCE_TOKEN'])
        print('Release history PR: ' + pr['html_url'])
    except subprocess.CalledProcessError:
        print(f'::warning::发布历史已提交到 {branch}；仓库禁止 Actions 创建 PR，请从该分支合并记录。')


def publish(root, directory, tag, prerelease, *, audit=record_history):
    version_from_tag(tag)
    assert_unused_tag(tag)
    paths = sorted(directory.iterdir())
    manifest = json.loads((directory / 'release-manifest.json').read_text(encoding='utf-8'))
    notes = (root / 'docs/releases' / (tag + '.md')).read_text(encoding='utf-8')
    notes += '\n\n安装与权限说明见附件 INSTALL.md；校验值见 SHA256SUMS；第三方对应源码与构建材料见 Tbox_third-party-sources.tar.gz。\n'
    # GitHub rejects a duplicate tag. Never clobber assets, edit an old release,
    # or automatically adopt a leftover draft from an earlier failed attempt.
    release = api(f'repos/{PUBLIC_REPO}/releases', 'POST', {
        'tag_name': tag, 'name': 'Tbox ' + tag, 'body': notes, 'draft': True,
        'prerelease': prerelease, 'make_latest': 'false'
    })
    if release.get('draft') is not True:
        raise ValueError('Expected a draft before uploading.')
    command('gh', 'release', 'upload', tag, '--repo', PUBLIC_REPO, *map(str, paths))
    release = api(f"repos/{PUBLIC_REPO}/releases/{release['id']}")
    validate_assets(release, paths)
    audit(root, tag, manifest)
    published = api(f"repos/{PUBLIC_REPO}/releases/{release['id']}", 'PATCH', {
        'draft': False, 'prerelease': prerelease, 'make_latest': 'false' if prerelease else 'true'
    })
    if published.get('draft') is not False or published.get('prerelease') != prerelease:
        raise ValueError('Publication was not confirmed.')
    validate_assets(published, paths)
    print('Published: ' + published['html_url'])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['preflight', 'build-record', 'collect', 'publish'])
    parser.add_argument('--tag', required=True)
    parser.add_argument('--directory', type=Path, default=Path('TEMP/release-assets'))
    parser.add_argument('--prerelease', action='store_true')
    parser.add_argument('--platform', choices=['osx-arm64', 'win-x64'])
    args = parser.parse_args()
    root = Path.cwd()
    if args.action == 'preflight':
        print(preflight(root, args.tag))
        if os.environ.get('GITHUB_ACTIONS') == 'true':
            remote_preflight()
            assert_unused_tag(args.tag)
    elif args.action == 'build-record':
        if not args.platform:
            raise ValueError('Choose a build platform.')
        build_record(root, args.directory, args.tag, args.platform)
    elif args.action == 'collect':
        collect(args.directory, args.tag, root / 'docs/releases/INSTALL.md', root / 'assets/third-party/media-sources.json')
    else:
        publish(root, args.directory.resolve(), args.tag, args.prerelease)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
