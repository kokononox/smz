# Generated memory-fit core from canonical plan_engine.py; do not hand-edit.
import time
import math
import random

class PlanAbort(Exception):
    pass

def _load_mouse_pos(ctx, ops):
    sw, sh = (ctx.screen_w, ctx.screen_h)
    for op, prm in ops:
        if op == 'SCREEN':
            sw, sh = prm['v']
            break
    saved = None
    getter = getattr(ctx, 'get_mouse_pos', None)
    if getter is not None:
        try:
            saved = getter()
        except Exception:
            saved = None
    if saved is None or len(saved) < 2:
        return [sw // 2, sh // 2]
    return [_clamp(int(saved[0]), 0, max(0, sw - 1)), _clamp(int(saved[1]), 0, max(0, sh - 1))]

def _save_mouse_pos(ctx, pos):
    setter = getattr(ctx, 'set_mouse_pos', None)
    if setter is not None:
        setter(int(pos[0]), int(pos[1]))

def _below(n):
    return random.randrange(n) if n > 0 else 0

def rand_range(mn, mx):
    if mx < mn:
        mn, mx = (mx, mn)
    if mx <= 0:
        return 0
    return mn if mx <= mn else random.randint(mn, mx)

def _clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v

def pct_dec(s):
    out = []
    i = 0
    while i < len(s):
        if s[i] == '%' and i + 2 < len(s) + 1 and (i + 2 <= len(s) - 1):
            try:
                out.append(chr(int(s[i + 1:i + 3], 16)))
                i += 3
                continue
            except Exception:
                pass
        out.append(s[i])
        i += 1
    return ''.join(out)
_OPS = ('PLAN', 'SCREEN', 'SPEED', 'RMOUSE', 'CLICK', 'TYPE', 'DELAY', 'LOOP', 'LOOPTIME', 'ENDLOOP', 'WLIGHT', 'STATELOOP', 'MOVETO', 'KEY', 'KDOWN', 'KUP', 'WHEEL', 'RAW', 'WSND', 'TRGSND', 'IFSND', 'IFLUX', 'ELSE', 'ENDIF', 'LABEL', 'GOTO', 'INCLUDE', 'RPKG', 'PKGITEM', 'ENDPKG', 'PGROUP', 'PARITEM', 'ENDPAR', 'BEEP')
_V2_OPS = frozenset(_OPS[11:])

def _pair(s, what, line_no):
    parts = s.split(',')
    try:
        a, b = (int(parts[0]), int(parts[1]))
    except Exception:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))
    return (a, b) if b >= a else (b, a)

def _quad(s, what, line_no):
    parts = s.split(',')
    if len(parts) != 4:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))
    try:
        return tuple((int(p) for p in parts))
    except Exception:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))

def _link_blocks(ops):
    stack = []
    labels = {}
    for i, (op, prm) in enumerate(ops):
        if op in ('LOOP', 'LOOPTIME', 'IFSND', 'IFLUX'):
            stack.append(i)
        elif op == 'ENDLOOP':
            ops[stack.pop()][1]['end_ip'] = i
        elif op == 'ELSE':
            ops[stack[-1]][1]['else_line'] = i
        elif op == 'ENDIF':
            sp = ops[stack.pop()][1]
            sp['endif_ip'] = i + 1
            if 'else_line' in sp:
                ops[sp['else_line']][1]['endif_ip'] = i + 1
                sp['else_ip'] = sp['else_line'] + 1
            else:
                sp['else_ip'] = i + 1
        elif op == 'LABEL':
            if prm['name'] in labels:
                raise ValueError("duplicate LABEL '%s'" % prm['name'])
            labels[prm['name']] = i
    for op, prm in ops:
        if op != 'GOTO':
            continue
        lip = labels.get(prm['name'])
        if lip is None:
            raise ValueError("GOTO '%s' has no matching LABEL" % prm['name'])
        gc = prm.get('_chain', ())
        lc = ops[lip][1].get('_chain', ())
        if lc != gc[:len(lc)]:
            raise ValueError("GOTO '%s': the label is not visible from this block" % prm['name'])
