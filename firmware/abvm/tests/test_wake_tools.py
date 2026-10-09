import pathlib, unittest
ROOT = pathlib.Path(__file__).resolve().parents[3]


class WakeToolScriptTests(unittest.TestCase):
    """The wake tools are run by double-click on the target machine, which means
    Windows PowerShell 5.1 reads the script.  5.1 decodes a BOM-less file with the
    system code page, so a UTF-8 script full of Persian text is mangled before the
    parser ever sees it -- the strings break, and the tool dies with a parse error
    the operator cannot act on.  A BOM is what makes the encoding unambiguous."""

    def test_scripts_with_non_ascii_text_carry_a_utf8_bom(self):
        scripts = sorted((ROOT / 'tools').glob('*/*.ps1'))
        self.assertTrue(scripts, 'expected the wake tools to exist')
        checked = 0
        for script in scripts:
            raw = script.read_bytes()
            with self.subTest(script=str(script)):
                if all(byte < 0x80 for byte in raw):
                    continue  # pure ASCII cannot be misread by any code page
                checked += 1
                self.assertEqual(raw[:3], b'\xef\xbb\xbf',
                                 f'{script} has non-ASCII text and needs a UTF-8 BOM')
        self.assertGreaterEqual(checked, 2, 'the wake tools carry Persian text')

    def test_cmd_launchers_are_ascii_and_crlf(self):
        launchers = sorted((ROOT / 'tools').glob('*/*.cmd'))
        self.assertTrue(launchers, 'expected the wake tool launchers to exist')
        for launcher in launchers:
            raw = launcher.read_bytes()
            with self.subTest(script=str(launcher)):
                raw.decode('ascii')
                self.assertNotIn(b'\n', raw.replace(b'\r\n', b''),
                                 f'{launcher} must use CRLF line endings')


if __name__ == '__main__':
    unittest.main()
