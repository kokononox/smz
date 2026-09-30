#!/usr/bin/env python3
"""Closing is single-owner: first click hides, shuts down, then force-closes."""
from pathlib import Path

root = Path(__file__).resolve().parents[3]
source = (root / "ams-shell/src/Ams.UI/MainWindow.xaml.cs").read_text(encoding="utf-8")
assert "private bool _closeInProgress;" in source
handler = source[source.index("private async void Window_Closing"): ]
handler = handler[:handler.index("\n    }", handler.index("finally")) + 6]
assert "if (_closeInProgress) return;" in handler
assert "if (vm is not null && !vm.ConfirmDiscard()) return;" in handler
assert handler.index("ConfirmDiscard") < handler.index("_closeInProgress = true;") < handler.index("Hide();")
assert "await vm.ShutdownAsync();" in handler
assert "finally" in handler and handler.index("_forceClose = true;") < handler.index("Close();")
print("mainwindow single-click close contract: PASS")
