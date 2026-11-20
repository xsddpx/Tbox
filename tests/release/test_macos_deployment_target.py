import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('deployment', Path(__file__).resolve().parents[2] / 'scripts/verify-macos-deployment-target.py')
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)


class MacDeploymentTargetTests(unittest.TestCase):
    target = deployment.parse_version('15.0')

    def test_new_sdk_and_linker_do_not_raise_minimum_os(self):
        output = 'cmd LC_BUILD_VERSION\nplatform MACOS\nminos 15.0\nsdk 27.0\ntool LD\nversion 27037.1\n'
        self.assertEqual(deployment.minimum_versions(output, self.target), [(15, 0, 0)])

    def test_legacy_and_fat_binary_slices_are_all_checked(self):
        output = 'cmd LC_VERSION_MIN_MACOSX\nversion 10.15\nsdk 15.0\ncmd LC_BUILD_VERSION\nplatform MACOS\nminos 14.0\n'
        self.assertEqual(len(deployment.minimum_versions(output, self.target)), 2)
        with self.assertRaises(ValueError):
            deployment.minimum_versions(output + 'cmd LC_BUILD_VERSION\nplatform MACOS\nminos 15.1\n', self.target)

    def test_newer_required_os_is_rejected(self):
        for version in ('15.0.1', '16.0', '27.0'):
            with self.subTest(version=version), self.assertRaises(ValueError):
                deployment.minimum_versions('cmd LC_BUILD_VERSION\nplatform MACOS\nminos ' + version, self.target)

    def test_declared_target_sets_the_limit(self):
        output = 'cmd LC_BUILD_VERSION\nplatform MACOS\nminos 15.0'
        with self.assertRaises(ValueError):
            deployment.minimum_versions(output, deployment.parse_version('14.0'))

    def test_missing_invalid_or_other_platform_metadata_is_rejected(self):
        for output in ('', 'cmd LC_BUILD_VERSION\nplatform IOS\nminos 14.0', 'cmd LC_BUILD_VERSION\nminos unknown'):
            with self.subTest(output=output), self.assertRaises(ValueError):
                deployment.minimum_versions(output, self.target)
