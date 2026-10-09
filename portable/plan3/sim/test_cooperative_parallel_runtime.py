#!/usr/bin/env python3
"""Cooperative PGROUP: HANDPATH/TYPE/WSND plus nested LOOP and RPKG."""
import random, sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
MODERN=ROOT/'CIRCUITPY-MODERN'
sys.path.insert(0,str(MODERN))
for name in ('plan_engine','plan_engine_exec','plan_engine_human','plan_engine_parse'):
    sys.modules.pop(name,None)
import plan_engine
from plan_engine_human import PausePlanner
assert 'plan_engine_parallel' not in sys.modules, 'parallel scheduler imported before PGROUP'

class Ctx:
    plan_api=3; screen_w=1920; screen_h=1080; speed_min=300; speed_max=2000; mouse_mode='relative'
    def __init__(self):
        self.t=0.0; self.ev=[]; self.sound=False; self.peak=140
    def now(self): return self.t
    def gate(self): return True
    def sleep_ms(self,ms): self.t+=ms/1000.0; self.ev.append(('sleep',ms)); return True
    def log(self,s): self.ev.append(('log',s))
    def mmove_relative(self,dx,dy): self.ev.append(('move',round(self.t,3),dx,dy))
    def mmove(self,x,y): self.ev.append(('abs',round(self.t,3),x,y))
    def type_char(self,ch): self.ev.append(('char',round(self.t,3),ch))
    def ktext(self,a,z,s): self.ev.append(('text',round(self.t,3),s))
    def kcombo(self,v): self.ev.append(('combo',round(self.t,3),v))
    def key_combo(self,v,a,z): self.ev.append(('key',round(self.t,3),tuple(v)))
    def sound_start(self,thr,minimum,timeout): self.sound=True; self.ev.append(('sound-start',round(self.t,3),thr,minimum,timeout))
    def sound_poll(self):
        self.ev.append(('sound-poll',round(self.t,3)))
        return True if self.t>=0.055 else None
    def sound_cancel(self): self.sound=False; self.ev.append(('sound-cancel',round(self.t,3)))
    def sound_peak(self): return self.peak
    def sound_profile(self,profile_id,binding,threshold,minimum): return threshold,minimum
    def read_plan_file(self,name): return getattr(self,'files',{}).get(name,'PLAN|2\n')
    def beep(self,frequency,duration): self.ev.append(('beep',frequency,duration))

plan='''PLAN|2
PGROUP
LOOP|2
RPKG|seq,0,1
HANDPATH|20,2,0;20,0,2
ENDPKG
ENDLOOP
PARITEM
LOOP|1
WSND|90,60,500
TYPE|h=15,15|text=ok
KEY|combo=13
ENDLOOP
ENDPAR'''
ctx=Ctx(); random.seed(4)
ops=plan_engine.parse_plan(plan)
plan_engine.run_plan(ops,ctx)
assert 'plan_engine_parallel' in sys.modules, 'parallel scheduler was not loaded at PGROUP'
moves=[e for e in ctx.ev if e[0]=='move']
chars=[e for e in ctx.ev if e[0]=='char']
assert 1 <= len(moves) <= 4,moves
assert ''.join(e[2] for e in chars)=='ok',chars
# Mouse reports continue while listening, then sound success cancels siblings.
assert any(e[0]=='move' and 0.0 < e[1] < 0.055 for e in ctx.ev),ctx.ev
assert all(e[1] <= chars[0][1] for e in moves),ctx.ev
assert ('log','parallel wsnd heard - cancel siblings') in ctx.ev,ctx.ev
assert any(e[0]=='sound-start' for e in ctx.ev),ctx.ev
assert ('key',ctx.ev[-1][1],(13,)) in ctx.ev or any(e[0]=='key' and e[2]==(13,) for e in ctx.ev),ctx.ev

# Two calibrated WSND branches share one ARM ADC listener. The scheduler
# listens at the lowest threshold, then selects the highest threshold matched
# by the reported peak instead of opening a second listener and crashing.
dual='''PLAN|2
PGROUP
WSND|130,60,500
BEEP|700,100
PARITEM
WSND|30,60,500
BEEP|900,100
ENDPAR'''
high_ctx=Ctx()
plan_engine.run_plan(plan_engine.parse_plan(dual),high_ctx)
assert ('sound-start',0.0,130,60,500) in high_ctx.ev,high_ctx.ev
assert ('sound-start',0.0,30,60,500) in high_ctx.ev,high_ctx.ev
assert ('beep',700,100) in high_ctx.ev and ('beep',900,100) not in high_ctx.ev,high_ctx.ev
assert ('log','parallel wsnd profile range=130..65535 priority=0 peak=140') in high_ctx.ev,high_ctx.ev

low_ctx=Ctx(); low_ctx.peak=80
plan_engine.run_plan(plan_engine.parse_plan(dual),low_ctx)
assert ('beep',900,100) in low_ctx.ev and ('beep',700,100) not in low_ctx.ev,low_ctx.ev
assert ('log','parallel wsnd profile range=30..65535 priority=0 peak=80') in low_ctx.ev,low_ctx.ev
# Synchronous SCAL polls must not land between streamed mouse segments. The
# hardware round trip is ~60 ms and produced visible periodic cursor stalls.
move_times=[e[1] for e in moves]
sound_times=[e[1] for e in ctx.ev if e[0]=='sound-poll']
assert not any(min(move_times) < t < max(move_times) for t in sound_times),(moves,sound_times)
# Parser must still fail closed for the Arm-owned blocking sound/click transaction.
bad='PLAN|2\nPGROUP\nTRGSND|90,60,500,1,80,180,30,90\nPARITEM\nDELAY|1\nENDPAR'
try:
    plan_engine.parse_plan(bad)
