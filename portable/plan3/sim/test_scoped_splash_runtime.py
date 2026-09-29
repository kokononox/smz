#!/usr/bin/env python3
"""Whisper resumes the current cast; Splash or its timeout advances to next cast."""
from pathlib import Path
import sys, random
root=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(root/'CIRCUITPY-MODERN'))
import plan_engine_game as game

class ArmState:
    sound_result=None
class RuntimeState:
    def __init__(self): self.arm=ArmState()

class Ctx:
    screen_w=1920; screen_h=1080; speed_min=300; speed_max=2000
    def __init__(self, splash=True):
        self.t=0.0; self.ev=[]; self.scope=None; self.pending=None
        self.whisper=False; self.splash=splash; self.profile_result=None
        self.in_sleep=False
        self.r=RuntimeState()
    def now(self): return self.t
    def gate(self): return self.t < 2
    def log(self,s): self.ev.append(('log',s))
    def sleep_ms(self,ms):
        self.t += ms/1000
        self.in_sleep=True
        try: self.poll_sound_watch()
        finally: self.in_sleep=False
        return self.gate()
    def install_sound_watch(self,profiles):
        self.profiles=profiles; self.pending=None
        self._sound_watch={"profiles":profiles,"scope_result":None}
        self.ev.append(('watch',len(profiles)))
    def poll_sound_watch(self):
        if self.pending is None and not self.whisper and self.t >= .02:
            self.whisper=True
            self.pending=next(x for x in self.profiles if x['id']=='whisper')
        return None
    def take_sound_watch(self):
        winner=self.pending; self.pending=None; return winner
    def begin_profile_wait(self,pid): self.scope=pid; self.ev.append(('scope-start',pid))
    def poll_profile_wait(self,pid):
        if self.splash and self.t >= .06 and self.profile_result is None:
            self.profile_result=next(x for x in self.profiles if x['id']==pid)
        return self.profile_result
    def end_profile_wait(self): self.scope=None; self.profile_result=None; self.ev.append(('scope-end',))
    def suspend_sound_watch(self): self.ev.append(('suspend',))
    def resume_sound_watch(self,cooldown): self.ev.append(('resume',cooldown))
    def close(self): pass
    def read_plan_file(self,name):
        if name=='whisper_steps.txt': return 'PLAN|2\nBEEP|1900,80\n'
        if name=='splash_steps.txt': return 'PLAN|2\nKEY|combo=70\n'
        raise AssertionError(name)
    def beep(self,f,d):
        assert not self.in_sleep, 'response executed inside sleep polling'
        self.ev.append(('beep',f,d))
    def key_combo(self,v,a,z):
        assert not self.in_sleep, 'response executed inside sleep polling'
        self.ev.append(('key',tuple(v)))
    def kdown(self,v): pass
    def kup(self,v): pass
    def wheel(self,v): pass
    def raw(self,v): pass
    def mmove_relative(self,x,y): self.ev.append(('move',x,y))

header='SOUNDWATCH|whisper,20,200,60,10,1800,whisper_steps.txt,global;splash,20,100,60,5,900,splash_steps.txt,scoped'
commands=[('PLAN','2'),('SOUNDWATCH',header.split('|',1)[1]),('PGROUP',''),
          ('LOOP','0'),('DELAY','10,10'),('ENDLOOP',''),('PARITEM',''),
          ('WPROFILE','splash,80,120'),('ENDPAR',''),('KEY','combo=71')]
random.seed(4); ctx=Ctx(True); game.run_game(commands,ctx)
assert ('beep',1900,80) in ctx.ev,ctx.ev
assert ('key',(70,)) in ctx.ev,ctx.ev
assert ('key',(71,)) in ctx.ev,ctx.ev
assert ctx.ev.index(('beep',1900,80)) < ctx.ev.index(('key',(70,))),ctx.ev
assert ('resume',1800) in ctx.ev and ('resume',900) in ctx.ev,ctx.ev

