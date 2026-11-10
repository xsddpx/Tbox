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
import time
import xml.etree.ElementTree as ET

REPOSITORY = 'xsddpx/Tbox'
NS = {'m': 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'}
SUPPORT_FILES = ('Tbox-Development.cer', 'Tbox_third-party-sources.tar.gz')


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


def collect(directory, tag, source_manifest):
    version = version_from_tag(tag)
    expected = {f'Tbox_{version}_osx-arm64.zip', f'Tbox_{version}.0_win-x64.msix', *SUPPORT_FILES}
    records = {'osx-arm64-build.json', 'win-x64-build.json', 'third-party-build.json'}
    if {p.name for p in directory.iterdir()} != expected | records:
        raise ValueError('Expected the two installers, public certificate and corresponding source materials.')
    for path in directory.iterdir():
        if path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size <= 2 * 1024**3:
            raise ValueError('Missing, empty, linked or oversized artifact.')
    assets = {name: {'name': name, 'size': (directory / name).stat().st_size,
                     'sha256': sha256(directory / name)} for name in sorted(expected)}
    source_record = json.loads((directory / 'third-party-build.json').read_text(encoding='utf-8'))
    source = assets['Tbox_third-party-sources.tar.gz']
    if (any(source_record.get(key) != value for key, value in source.items())
            or source_record.get('manifestSha256') != sha256(source_manifest)):
        raise ValueError('Source material differs from the reviewed manifest or build audit.')
    for name in sorted(records - {'third-party-build.json'}):
        record = json.loads((directory / name).read_text(encoding='utf-8'))
        platform = name.removesuffix('-build.json')
        expected_name = f'Tbox_{version}_osx-arm64.zip' if platform == 'osx-arm64' else f'Tbox_{version}.0_win-x64.msix'
        asset = assets[expected_name]
        if any(record.get(key) != value for key, value in asset.items()):
            raise ValueError('Package differs from the signed build audit.')
        if datetime.fromisoformat(record['packagedAt']).tzinfo is None:
            raise ValueError('Build timestamp must include timezone.')
        asset['packagedAt'] = record['packagedAt']
    return {'version': version, 'assets': assets}


def same_asset(actual, expected):
    return (actual.get('state') == 'uploaded' and actual.get('size') == expected['size']
            and actual.get('digest') == 'sha256:' + expected['sha256'])


def validate_assets(release, expected, *, allow_missing=False):
    assets = release.get('assets', [])
    actual = {asset['name']: asset for asset in assets}
    missing = expected.keys() - actual.keys()
    if len(actual) != len(assets) or actual.keys() - expected.keys() or (missing and not allow_missing):
        raise ValueError('Release does not contain the complete asset set.')
    for name, asset in actual.items():
        if not same_asset(asset, expected[name]):
            raise ValueError('Uploaded asset is incomplete or its SHA-256 differs: ' + name)
    return sorted(missing)


def command(*args, env=None, input=None):
    return subprocess.run(args, input=input, text=True, encoding='utf-8', check=True, stdout=subprocess.PIPE, env=env).stdout.strip()


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
    # Source and installers share one public repository; reject missing visibility data.
    if api(f'repos/{REPOSITORY}').get('private') is not False:
        raise ValueError('Release repository must be public.')


def releases():
    pages = json.loads(command('gh', 'api', '--paginate', '--slurp', f'repos/{REPOSITORY}/releases?per_page=100'))
    return {item['tag_name']: item for page in pages for item in page}


def source_commit(tag):
    version_from_tag(tag)
    commit = command('git', 'rev-parse', 'HEAD')
    if command('git', 'rev-parse', '--verify', f'refs/tags/{tag}^{{commit}}') != commit:
        raise ValueError('Check out the release tag itself before building or publishing.')
    return commit


def audit_rows(manifest):
    version = manifest['version']
    rows = []
    for package_version, platform, name in (
        (version, 'macOS osx-arm64', f'Tbox_{version}_osx-arm64.zip'),
        (version + '.0', 'Windows win-x64', f'Tbox_{version}.0_win-x64.msix'),
    ):
        asset = manifest['assets'][name]
        stamp = datetime.fromisoformat(asset['packagedAt']).astimezone(timezone(timedelta(hours=8)))
        rows.append(f"| {package_version} | {platform} | {stamp:%Y-%m-%d %H:%M:%S %z} | `{asset['sha256']}` |")
    return rows


def has_audit(history, tag, rows):
    sections = re.findall(rf'^## 自动发布 {re.escape(tag)}[ \t]*\n(.*?)(?=^## |\Z)', history, re.M | re.S)
    if not sections:
        return False
    if len(sections) != 1 or sorted(re.findall(r'^\|\s*\d.*$', sections[0], re.M)) != sorted(rows):
        raise ValueError(f'Existing release history for {tag} differs from the signed packages.')
    return True


def record_history(root, tag, manifest):
    branch = 'codex/release-record-' + tag
    ref = f'refs/heads/{branch}'
    rows = audit_rows(manifest)
    command('git', 'fetch', 'origin', 'refs/heads/main:refs/remotes/origin/main')
    before = command('git', 'show', 'origin/main:docs/release-history.md')
    if has_audit(before, tag, rows):
        return

    def remote_audit_exists():
        if not command('git', 'ls-remote', '--refs', 'origin', ref):
            return False
        command('git', 'fetch', 'origin', ref)
        if not has_audit(command('git', 'show', 'FETCH_HEAD:docs/release-history.md'), tag, rows):
            raise ValueError(f'Existing branch {branch} does not contain the release audit.')
        return True

    if remote_audit_exists():
        return
    command('git', 'switch', '-c', branch, 'origin/main')
    section = [f'## 自动发布 {tag}', '', '两平台使用持久证书签名。', '',
               '| 版本 | 平台 | 打包时间（Asia/Shanghai） | SHA-256 |', '| --- | --- | --- | --- |', *rows]
    (root / 'docs/release-history.md').write_text(before.rstrip() + '\n\n' + '\n'.join(section) + '\n', encoding='utf-8')
    command('git', 'add', 'docs/release-history.md')
    command('git', '-c', 'user.name=github-actions[bot]', '-c',
            'user.email=41898282+github-actions[bot]@users.noreply.github.com',
            'commit', '-m', '[codex] docs: 记录自动发布 ' + tag)
    for attempt in range(3):
        try:
            command('git', 'push', 'origin', f'HEAD:{ref}')
            break
        except subprocess.CalledProcessError:
            # A failed response can follow a successful write. Reuse only a matching remote record.
            try:
                if remote_audit_exists():
                    break
            except subprocess.CalledProcessError:
                if attempt == 2:
                    raise
            if attempt == 2:
                raise
            time.sleep(2 ** (attempt + 1))
    # Some personal repositories disallow Actions-created PRs. The committed audit
    # remains durable even then; do not weaken repository settings automatically.
    try:
        pr = api(f'repos/{REPOSITORY}/pulls', 'POST', {
            'title': '[codex] 记录自动发布 ' + tag, 'head': branch, 'base': 'main',
            'body': f'记录 {tag} 两平台安装包的时间与 SHA-256。产物已通过签名、运行及上传完整性检查。仅更新发布历史。'
        })
        print('Release history PR: ' + pr['html_url'])
    except subprocess.CalledProcessError:
        print(f'::warning::发布历史已提交到 {branch}；自动创建 PR 失败。')


def download_url(tag, name):
    return f'https://github.com/{REPOSITORY}/releases/download/{tag}/{name}'


def check_release(release, identity, *, draft_name=None):
    if any(release.get(key) != value for key, value in identity.items()) or type(release.get('draft')) is not bool:
        raise ValueError(f"Existing release {identity['tag_name']} does not match this transaction.")
    if release['draft'] and draft_name is not None and release.get('name') != draft_name:
        raise ValueError(f"Draft {identity['tag_name']} does not match this transaction.")


def stage_release(directory, existing, identity, assets, *, name, commit):
    release = existing
    if release is None:
        release = api(f'repos/{REPOSITORY}/releases', 'POST', {
            **identity, 'name': name, 'target_commitish': commit, 'draft': True, 'make_latest': 'false'
        })
        if release.get('draft') is not True:
            raise ValueError('Expected a draft before uploading.')
    check_release(release, identity, draft_name=name)
    if not release['draft']:
        validate_assets(release, assets)
        return release

    missing = validate_assets(release, assets, allow_missing=True)
    if missing:
        command('gh', 'release', 'upload', identity['tag_name'], '--repo', REPOSITORY,
                *(str(directory / name) for name in missing))
    release = api(f"repos/{REPOSITORY}/releases/{release['id']}")
    check_release(release, identity, draft_name=name)
    validate_assets(release, assets)
    return release


def finish_release(release, identity, assets, *, name):
    path = f"repos/{REPOSITORY}/releases/{release['id']}"
    current = api(path)
    check_release(current, identity, draft_name=release.get('name'))
    validate_assets(current, assets)
    if not current['draft']:
        return current
    published = api(path, 'PATCH', {
        'name': name, 'draft': False, 'prerelease': identity['prerelease'],
        'make_latest': 'false' if identity['prerelease'] else 'true'
    })
    check_release(published, identity)
    if published['draft'] or published.get('name') != name:
        raise ValueError('Publication was not confirmed.')
    validate_assets(published, assets)
    return published


def support_identity(existing, assets):
    # Reuse the already published files when their bytes match the current build.
    legacy = existing.get('v1.1.50', {})
    previous = {asset['name']: asset for asset in legacy.get('assets', [])}
    if legacy.get('draft') is False and all(same_asset(previous.get(name, {}), asset) for name, asset in assets.items()):
        return {'tag_name': 'v1.1.50'}, None

    # A new archive is needed only when the certificate or corresponding sources change.
    fingerprint = hashlib.sha256(''.join(asset['sha256'] for asset in assets.values()).encode('ascii')).hexdigest()
    tag = 'support-' + fingerprint
    body = ('Tbox 的 Windows 公钥证书和第三方对应源码。各应用版本的发布正文会链接所用文件。\n\n'
            f'<!-- tbox-release-support:{fingerprint} -->')
    return {'tag_name': tag, 'prerelease': True, 'body': body}, existing.get(tag)


def release_notes(root, tag, installers, support_tag):
    version = version_from_tag(tag)
    windows = f'Tbox_{version}.0_win-x64.msix'
    mac = f'Tbox_{version}_osx-arm64.zip'
    certificate, sources = (download_url(support_tag, name) for name in SUPPORT_FILES)
    checksums = '\n'.join(f"| `{name}` | `{installers[name]['sha256']}` |" for name in (windows, mac))
    notes = (root / 'docs/releases' / (tag + '.md')).read_text(encoding='utf-8').strip()
    return f"""## 下载并安装

- [Windows 11 x64]({download_url(tag, windows)})
- [Apple Silicon Mac]({download_url(tag, mac)})

[安装与升级说明](https://github.com/{REPOSITORY}/blob/{tag}/docs/releases/INSTALL.md) · [Windows 首次安装证书]({certificate}) · [第三方对应源码]({sources})

GitHub 自动生成的 `Source code (zip)` 和 `Source code (tar.gz)` 用于查看项目源码。安装请使用上面的两个链接。

<details>
<summary>安装包 SHA-256</summary>

| 文件 | SHA-256 |
| --- | --- |
{checksums}

</details>

{notes}
"""


def publish(root, directory, tag, prerelease, *, audit=record_history):
    commit = source_commit(tag)
    run_id = os.environ.get('GITHUB_RUN_ID', '')
    if not re.fullmatch(r'[0-9]+', run_id):
        raise ValueError('Publish through the release workflow so the draft can be tied to its run.')
    manifest = collect(directory, tag, root / 'assets/third-party/media-sources.json')
    installers = {name: asset for name, asset in manifest['assets'].items() if name not in SUPPORT_FILES}
    support = {name: manifest['assets'][name] for name in SUPPORT_FILES}
    existing = releases()
    support_fields, support_release = support_identity(existing, support)
    support_tag = support_fields['tag_name']
    name = 'Tbox ' + tag
    support_name = 'Tbox 第三方源码与 Windows 证书'
    identity = {'tag_name': tag, 'target_commitish': commit, 'prerelease': prerelease,
                'body': release_notes(root, tag, installers, support_tag)}
    app = stage_release(directory, existing.get(tag), identity, installers,
                        name=f'{name} · Actions {run_id}', commit=commit)
    if support_tag != 'v1.1.50':
        support_release = stage_release(directory, support_release, support_fields, support,
                                        name=support_name, commit=commit)

    audit(root, tag, manifest)
    if support_release is not None:
        finish_release(support_release, support_fields, support, name=support_name)
    published = finish_release(app, identity, installers, name=name)
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
            source_commit(args.tag)
            if args.tag in releases():
                raise ValueError('Release already exists. Rerun failed jobs to reuse the signed builds and draft.')
    elif args.action == 'build-record':
        if not args.platform:
            raise ValueError('Choose a build platform.')
        build_record(root, args.directory, args.tag, args.platform)
    elif args.action == 'collect':
        print(json.dumps(collect(args.directory, args.tag, root / 'assets/third-party/media-sources.json'), indent=2))
    else:
        publish(root, args.directory.resolve(), args.tag, args.prerelease)


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)