_PAR_OK = ('MOVETO', 'CLICK', 'KEY', 'KDOWN', 'KUP', 'WHEEL', 'TYPE', 'DELAY', 'RAW', 'BEEP')
_PKG_MODES = ('pick', 'all', 'seq')

def _extract_containers(text):
    lines = text.split('\n')
    out = []
    table = []
    i = 0
    while i < len(lines):
        head = lines[i].strip()
        kind = head.split('|')[0].upper()
        if kind not in ('RPKG', 'PGROUP'):
            if kind in ('PKGITEM', 'ENDPKG', 'PARITEM', 'ENDPAR'):
                raise ValueError('line %d: %s outside a package/parallel block' % (i + 1, kind))
            out.append(lines[i])
            i += 1
            continue
        sep = 'PKGITEM' if kind == 'RPKG' else 'PARITEM'
        end = 'ENDPKG' if kind == 'RPKG' else 'ENDPAR'
        items = [[]]
        depth = 0
        closed = False
        j = i + 1
        while j < len(lines):
            k = lines[j].strip().split('|')[0].upper()
            if k in ('RPKG', 'PGROUP'):
                depth += 1
            elif k in ('ENDPKG', 'ENDPAR'):
                if depth == 0:
                    if k != end:
                        raise ValueError('line %d: %s closes a %s block' % (j + 1, k, kind))
                    closed = True
                    break
                depth -= 1
            elif k == sep and depth == 0:
                items.append([])
                j += 1
                continue
            items[-1].append(lines[j])
            j += 1
        if not closed:
            raise ValueError('line %d: %s is never closed with %s' % (i + 1, kind, end))
        table.append(['\n'.join(b) for b in items])
        out.append(head + (',' if '|' in head else '|') + '#%d' % (len(table) - 1))
        i = j + 1
    return ('\n'.join(out), table)

def _parse_v3(op, fields, prm, line_no, ctab):
    if op in ('PKGITEM', 'ENDPKG', 'PARITEM', 'ENDPAR'):
        raise ValueError('line %d: stray %s' % (line_no, op))
    body = fields[1] if len(fields) > 1 else ''
    if op == 'BEEP':
        parts = body.split(',')
        if len(parts) != 2:
            raise ValueError('line %d: BEEP needs freq,ms' % line_no)
        try:
            prm['v'] = (int(parts[0]), int(parts[1]))
        except Exception:
            raise ValueError("line %d: bad BEEP '%s'" % (line_no, body))
        if not 30 <= prm['v'][0] <= 20000 or prm['v'][1] < 0:
            raise ValueError('line %d: BEEP out of range (30..20000 Hz)' % line_no)
        return
    parts = body.split(',')
    if not parts[-1].startswith('#'):
        raise ValueError('line %d: %s must open a block body' % (line_no, op))
    bodies = ctab[int(parts[-1][1:])]
    progs = [parse_plan('PLAN|2\n' + b) for b in bodies]
    if op == 'RPKG':
        if len(parts) != 4:
            raise ValueError('line %d: RPKG needs mode,min,max' % line_no)
        mode = parts[0].strip().lower()
        if mode not in _PKG_MODES:
            raise ValueError("line %d: unknown RPKG mode '%s' (pick|all|seq)" % (line_no, parts[0]))
        try:
            mn, mx = (int(parts[1]), int(parts[2]))
        except Exception:
            raise ValueError("line %d: bad RPKG counts '%s'" % (line_no, body))
        if not progs:
            raise ValueError('line %d: RPKG has no items' % line_no)
        if mn < 0 or mx < mn or mx > len(progs):
            raise ValueError('line %d: RPKG counts out of range (%d items)' % (line_no, len(progs)))
        prm['mode'], prm['mn'], prm['mx'], prm['progs'] = (mode, mn, mx, progs)
        return
    if len(parts) != 1:
        raise ValueError('line %d: PGROUP takes no arguments' % line_no)
    if len(progs) < 2:
        raise ValueError('line %d: PGROUP needs at least two branches' % line_no)
    for b in progs:
        for o, _p in b:
            if o != 'PLAN' and o not in _PAR_OK:
                raise ValueError('line %d: %s is not allowed inside PGROUP' % (line_no, o))
    prm['progs'] = progs

