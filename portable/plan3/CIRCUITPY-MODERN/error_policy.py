"""Portable fatal-error policy for PLAN|2/PLAN|3 runtimes.

The record is intentionally bounded: a portable board must never grow a flash log on
每 run. The alarm is local and does not depend on a connected PC.
"""
import time

try:
    import board
    import digitalio
    import pwmio
except Exception:
    board = digitalio = pwmio = None

MAX_HISTORY = 8
HISTORY_FILE = "error_history.txt"
BUZZER_PIN = "GP6"


def _append_bounded(line):
    try:
        rows = []
        with open(HISTORY_FILE, "r") as f:
            rows = f.read().splitlines()
    except OSError:
        pass
    rows = (rows + [line])[-MAX_HISTORY:]
    try:
        with open(HISTORY_FILE, "w") as f:
            f.write("\n".join(rows) + "\n")
    except OSError:
        pass


def fatal(message, led=None, repeat=True):
    """Record only the bounded last errors, flash the existing fault LED and sound locally."""
    _append_bounded("%d|%s" % (time.monotonic(), message[:160]))
    if led is not None:
        try:
            led.value = True
        except Exception:
            pass
    buzzer = None
    try:
        if board is not None and pwmio is not None:
            buzzer = pwmio.PWMOut(getattr(board, BUZZER_PIN), frequency=700, duty_cycle=0)
            while True:
                buzzer.duty_cycle = 32768
                time.sleep(0.22)
                buzzer.duty_cycle = 0
                time.sleep(0.18)
                if not repeat:
                    break
    except Exception:
        pass
    finally:
        if buzzer is not None:
            try:
                buzzer.deinit()
            except Exception:
                pass
