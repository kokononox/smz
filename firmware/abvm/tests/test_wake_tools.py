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

    def test_readmes_carry_a_utf8_bom(self):
        readmes = sorted((ROOT / 'tools').glob('*/README-FA.txt'))
        self.assertTrue(readmes, 'expected the Persian readmes to exist')
        for readme in readmes:
            raw = readme.read_bytes()
            with self.subTest(readme=str(readme)):
                self.assertEqual(raw[:3], b'\xef\xbb\xbf',
                                 f'{readme} is Persian text and needs a UTF-8 BOM')

    def test_cmd_launchers_are_ascii_and_crlf(self):
        launchers = sorted((ROOT / 'tools').glob('*/*.cmd'))
        self.assertTrue(launchers, 'expected the wake tool launchers to exist')
        for launcher in launchers:
            raw = launcher.read_bytes()
            with self.subTest(script=str(launcher)):
                raw.decode('ascii')
                self.assertNotIn(b'\n', raw.replace(b'\r\n', b''),
                                 f'{launcher} must use CRLF line endings')


class WakeTestModeLauncherTests(unittest.TestCase):
    """A launcher that forgets its mode argument silently degrades the test into
    the safe dry run: the wake path runs, the macro never starts, and the run
    looks like a success.  That is how the real-macro test was lost once, so each
    mode gets its own clickable file and each file's argument is pinned here."""

    def test_each_live_mode_has_a_launcher_that_passes_it(self):
        tool = ROOT / 'tools' / 'wake-test'
        for launcher, mode in (('wake-test-full.cmd', 'full'),
                               ('wake-test-window.cmd', 'window')):
            text = (tool / launcher).read_text(encoding='ascii')
            with self.subTest(launcher=launcher):
                self.assertIn('wake-test.ps1" ' + mode, text)

    def test_the_default_launcher_stays_dry(self):
        text = (ROOT / 'tools' / 'wake-test' / 'wake-test.cmd').read_text(encoding='ascii')
        self.assertNotIn('wake-test.ps1" full', text)
        self.assertNotIn('wake-test.ps1" window', text)

    def test_the_script_only_treats_full_and_window_as_live(self):
        script = (ROOT / 'tools' / 'wake-test' / 'wake-test.ps1').read_text(encoding='utf-8-sig')
        self.assertIn("$window = ($Mode -eq 'window')", script)
        self.assertIn("$dry = -not (($Mode -eq 'full') -or $window)", script)

    def test_the_readme_names_every_launcher(self):
        readme = (ROOT / 'tools' / 'wake-test' / 'README-FA.txt').read_text(encoding='utf-8-sig')
        for launcher in ('wake-test.cmd', 'wake-test-full.cmd', 'wake-test-window.cmd'):
            with self.subTest(launcher=launcher):
                self.assertIn(launcher, readme)


if __name__ == '__main__':
    unittest.main()
