#!/usr/bin/env python3
"""Reject incomplete, stale or mixed Windows exporter artifacts before packaging."""
import argparse
import hashlib
import json
from pathlib import Path

SOURCES = {
    'ffmpeg-8.1.2.tar.xz': '464beb5e7bf0c311e68b45ae2f04e9cc2af88851abb4082231742a74d97b524c',
    'libwebp-1.6.0.tar.gz': 'e4ab7009bf0629fd11982d4c2aa83964cf244cffba7347ecd39019a9e38c4564',
}
FILES = {'bin/ffmpeg.exe', 'BUILD-INFO.json', 'LICENSE.txt', 'libwebp-LICENSE.txt', 'libwebp-PATENTS.txt'}
SYSTEM_DLLS = {'advapi32.dll', 'bcrypt.dll', 'kernel32.dll', 'msvcrt.dll', 'ole32.dll', 'shell32.dll', 'user32.dll', 'ws2_32.dll'}


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def verify(directory, recipe):
    paths = list(directory.rglob('*'))
    if any(p.is_symlink() for p in paths) or {p.relative_to(directory).as_posix() for p in paths if p.is_file()} != FILES:
        raise ValueError('Windows exporter files differ from the approved artifact set.')
    if any(not (directory / name).stat().st_size for name in FILES):
        raise ValueError('Empty exporter or license file.')
    record = json.loads((directory / 'BUILD-INFO.json').read_text())
    for key, value in {'ffmpeg': '8.1.2', 'libwebp': '1.6.0', 'target': 'win-x64', 'license': 'LGPL-2.1-or-later'}.items():
        if record.get(key) != value:
            raise ValueError('Unexpected exporter version, target or license.')
    sources = record.get('sources', [])
    if len(sources) != len(SOURCES) or {item['name']: item['sha256'] for item in sources} != SOURCES:
        raise ValueError('Exporter source archives differ from pinned inputs.')
    imports = record.get('systemImports', [])
    if not imports or any(name.lower() not in SYSTEM_DLLS for name in imports):
        raise ValueError('Exporter requires an unapproved runtime DLL.')
    if record.get('buildScriptSha256') != sha256(recipe):
        raise ValueError('Exporter was built using another recipe. Rebuild it.')
    if record.get('binarySha256') != sha256(directory / 'bin/ffmpeg.exe'):
        raise ValueError('Exporter binary differs from its build record.')
    return record


if __name__ == '__main__':
    root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser()
    parser.add_argument('directory', nargs='?', type=Path, default=root / 'TEMP/media-runtime/windows')
    args = parser.parse_args()
    record = verify(args.directory, root / 'scripts/prepare-windows-media.sh')
    print('Verified Windows FFmpeg 8.1.2: ' + record['binarySha256'])
