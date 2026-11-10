from pathlib import Path
import struct
import unittest


ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / "src/Tbox.Desktop/Assets"
TARGET_SIZES = (16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)


def png_size(path):
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        raise ValueError(f"Not a PNG: {path}")
    return struct.unpack(">II", data[16:24])


class WindowsIconTests(unittest.TestCase):
    def test_unplated_shell_assets_cover_common_target_sizes(self):
        for size in TARGET_SIZES:
            path = ASSETS / f"Square44x44Logo.targetsize-{size}_altform-unplated.png"
            with self.subTest(size=size):
                self.assertTrue(path.is_file())
                self.assertEqual((size, size), png_size(path))

    def test_windows_package_copies_target_size_variants(self):
        script = (ROOT / "scripts/package-desktop-windows.ps1").read_text(encoding="utf-8")
        self.assertIn("'Square44x44Logo*.png'", script)
        self.assertIn("Assert-DesktopIconAssets $unpacked", script)


if __name__ == "__main__":
    unittest.main()
