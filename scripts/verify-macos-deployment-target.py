#!/usr/bin/env python3
"""Reject native runtime binaries that require a newer Mac than we support."""
import argparse
from pathlib import Path
import re
import subprocess

MACHO_MAGIC = {bytes.fromhex(v) for v in ('feedface', 'cefaedfe', 'feedfacf', 'cffaedfe', 'cafebabe', 'bebafeca', 'cafebabf', 'bfbafeca')}


def parse_version(value):
    if not re.fullmatch(r'\d+\.\d+(?:\.\d+)?', value):
        raise ValueError('Invalid macOS version: ' + value)
    return tuple(int(n) for n in (value.split('.') + ['0'])[:3])


def minimum_versions(output, deployment_target):
    command = None
    versions = []
    for line in output.splitlines():
        parts = line.strip().split()
        if len(parts) != 2:
            continue
        key, value = parts
        if key == 'cmd':
            command = value
        if key == 'platform' and command == 'LC_BUILD_VERSION' and value != 'MACOS':
            raise ValueError('Expected a macOS binary.')
        if (command == 'LC_BUILD_VERSION' and key == 'minos') or (command == 'LC_VERSION_MIN_MACOSX' and key == 'version'):
            versions.append(parse_version(value))
    if not versions:
        raise ValueError('Native binary has no minimum macOS metadata.')
    if any(version > deployment_target for version in versions):
        target = '.'.join(map(str, deployment_target))
        raise ValueError(f'Native binary requires macOS newer than {target}: {versions}')
    return versions


def verify(paths, deployment_target):
    target_version = parse_version(deployment_target)
    count = 0
    for root in paths:
        if not root.exists():
            raise ValueError('Missing native runtime: ' + str(root))
        for path in ([root] if root.is_file() else root.rglob('*')):
            if not path.is_file() or path.is_symlink():
                continue
            with path.open('rb') as stream:
                if stream.read(4) not in MACHO_MAGIC:
                    continue
            result = subprocess.run(['/usr/bin/vtool', '-show-build', str(path)], check=True, capture_output=True, text=True)
            try:
                minimum_versions(result.stdout, target_version)
            except ValueError as error:
                raise ValueError(str(path) + ': ' + str(error)) from error
            count += 1
    if not count:
        raise ValueError('No native binaries were checked.')
    print(f'Verified minimum macOS <= {deployment_target} for {count} native binaries.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--deployment-target', required=True)
    parser.add_argument('paths', type=Path, nargs='+')
    args = parser.parse_args()
    verify(args.paths, args.deployment_target)
