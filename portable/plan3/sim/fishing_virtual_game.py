#!/usr/bin/env python3
"""Deterministic virtual fishing game for the real Build-118 Game VM.

Models: cast (7), parallel hand movement, scoped Splash eight seconds later,
response (F), pause/resume, Cursor continuation and one unchanged LOOPTIME.
"""
from pathlib import Path
import random, sys

ROOT = Path(__file__).resolve().parents[1]
MODERN = ROOT / "CIRCUITPY-MODERN"
sys.path.insert(0, str(MODERN))
import plan_engine_game_core as core
import plan_engine_game_runtime as runtime


class Arm:
    def __init__(self):
        self.sound_result = None
        self.sound_detail = None
        self.async_sound = True
    def send(self, command, timeout=0):
        if command == "ASNDCANCEL": self.sound_result = None
    def pump(self): pass


class Runner:
    def __init__(self): self.arm=Arm(); self.telemetry=[]
    def emit(self, value): self.telemetry.append(value)


class VirtualFishingGame:
    screen_w=1920; screen_h=1080
    def __init__(self):
        self.r=Runner(); self.t=0.0; self.events=[]; self.cast_times=[]
        self.catches=0; self._sound_watch=None; self._sound_watch_pending=None
        self._sound_watch_servicing=False; self._parallel_sound=None
        self.pause_from=2.0; self.pause_until=3.5; self.pause_seen=False
    def now(self): return self.t
    def gate(self): return self.t < 22.0
    def log(self, value): self.events.append((round(self.t,3),"log",value))
    def sleep_ms(self, ms):
        target=self.t+max(0,ms)/1000
        if not self.pause_seen and self.t < self.pause_from <= target:
            self.t=self.pause_from; self.events.append((self.t,"pause",None))
            self.t=self.pause_until; self.events.append((self.t,"resume",None)); self.pause_seen=True
            target += self.pause_until-self.pause_from
        self.t=target
        if self.cast_times and self.catches < len(self.cast_times):
            due=self.cast_times[self.catches]+8.0
            if self.t >= due and self._parallel_sound == "async":
                self.r.arm.sound_result=True; self.r.arm.sound_detail="peak=100"
                self.events.append((round(due,3),"splash",100))
        return self.gate()
    def key_combo(self, values, lo, hi):
        key=values[0]
        self.events.append((round(self.t,3),"key",key))
        if key==55: self.cast_times.append(self.t)
        if key==70: self.catches += 1
    def mmove_relative(self,dx,dy): self.events.append((round(self.t,3),"move",(dx,dy)))
    def kdown(self,v): pass
    def kup(self,v): pass
    def wheel(self,v): pass
    def raw(self,v): pass
    def beep(self,f,d): pass
    def sound_profile(self,*args): return (76,20)
    def sound_start(self,threshold,minimum,timeout): self._parallel_sound="async"
    def sound_cancel(self): self._parallel_sound=None; self.r.arm.sound_result=None
    def sound_poll(self):
        if self.r.arm.sound_result is None: return None
        result=self.r.arm.sound_result; self.r.arm.sound_result=None; return result
    def sound_peak(self): return 100
    def sound_parallel_safe(self): return True
    def _active(self):
        if self._sound_watch is None: return []
        scope=self._sound_watch.get("scope")
        return [p for p in self._sound_watch["profiles"] if p["mode"]=="global" or p["id"]==scope]
    def _arm_sound_watch(self):
        if self._sound_watch is None or self._sound_watch_servicing or self._parallel_sound is not None: return
        profiles=self._active(); self._sound_watch["armed"]=profiles or None
        if profiles: self.sound_start(76,20,30000)
    def install_sound_watch(self,profiles):
        self._sound_watch={"profiles":profiles,"scope":None,"scope_result":None,
                           "cooldown_until":0.0,"armed":None}
        self._sound_watch_pending=None; self._arm_sound_watch()
    def poll_sound_watch(self):
        if self._sound_watch is None or self._sound_watch_pending is not None: return
        if self._parallel_sound is None: self._arm_sound_watch()
        result=self.sound_poll()
        if result: self._sound_watch_pending=True
    def take_sound_watch(self):
        value=self._sound_watch_pending; self._sound_watch_pending=None; return value
    def begin_profile_wait(self,pid):
        if self._parallel_sound is not None: self.sound_cancel()
        self._sound_watch["scope"]=pid; self._sound_watch["scope_result"]=None
        self._arm_sound_watch()
    def poll_profile_wait(self,pid): return self._sound_watch.get("scope_result")
    def end_profile_wait(self):
        if self._parallel_sound is not None: self.sound_cancel()
        self._sound_watch["scope"]=None; self._sound_watch["scope_result"]=None
    def suspend_sound_watch(self): self._sound_watch_servicing=True; self.sound_cancel()
    def resume_sound_watch(self,cooldown): self._sound_watch_servicing=False
    def close_sound_watch(self): self._sound_watch=None; self._parallel_sound=None


class ResponseCommands:
    def __init__(self,name,offsets=None): self.rows=[("PLAN","2"),("KEY","combo=70|hold=80,180")]
    def __len__(self): return len(self.rows)
    def __getitem__(self,index): return self.rows[index]
    def __iter__(self): return iter(self.rows)
    def close(self): pass


def scenario():
    watch="splash,76,511,20,5,900,splash_steps.txt,scoped"
    return [("PLAN","2"),("SOUNDWATCH",watch),("LOOPTIME","21"),
            ("KEY","combo=55|hold=80,180"),("PGROUP",""),
            ("LOOP","2"),("DELAY","250,250"),("RMOUSE","virtual"),("ENDLOOP",""),
            ("PARITEM",""),("LOOP","1"),("WPROFILE","splash,18000,22000"),
            ("ENDLOOP",""),("ENDPAR",""),("ENDLOOP","")]


def main():
    ctx=VirtualFishingGame(); random.seed(118); runtime._response_run=None
    original=core._FileCommands; original_mouse=core._mouse_events
    core._FileCommands=ResponseCommands
    core._mouse_events=lambda args,c,s: iter((("move",100,3,2),("wait",100)))
    resume=None; deadlines=[]
    try:
        while ctx.gate():
            signal=runtime.run_game(scenario(),ctx,core,resume)
            if signal is None: break
            deadlines.append(signal["_game_cursor"].frames[-1][6])
            assert runtime.service_sound_exit(ctx,signal), "Splash did not execute response"
            resume=signal
    except core.GameAbort:
        pass
    finally:
        core._FileCommands=original; core._mouse_events=original_mouse
    assert ctx.catches >= 2, ctx.events
    assert len(set(deadlines)) == 1, deadlines
    assert ctx.cast_times[1] > ctx.cast_times[0]
    assert any(e[1]=="move" for e in ctx.events)
    assert any(e[1]=="pause" for e in ctx.events) and any(e[1]=="resume" for e in ctx.events)
    assert ctx.t < 24, ctx.t
    print("virtual fishing: PASS")
    print("casts=%d catches=%d deadline=%.3f finished=%.3f" %
          (len(ctx.cast_times),ctx.catches,deadlines[0],ctx.t))

if __name__ == "__main__": main()
