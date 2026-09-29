"""Non-blocking GP6 feedback for physical Guard controls."""
import board
import pwmio
import time

_PATTERNS = {
    "start": ((784, 160), (988, 160), (1175, 200), (0, 80), (1175, 280)),
    "stop": ((392, 180), (330, 160), (262, 260), (0, 60), (196, 260)),
    "pause": ((523, 180), (0, 100), (523, 180), (0, 100), (523, 340)),
    "resume": ((659, 150), (784, 150), (988, 150), (784, 150), (988, 300)),
}

def cancel(owner):
    owner.guard_cue_name = None
    tone = owner.guard_cue_tone
    owner.guard_cue_tone = None
    if tone is not None:
        try:
            tone.duty_cycle = 0
            tone.deinit()
        except Exception:
            pass

def queue(owner, name):
    owner.guard_cue_name = name
    owner.guard_cue_index = 0
    owner.guard_cue_next = 0

def tick(owner):
    name = owner.guard_cue_name
    if name is None:
        return
    now = time.monotonic()
    if now < owner.guard_cue_next:
        return
    tone = owner.guard_cue_tone
    owner.guard_cue_tone = None
    if tone is not None:
        try:
            tone.duty_cycle = 0
            tone.deinit()
        except Exception:
            pass
    pattern = _PATTERNS[name]
    index = owner.guard_cue_index
    if index >= len(pattern):
        owner.guard_cue_name = None
        owner.guard_cue_index = 0
        return
    frequency, duration_ms = pattern[index]
    owner.guard_cue_index = index + 1
    owner.guard_cue_next = now + duration_ms / 1000
    if frequency > 0:
        try:
            owner.guard_cue_tone = pwmio.PWMOut(
                board.GP6, duty_cycle=32768, frequency=int(frequency),
                variable_frequency=True)
        except Exception:
            owner.guard_cue_name = None
            owner.emit("ERR|CAL|AUDIO")