# No splash: random per-cast timeout ends the race and still advances after PGROUP.
random.seed(7); timeout=Ctx(False); game.run_game(commands,timeout)
assert ('key',(70,)) not in timeout.ev,timeout.ev
assert ('key',(71,)) in timeout.ev,timeout.ev
assert any(e==('log','scoped splash timeout -> next cast') for e in timeout.ev),timeout.ev
assert .08 <= timeout.t <= .13,timeout.t
print('scoped Splash: random timeout, Whisper interrupt, response and next-cast semantics passed')

# The real Pico context must not recursively service the Game callback from
# poll_profile_wait().  Cooperative sleep_ms() owns callback polling; keeping
# the scoped accessor passive protects CircuitPython's bounded pystack when
# WPROFILE is nested under PGROUP/RPKG/LOOP.
runtime_source=(root/'CIRCUITPY-MODERN'/'combined_guard_runtime.py').read_text(encoding='utf-8')
profile_wait=runtime_source.split('    def poll_profile_wait(self, profile_id):',1)[1].split(
    '    def end_profile_wait(self):',1)[0]
assert '_sound_watch_callback' not in profile_wait,profile_wait
assert 'return state.get("scope_result")' in profile_wait,profile_wait
print('scoped Splash: profile waiter is passive; response stays outside sleep')

# Hardware response routes must stay file-backed instead of allocating the
# whole text plus a tuple list after SoundWatch has fragmented the Pico heap.
import plan_engine_game_runtime as game_runtime
import plan_engine_game_core as game_core
closed=[]
class FlashCommands:
    def __init__(self,name):
        assert name=='splash_steps.txt'
        self.rows=[('PLAN','2'),('KEY','combo=70|hold=80,180'),('DELAY','1,1')]
    def __len__(self): return len(self.rows)
    def __getitem__(self,index): return self.rows[index]
    def __iter__(self): return iter(self.rows)
    def close(self): closed.append(True)
original_commands=game_core._FileCommands
game_core._FileCommands=FlashCommands
flash=Ctx(True)
flash.read_plan_file=lambda name: (_ for _ in ()).throw(AssertionError('heap read '+name))
game_runtime._core=game_core
game_runtime._response_run=None
game_runtime._prepare_response(flash)
game_runtime._run_response(flash,'splash_steps.txt',
    {'speed':[0,2000],'pos':[960,540],'pauses':None,'watch':False})
game_core._FileCommands=original_commands
assert ('key',(70,)) in flash.ev and closed==[True],flash.ev
print('scoped Splash: hardware response remains file-backed')

runtime_text=(root/'CIRCUITPY-MODERN'/'plan_engine_game_runtime.py').read_text(encoding='utf-8')
actions_text=(root/'CIRCUITPY-MODERN'/'plan_engine_game_actions.py').read_text(encoding='utf-8')
sound_text=(root/'CIRCUITPY-MODERN'/'plan_engine_game_sound.py').read_text(encoding='utf-8')
assert '_queue_sound_watch' not in runtime_text
assert 'ctx.install_sound_watch(_core._watch_profiles(args))' in actions_text
assert 'before-response-bind' in runtime_text
assert 'before-response-callback' in sound_text
assert '_response_run(ctx, name, state, _run)' in runtime_text
assert 'plan_engine_parse' not in runtime_text
assert 'return _sound_module().resolve_sound_watch(ctx)' in runtime_text
assert 'return _sound_module().service_sound_exit(ctx, signal, _run_response)' in runtime_text
parallel_text=(root/'CIRCUITPY-MODERN'/'plan_engine_game_parallel.py').read_text(encoding='utf-8')
assert 'response_runner(ctx, winner["file"], state, execute)' in parallel_text
assert 'service_pending_response' not in parallel_text
loop_body=parallel_text.split('while tasks:',1)[1].split('now = int(ctx.now()',1)[0]
assert loop_body.index('ctx.r.arm.sound_result is True') < loop_body.index(
    'resolve_sound_watch(ctx)')
assert 'return ctx._sound_watch' in loop_body
context_text=(root/'CIRCUITPY-MODERN'/'combined_guard_runtime.py').read_text(encoding='utf-8')
assert '_sound_watch_callback' not in context_text
assert 'self._sound_watch_pending = True' in context_text
assert 'def take_sound_watch(self):' in context_text
poll_body=context_text.split('def poll_sound_watch(self):',1)[1].split(
    'def take_sound_watch(self):',1)[0]