def parse_plan(text):
    text, _ctab = _extract_containers(text)
    ops = []
    loop_stack = []
    else_seen = set()
    for line_no, raw in enumerate(text.split('\n'), 1):
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        fields = line.split('|')
        op = fields[0].upper()
        if op not in _OPS:
            raise ValueError("line %d: unknown op '%s'" % (line_no, fields[0]))
        prm = {}
        prm['_chain'] = tuple((b[2] for b in loop_stack))
        if op in ('RPKG', 'PGROUP', 'BEEP', 'PKGITEM', 'ENDPKG', 'PARITEM', 'ENDPAR'):
            _parse_v3(op, fields, prm, line_no, _ctab)
            ops.append((op, prm))
            continue
        if op in ('PLAN', 'SCREEN', 'SPEED', 'DELAY', 'LOOP', 'LOOPTIME'):
            body = fields[1] if len(fields) > 1 else ''
            if op == 'PLAN':
                try:
                    prm['v'] = int(body or '0')
                except Exception:
                    raise ValueError('line %d: bad PLAN version' % line_no)
                if prm['v'] not in (1, 2):
                    raise ValueError('line %d: unsupported PLAN version %d' % (line_no, prm['v']))
            elif op == 'SCREEN':
                parts = body.split(',')
                try:
                    prm['v'] = (int(parts[0]), int(parts[1]))
                except Exception:
                    raise ValueError("line %d: bad SCREEN '%s'" % (line_no, body))
            elif op == 'SPEED':
                prm['v'] = _pair(body, op, line_no)
            elif op == 'DELAY':
                prm['v'] = _pair(body + (',' + body if ',' not in body else ''), op, line_no)
            elif op == 'LOOP':
                prm['n'] = int(body or '1')
                loop_stack.append((op, line_no, len(ops)))
            else:
                prm['sec'] = float(body or '0')
                loop_stack.append((op, line_no, len(ops)))
        elif op == 'ENDLOOP':
            if not loop_stack or loop_stack[-1][0] in ('IFSND', 'IFLUX'):
                raise ValueError('line %d: ENDLOOP without LOOP' % line_no)
            loop_stack.pop()
        elif op == 'ENDIF':
            if not loop_stack or loop_stack[-1][0] not in ('IFSND', 'IFLUX'):
                raise ValueError('line %d: ENDIF without IFSND/IFLUX' % line_no)
            loop_stack.pop()
        elif op == 'ELSE':
            if not loop_stack or loop_stack[-1][0] not in ('IFSND', 'IFLUX'):
                raise ValueError('line %d: ELSE outside an IF block' % line_no)
            if loop_stack[-1][1] in else_seen:
                raise ValueError('line %d: duplicate ELSE in one IF block' % line_no)
            else_seen.add(loop_stack[-1][1])
        elif op == 'STATELOOP':
            vals = {}
            for kv in fields[1:]:
                if '=' not in kv:
                    raise ValueError('line %d: STATELOOP wants key=value' % line_no)
                k, v = kv.split('=', 1)
                vals[k.strip().lower()] = v.strip()
            try:
                prm['poll'] = max(25, int(vals.get('poll', '250')))
                prm['stable'] = max(0, int(vals.get('stable', '750')))
                prm['hysteresis'] = max(0, int(vals.get('hysteresis', '0')))
                prm['timeout'] = max(prm['poll'], int(vals.get('timeout', '1500')))
            except Exception:
                raise ValueError('line %d: bad STATELOOP timing' % line_no)
            raw_routes = vals.get('routes', '')
            routes = []
            for raw_route in raw_routes.split(','):
                bits = raw_route.split(':')
                if len(bits) != 4 or not bits[0] or (not bits[3].endswith('.txt')):
                    raise ValueError("line %d: bad STATELOOP route '%s'" % (line_no, raw_route))
                try:
                    lo, hi = (int(bits[1]), int(bits[2]))
                except Exception:
                    raise ValueError('line %d: bad STATELOOP lux range' % line_no)
                if hi < lo:
                    lo, hi = (hi, lo)
                routes.append({'id': bits[0], 'lo': lo, 'hi': hi, 'file': bits[3]})
            if not routes:
                raise ValueError('line %d: STATELOOP needs routes=' % line_no)
            prm['routes'] = routes
            prm['fallback'] = vals.get('fallback', 'STOP').upper()
            if prm['fallback'] not in ('STOP', 'FIRST'):
                raise ValueError('line %d: STATELOOP fallback must be STOP or FIRST' % line_no)
        elif op in ('WSND', 'TRGSND', 'IFSND', 'IFLUX'):
            pa = fields[1].split(',') if len(fields) > 1 else []
            need = {'WSND': 3, 'IFSND': 3, 'IFLUX': 5, 'TRGSND': 8}[op]
            if len(pa) < need:
                raise ValueError('line %d: %s needs %d comma numbers' % (line_no, op, need))
            try:
                prm['a'] = [int(x) for x in pa[:need]]
            except Exception:
                raise ValueError('line %d: bad %s numbers' % (line_no, op))
            if op in ('IFSND', 'IFLUX'):
                loop_stack.append((op, line_no, len(ops)))
        elif op in ('KDOWN', 'KUP', 'WHEEL'):
            try:
                prm['v'] = int(fields[1]) if len(fields) > 1 else 0
            except Exception:
                raise ValueError('line %d: %s needs a number' % (line_no, op))
            if op != 'WHEEL' and (not 0 < prm['v'] < 256):
                raise ValueError('line %d: %s vk out of range' % (line_no, op))
        elif op in ('LABEL', 'GOTO'):
            nm = fields[1].strip() if len(fields) > 1 else ''
            if not nm or '=' in nm:
                raise ValueError('line %d: %s needs a plain name' % (line_no, op))
            prm['name'] = nm
        elif op == 'RAW':
            prm['line'] = '|'.join(fields[1:]).strip()
            if not prm['line']:
                raise ValueError('line %d: RAW needs a board line' % line_no)
        elif op == 'WLIGHT':
            pos = fields[1].split(',') if len(fields) > 1 else []
            if len(pos) < 5:
                raise ValueError('line %d: WLIGHT needs lo,hi,stable,to,mode' % line_no)
            try:
                prm['lo'], prm['hi'] = (int(pos[0]), int(pos[1]))
                prm['stable'], prm['to'], prm['mode'] = (int(pos[2]), int(pos[3]), int(pos[4]))
            except Exception:
                raise ValueError('line %d: bad WLIGHT numbers' % line_no)
            for kv in fields[2:]:
                if '=' not in kv:
                    raise ValueError("line %d: bad arg '%s'" % (line_no, kv))
                k, v = kv.split('=', 1)
                if k == 'key':
                    t = v.split(',')
                    prm['key'] = (int(t[0]), int(t[1]) if len(t) > 1 else 30, int(t[2]) if len(t) > 2 else 90)
                elif k == 'react':
                    prm['react'] = _pair(v, k, line_no)
                else:
                    raise ValueError("line %d: unknown WLIGHT key '%s'" % (line_no, k))
        else:
            for kv in fields[1:]:
                if '=' not in kv:
                    raise ValueError("line %d: bad arg '%s' (want key=value)" % (line_no, kv))
                k, v = kv.split('=', 1)
                prm[k] = v
            if op == 'RMOUSE':
                if 'region' not in prm:
                    raise ValueError('line %d: RMOUSE needs region=x,y,w,h' % line_no)
                prm['region'] = _quad(prm['region'], 'region', line_no)
                for k in ('mt', 'curve', 'before', 'after'):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if 'mid' in prm:
                    c, _, r = prm['mid'].partition(':')
                    prm['mid'] = (int(c), _pair(r, 'mid', line_no))
                if 'idle' in prm:
                    e, _, r = prm['idle'].partition(':')
                    prm['idle'] = (_pair(e, 'idle-every', line_no), _pair(r, 'idle-pause', line_no))
                if 'over' in prm:
                    prm['over'] = int(prm['over'])
            elif op == 'CLICK':
                prm['btn'] = prm.get('btn', 'left')
                prm['n'] = int(prm.get('n', '1'))
                if 'hold' in prm:
                    prm['hold'] = _pair(prm['hold'], 'hold', line_no)
            elif op == 'TYPE':
                if 'text' not in prm:
                    raise ValueError('line %d: TYPE needs text=' % line_no)
                prm['text'] = pct_dec(prm['text'])
                for k in ('h', 'w', 'p', 'typo', 'typos'):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if 'think' in prm:
                    c, _, r = prm['think'].partition(':')
                    prm['think'] = (int(c), _pair(r, 'think', line_no))
                if 'wp' in prm:
                    prm['wp'] = int(prm['wp'])
            elif op == 'MOVETO':
                if 'x' not in prm or 'y' not in prm:
                    raise ValueError('line %d: MOVETO needs x= and y=' % line_no)
                try:
                    prm['x'] = int(prm['x'])
                    prm['y'] = int(prm['y'])
                    prm['human'] = int(prm.get('human', '1'))
                except Exception:
                    raise ValueError('line %d: bad MOVETO numbers' % line_no)
                for k in ('mt', 'curve', 'before', 'after'):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if 'mid' in prm:
                    c, _, r = prm['mid'].partition(':')
                    prm['mid'] = (int(c), _pair(r, 'mid', line_no))
                if 'idle' in prm:
                    e, _, r = prm['idle'].partition(':')
                    prm['idle'] = (_pair(e, 'idle-every', line_no), _pair(r, 'idle-pause', line_no))
                if 'over' in prm:
                    prm['over'] = int(prm['over'])
            elif op == 'KEY':
                if 'combo' not in prm:
                    raise ValueError('line %d: KEY needs combo=vk+vk' % line_no)
                try:
                    prm['combo'] = [int(v) for v in prm['combo'].split('+') if v != '']
                except Exception:
                    raise ValueError('line %d: bad KEY combo' % line_no)
                if not prm['combo'] or any((v <= 0 or v > 255 for v in prm['combo'])):
                    raise ValueError('line %d: bad KEY combo' % line_no)
                if 'hold' in prm:
                    prm['hold'] = _pair(prm['hold'], 'hold', line_no)
            elif op == 'INCLUDE':
                nm = prm.get('file', '')
                if not nm or any((ch in nm for ch in '/\\:')) or (not nm.lower().endswith('.txt')):
                    raise ValueError('line %d: INCLUDE needs a plain *.txt file name' % line_no)
        ops.append((op, prm))
    if loop_stack:
        _k, _ln, _ = loop_stack[-1]
        raise ValueError('line %d: %s without %s' % (_ln, _k, 'ENDIF' if _k in ('IFSND', 'IFLUX') else 'ENDLOOP'))
    if not ops or ops[0][0] != 'PLAN':
        raise ValueError('plan must start with PLAN|1 or PLAN|2')
    _link_blocks(ops)
    return ops

