"""Verify that the archived app exposes its lid-sleep service to macOS."""

import plistlib
import stat
import sys
from pathlib import PurePosixPath
from zipfile import ZipFile


def verify(path):
    contents = PurePosixPath('Tbox.app/Contents')
    registration = contents / 'Library/LaunchDaemons/com.xsddpx.tbox.lid-sleep.plist'

    with ZipFile(path) as archive:
        def check(entry, kind, required):
            mode = archive.getinfo(entry).external_attr >> 16
            if stat.S_IFMT(mode) != kind or mode & required != required:
                raise ValueError(f'{entry}: ZIP permissions {stat.S_IMODE(mode):04o}, expected at least {required:04o}')

        check(str(registration), stat.S_IFREG, 0o644)
        program = plistlib.loads(archive.read(str(registration)))['BundleProgram']
        helper = PurePosixPath('Tbox.app') / program
        if not helper.is_relative_to(contents) or '..' in helper.parts:
            raise ValueError(f'Invalid BundleProgram: {program}')
        check(str(helper), stat.S_IFREG, 0o755)
        check(str(contents / 'Info.plist'), stat.S_IFREG, 0o644)
        for directory in set(registration.parents) | set(helper.parents):
            if directory.parts:
                check(f'{directory}/', stat.S_IFDIR, 0o755)


if __name__ == '__main__':
    try:
        verify(sys.argv[1])
    except (KeyError, ValueError) as error:
        sys.exit(f'合盖唤醒后台组件包验证失败：{error}')