except ValueError as exc:
    assert 'not allowed inside PGROUP' in str(exc),exc
else:
    raise AssertionError('TRGSND unexpectedly accepted')

# A large portable RMOUSE inside PGROUP must use the bounded streaming curve,
# not allocate the dense 2–3 px plan that exhausted the Pico after ~100 seconds.
import plan_engine_parallel as parallel
original_plan_move=parallel.plan_move
def forbidden_dense_plan(*args,**kwargs):
    raise AssertionError('relative PGROUP RMOUSE called dense plan_move')
parallel.plan_move=forbidden_dense_plan
try:
    random.seed(11)
    pos=[960,540]
    events=list(parallel._parallel_mouse_events({
        'region':(1301,0,378,1049),'before':(20,85),'after':(20,103),
        'curve':(0,3),'mid':(36,(144,375)),'over':2,'mt':(16,159),
        'idle':((5,12),(800,3000))
    },Ctx(),PausePlanner(),pos))
finally:
    parallel.plan_move=original_plan_move
stream_moves=[e for e in events if e[0]=='move']
assert 6 <= len(stream_moves) <= 128,len(stream_moves)
assert sum(e[2] for e in stream_moves)==pos[0]-960
assert sum(e[3] for e in stream_moves)==pos[1]-540
assert all(e[4] is True for e in stream_moves)

# A sampled speed band is authoritative when mt is absent. Account for ARM's
# own micro-step time so the Pico does not pay the recorded cadence twice.
random.seed(12)
speed_events=list(parallel._parallel_relative_mouse_events(
    {},Ctx(),PausePlanner(),[960,540],
    dict(parallel._DEFAULT_CFG, speed_min=500, speed_max=500, mt_min=0, mt_max=0,
         before_min=0,before_max=0,after_min=0,after_max=0,
         mid_chance=0,idle_pause_max=0),1260,540))
speed_moves=[e for e in speed_events if e[0]=='move']
assert 8 <= len(speed_moves) <= 128,len(speed_moves)
# 300 px at 500 px/s targets 600 ms; ARM supplies ~300 ms, so waits total 300.
assert sum(e[1] for e in speed_moves)==300,speed_moves
assert max(e[1] for e in speed_moves) <= 8,speed_moves

# Timeout is also a race result: cancel the infinite mouse sibling, skip the
# reaction after WSND, and return so the outer fishing loop can cast again.
class TimeoutCtx(Ctx):
    def sound_poll(self):
        self.ev.append(('sound-poll',round(self.t,3)))
        return False if self.t>=0.055 else None

timeout_plan='''PLAN|2
PGROUP
LOOP|0
DELAY|10
ENDLOOP
PARITEM
WSND|90,60,500
KEY|combo=70
ENDPAR'''
timeout_ctx=TimeoutCtx()
plan_engine.run_plan(plan_engine.parse_plan(timeout_plan),timeout_ctx)
assert not any(e[0]=='key' and e[2]==(70,) for e in timeout_ctx.ev),timeout_ctx.ev
assert ('log','parallel wsnd timeout - cancel group') in timeout_ctx.ev,timeout_ctx.ev
assert timeout_ctx.t < 1.0,timeout_ctx.t


# Build 92: the lightweight Game scheduler owns one physical listener for the
# entire Game route. A matching profile runs its response route, honors
# priority in an overlap, and resumes the exact Game iterator afterwards.
import plan_engine_game as game_runner
class WatchCtx(Ctx):
    def __init__(self):
        super().__init__(); self.peak=75
        self.files={'whisper_steps.txt':'PLAN|2\nBEEP|700,90\n',
                    'splash_steps.txt':'PLAN|2\nBEEP|900,90\n'}
    def gate(self): return self.t < .26

watch_commands=[
    ('PLAN','2'),('PGROUP',''),
    ('LOOP','0'),('KEY','combo=70'),('DELAY','20,20'),('ENDLOOP',''),
    ('PARITEM',''),('LOOP','0'),
    ('WSNDP','1,aaaaaaaaaaaa,20,60,1000,20,80,10,whisper_steps.txt,100'),
    ('ENDLOOP',''),('PARITEM',''),('LOOP','0'),
    ('WSNDP','2,bbbbbbbbbbbb,20,60,1000,70,200,5,splash_steps.txt,100'),
    ('ENDLOOP',''),('ENDPAR','')]
watch_ctx=WatchCtx()
try:
    game_runner.run_game(watch_commands,watch_ctx)
except game_runner.GameAbort:
    pass
assert ('beep',700,90) in watch_ctx.ev and ('beep',900,90) not in watch_ctx.ev,watch_ctx.ev
keys=[e for e in watch_ctx.ev if e[0]=='key' and e[2]==(70,)]
assert len(keys)>=2,watch_ctx.ev
beep_index=watch_ctx.ev.index(('beep',700,90))
assert any(i<beep_index for i,e in enumerate(watch_ctx.ev) if e[0]=='key'),watch_ctx.ev
assert any(i>beep_index for i,e in enumerate(watch_ctx.ev) if e[0]=='key'),watch_ctx.ev

print('cooperative parallel runtime: shared listener + resumable response passed')