class PausePlanner:

    def __init__(self):
        self.moves_since = 0
        self.next_idle_at = -1

    def mid_pause(self, c):
        if c['mid_chance'] > 0 and _below(100) < c['mid_chance']:
            return rand_range(c['mid_min'], c['mid_max'])
        return 0

    def roll_long(self, c):
        if c['idle_pause_max'] <= 0 or c['idle_every_max'] <= 0:
            return 0
        self.moves_since += 1
        if self.next_idle_at < 0:
            self.next_idle_at = max(1, rand_range(c['idle_every_min'], c['idle_every_max']))
        if self.moves_since < self.next_idle_at:
            return 0
        self.moves_since = 0
        self.next_idle_at = max(1, rand_range(c['idle_every_min'], c['idle_every_max']))
        return rand_range(c['idle_pause_min'], c['idle_pause_max'])

class _LightStateChanged(Exception):

    def __init__(self, state_id):
        self.state_id = state_id

class _LiveLightSession:

    def __init__(self, prm, ctx):
        try:
            from live_light_guard import LightStateGuard, state_spec
        except ImportError as exc:
            raise ValueError('STATELOOP needs live_light_guard.py in the portable bundle') from exc
        self.ctx = ctx
        self.prm = prm
        self.routes = {r['id']: r for r in prm['routes']}
        specs = [state_spec(r['id'], r['lo'], r['hi'], r['file']) for r in prm['routes']]
        self.guard = LightStateGuard(specs, prm['stable'], prm['hysteresis'], prm['timeout'])
        self.current = None
        self.next_poll = -1

    def _lux(self):
        reader = getattr(self.ctx, 'read_lux', None)
        if reader is None:
            reader = getattr(self.ctx, 'light_lux', None)
        if reader is not None:
            try:
                value = reader()
                return None if value is None else float(value)
            except Exception:
                return None
        waiter = getattr(self.ctx, 'wait_light', None)
        if waiter is None:
            return None
        for route in self.prm['routes']:
            try:
                if waiter(route['lo'], route['hi'], 0, self.prm['poll'], 0):
                    return (route['lo'] + route['hi']) / 2.0
            except Exception:
                return None
        return None

    def poll(self, force=False):
        now = int(self.ctx.now() * 1000)
        if not force and self.next_poll > now:
            return
        self.next_poll = now + self.prm['poll']
        state_id = self.guard.update(self._lux(), now)
        if state_id is None:
            if self.current is not None:
                self.ctx.log('light guard unsafe - stopping')
            raise PlanAbort()
        if self.current is None:
            self.current = state_id
            self.ctx.log('light state -> ' + state_id)
        elif state_id != self.current:
            old = self.current
            self.current = state_id
            self.ctx.log('light state %s -> %s' % (old, state_id))
            raise _LightStateChanged(state_id)