take_body=context_text.split('def take_sound_watch(self):',1)[1].split(
    'def begin_profile_wait(self, profile_id):',1)[0]
sleep_body=context_text.split('def sleep_ms(self, ms):',1)[1].split(
    'def log(self, text):',1)[0]
assert 'self.poll_sound_watch()' not in sleep_body
assert 'self.r.arm.sound_result is not None' in sleep_body
assert 'select_sound_profile' not in poll_body and 'sound_peak()' not in poll_body
assert 'select_sound_profile' not in take_body and 'sound_peak()' not in take_body
resolve_body=sound_text.split('def resolve_sound_watch(ctx):',1)[1].split(
    'def service_pending_response(ctx, state, run_response):',1)[0]
assert '_select_profile' in resolve_body and 'sound_peak()' in resolve_body
assert 'profiles = watch["armed"]' in resolve_body
assert resolve_body.index('ctx.poll_sound_watch()') < resolve_body.index(
    'ctx.take_sound_watch()')
assert '_resolve_sound_watch(ctx)' in runtime_text
assert 'def service_sound_exit(ctx, signal):' in runtime_text
service_body=sound_text.split('def service_sound_exit(ctx, signal, run_response):',1)[1]
assert 'ctx.r.arm.send("ASNDCANCEL", 2)' in service_body
assert '_drain_cancelled_sound(ctx)' in service_body
assert 'EVT|SOUNDWATCH|response-start' in service_body
assert 'EVT|SOUNDWATCH|response-done' in service_body
assert 'plan_engine_parse' not in sound_text
facade_text=(root/'CIRCUITPY-MODERN'/'plan_engine_game.py').read_text(encoding='utf-8')
assert 'def service_sound_exit(ctx, signal):' in facade_text
run_file_body=facade_text.split('def run_game_file(name, ctx):',1)[1]
assert 'finally:' in run_file_body and 'commands.close()' in run_file_body
assert 'def resume_game_file(name, ctx, resume):' in facade_text
assert 'signal["_game_cursor"].commands = None' in facade_text
assert '("plan_engine_game_sound", "sound")' in facade_text
code_text=(root/'CIRCUITPY-MODERN'/'code.py').read_text(encoding='utf-8')
assert 'plan_engine_game.service_sound_exit(ctx, signal)' in code_text
assert 'EVT|SOUNDWATCH|next-cast|mode=resume' in code_text
light_runner=code_text.split('def _run_light_route(ctx, commands):',1)[1].split(
    '    if any(item[0]',1)[0]
assert 'resume = None' in light_runner
assert 'plan_engine_game.resume_game_file(commands, ctx, resume)' in light_runner
assert 'return True' not in light_runner
assert 'state.clear()' not in service_body and 'ctx.close_sound_watch()' not in service_body
parallel_text=(root/'CIRCUITPY-MODERN'/'plan_engine_game_parallel.py').read_text(
    encoding='utf-8')
raw_exit=parallel_text.split('ctx.r.arm.sound_result is True:',1)[1].split(
    'winner = resolve_sound_watch',1)[0]
assert raw_exit.index('ctx.poll_sound_watch()') < raw_exit.index(
    'return ctx._sound_watch')
print('SoundWatch: response unwinds safely, then resumes the same Cursor/deadline')

# UART cancellation drains a late DETECTED from the closed cast before the
# next listener is installed, without growing the startup runtime compiler.
context_text=(root/'CIRCUITPY-MODERN'/'combined_guard_runtime.py').read_text(encoding='utf-8')
assert 'sound_armed' not in context_text and 'drain_sound' not in context_text
drain_body=sound_text.split('def _drain_cancelled_sound(ctx):',1)[1].split(
    'def _select_profile(',1)[0]
assert 'time.monotonic() + .06' in drain_body
assert drain_body.count('arm.sound_result = None') == 2
assert 'arm.pump()' in drain_body
print('SoundWatch: delayed ASND is drained outside the startup runtime')
