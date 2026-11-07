#!/usr/bin/env python3
"""Reject native runtime binaries that require a newer Mac than we support."""
from pathlib import Path
import re
import subprocess
import sys

MINIMUM_MACOS = (14, 0, 0)
MACHO_MAGIC = {bytes.fromhex(v) for v in ('feedface', 'cefaedfe', 'feedfacf', 'cffaedfe', 'cafebabe', 'bebafeca', 'cafebabf', 'bfbafeca')}


def minimum_versions(output):
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
            if not re.fullmatch(r'\d+\.\d+(?:\.\d+)?', value):
                raise ValueError('Invalid minimum OS version.')
            versions.append(tuple(int(n) for n in (value.split('.') + ['0'])[:3]))
    if not versions:
        raise ValueError('Native binary has no minimum macOS metadata.')
    if any(version > MINIMUM_MACOS for version in versions):
        raise ValueError('Native binary requires macOS newer than 14.0: ' + str(versions))
    return versions


def verify(paths):
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
                minimum_versions(result.stdout)
            except ValueError as error:
                raise ValueError(str(path) + ': ' + str(error)) from error
            count += 1
    if not count:
        raise ValueError('No native binaries were checked.')
    print(f'Verified minimum macOS <= 14.0 for {count} native binaries.')


if __name__ == '__main__':
    verify([Path(arg) for arg in sys.argv[1:]])