def _run_state_loop(prm, ctx, pos, pauses, inc):
    session = _LiveLightSession(prm, ctx)
    session.poll(True)
    while True:
        route = session.routes.get(session.current)
        if route is None:
            if prm['fallback'] == 'FIRST':
                route = prm['routes'][0]
                session.current = route['id']
            else:
                raise PlanAbort()
        try:
            sub = parse_plan(ctx.read_plan_file(route['file']))
        except Exception as exc:
            ctx.log('light route failed: ' + str(exc))
            raise PlanAbort()
        setattr(ctx, '_live_light_guard', session)
        try:
            run_plan(sub, ctx, _pos=pos, _pauses=pauses, _inc=inc + (route['file'],))
        except _LightStateChanged:
            continue
        finally:
            if getattr(ctx, '_live_light_guard', None) is session:
                delattr(ctx, '_live_light_guard')
        session.poll(True)

def run_plan(ops, ctx, _pos=None, _pauses=None, _inc=()):
    if any((o in _V2_OPS for o, _ in ops)) and getattr(ctx, 'plan_api', 1) < 2:
        raise ValueError('this plan uses v2 ops but the firmware ctx is plan_api 1 - flash code65+')
    pauses = _pauses if _pauses is not None else PausePlanner()
    pos = _pos if _pos is not None else _load_mouse_pos(ctx, ops)
    labels = {}
    for _li, (_o, _p) in enumerate(ops):
        if _o == 'LABEL':
            labels[_p['name']] = _li
    inc = tuple(_inc)
    i = 0
    stack = []
    while i < len(ops):
        op, prm = ops[i]
        _gate = getattr(ctx, 'gate', None)
        if _gate is not None and (not _gate()):
            raise PlanAbort()
        _live_guard = getattr(ctx, '_live_light_guard', None)
        if _live_guard is not None:
            _live_guard.poll()
        if op == 'PLAN':
            pass
        elif op == 'SCREEN':
            ctx.screen_w, ctx.screen_h = prm['v']
            _sr = getattr(ctx, 'setres', None)
            if _sr is not None:
                _sr(prm['v'][0], prm['v'][1])
            pos[0] = _clamp(pos[0], 0, max(0, ctx.screen_w - 1))
            pos[1] = _clamp(pos[1], 0, max(0, ctx.screen_h - 1))
            _save_mouse_pos(ctx, pos)
        elif op == 'SPEED':
            ctx.speed_min, ctx.speed_max = prm['v']
        elif op == 'DELAY':
            if not ctx.sleep_ms(rand_range(*prm['v'])):
                raise PlanAbort()
        elif op == 'LOOP':
            stack.append([i, prm['n'], None])
        elif op == 'LOOPTIME':
            stack.append([i, 0, ctx.now() + prm['sec']])
        elif op == 'ENDLOOP':
            top = stack[-1]
            if top[2] is not None:
                if ctx.now() < top[2]:
                    i = top[0]
                else:
                    stack.pop()
            elif top[1] == 0:
                i = top[0]
            else:
                top[1] -= 1
                if top[1] > 0:
                    i = top[0]
                else:
                    stack.pop()
        elif op == 'RMOUSE':
            _exec_rmouse(prm, ctx, pauses, pos)
        elif op == 'CLICK':
            hold = prm.get('hold', (0, 0))
            ctx.mclick(prm['btn'], prm['n'], hold[0], hold[1])
        elif op == 'TYPE':
            for cmd in plan_typing(prm['text'], prm):
                if cmd[0] == 'KTEXT':
                    ctx.ktext(cmd[1], cmd[2], cmd[3])
                elif cmd[0] == 'DLY':
                    if not ctx.sleep_ms(cmd[1]):
                        raise PlanAbort()
                else:
                    ctx.kcombo(cmd[1])
        elif op == 'STATELOOP':
            _run_state_loop(prm, ctx, pos, pauses, inc)
        elif op == 'WLIGHT':
            ok = ctx.wait_light(prm['lo'], prm['hi'], prm['stable'], prm['to'], prm['mode'])
            if ok and 'key' in prm:
                vk, hmn, hmx = prm['key']
                rmn, rmx = prm.get('react', (80, 180))
                if not ctx.sleep_ms(rand_range(rmn, rmx)):
                    raise PlanAbort()
                ctx.key(vk, rand_range(hmn, hmx))
            ctx.log('wlight ' + ('match' if ok else 'timeout'))
        elif op == 'WSND':
            r = ctx.wait_sound(prm['a'][0], prm['a'][1], prm['a'][2])
            if r is None:
                raise PlanAbort()
            ctx.log('wsnd ' + ('heard' if r else 'timeout - continue'))
        elif op == 'IFSND':
            r = ctx.wait_sound(prm['a'][0], prm['a'][1], prm['a'][2])
            if r is None:
                raise PlanAbort()
            ctx.log('ifsnd ' + ('heard -> then' if r else 'timeout -> else'))
            if not r:
                i = prm['else_ip']
                continue
        elif op == 'TRGSND':
            a = prm['a']
            r = ctx.trg_sound(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7])
            if r is None:
                raise PlanAbort()
            ctx.log('trgsnd ' + ('fired' if r else 'timeout - continue'))
        elif op == 'IFLUX':
            ok2 = ctx.wait_light(prm['a'][0], prm['a'][1], prm['a'][2], prm['a'][3], prm['a'][4])
            ctx.log('iflux ' + ('match -> then' if ok2 else 'timeout -> else'))
            if not ok2:
                i = prm['else_ip']
                continue
        elif op == 'ELSE':
            i = prm['endif_ip']
            continue
        elif op == 'ENDIF':
            pass
        elif op == 'MOVETO':
            _exec_rmouse(prm, ctx, pauses, pos, (prm['x'], prm['y']))
        elif op == 'KEY':
            hold = prm.get('hold', (0, 0))
            ctx.key_combo(prm['combo'], hold[0], hold[1])
        elif op == 'KDOWN':
            ctx.kdown(prm['v'])
        elif op == 'KUP':
            ctx.kup(prm['v'])
        elif op == 'WHEEL':
            ctx.wheel(prm['v'])
        elif op == 'RAW':
            if ctx.raw(prm['line']) is None:
                raise PlanAbort()
        elif op == 'LABEL':
            pass
        elif op == 'GOTO':
            lip = labels.get(prm['name'])
            if lip is None:
                ctx.log('goto: label not found: ' + prm['name'] + ' - plan stopped')
                return
            while stack and (not stack[-1][0] < lip < ops[stack[-1][0]][1].get('end_ip', 1 << 30)):
                stack.pop()
            i = lip
            continue
        elif op == 'RPKG':
            progs = prm['progs']
            order = list(range(len(progs)))
            if prm['mode'] != 'seq':
                for _k in range(len(order) - 1, 0, -1):
                    _j = _below(_k + 1)
                    order[_k], order[_j] = (order[_j], order[_k])
                if prm['mode'] == 'pick':
                    order = order[:rand_range(prm['mn'], prm['mx'])]
            ctx.log('package: %d of %d' % (len(order), len(progs)))
            for _ix in order:
                run_plan(progs[_ix], ctx, _pos=pos, _pauses=pauses, _inc=inc)
        elif op == 'PGROUP':
            _rr = [[o for o in p if o[0] != 'PLAN'] for p in prm['progs']]
            while True:
                _live = False
                for _b in _rr:
                    if _b:
                        _live = True
                        run_plan([_b.pop(0)], ctx, _pos=pos, _pauses=pauses, _inc=inc)
                if not _live:
                    break
        elif op == 'BEEP':
            _bp = getattr(ctx, 'beep', None)
            if _bp is None:
                raise ValueError('BEEP needs a plan_api>=3 firmware (buzzer pin)')
            _bp(prm['v'][0], prm['v'][1])
        elif op == 'INCLUDE':
            nm = prm['file']
            if nm in inc:
                ctx.log('include cycle: ' + nm + ' - skipped')
            elif len(inc) >= 4:
                ctx.log('include depth cap (4): ' + nm + ' - skipped')
            else:
                try:
                    sub = parse_plan(ctx.read_plan_file(nm))
                except Exception as exc:
                    ctx.log('include failed: ' + nm + ': ' + str(exc))
                else:
                    run_plan(sub, ctx, _pos=pos, _pauses=pauses, _inc=inc + (nm,))
        i += 1
_motion_module = None
_typing_module = None

def _exec_rmouse(prm, ctx, pauses, pos, target=None):
    global _motion_module
    if _motion_module is None:
        import plan_motion as _motion_module
        _motion_module.PlanAbort = PlanAbort
    return _motion_module._exec_rmouse(prm, ctx, pauses, pos, target)

def plan_typing(text, prm):
    global _typing_module
    if _typing_module is None:
        import plan_typing as _typing_module
    return _typing_module.plan_typing(text, prm)
