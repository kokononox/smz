# Generated from canonical plan_engine.py; do not hand-edit.
import time
import math
import random

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
_QWERTY_ROWS = ('1234567890', 'qwertyuiop', 'asdfghjkl', 'zxcvbnm')
_PUNCT = '.,!?;:'

def _qwerty_neighbor(ch):
    lower = ch.lower()
    for row in _QWERTY_ROWS:
        i = row.find(lower)
        if i < 0:
            continue
        j = i + (-1 if _below(2) == 0 else 1)
        if j < 0 or j >= len(row):
            j = 1 if i == 0 else i - 1
        n = row[j]
        return n.upper() if ch.isupper() else n
    return None

def _split_punct(s):
    outp = []
    start = 0
    for i in range(len(s) - 1):
        if s[i] in _PUNCT:
            outp.append(s[start:i + 1])
            start = i + 1
    if start < len(s):
        outp.append(s[start:])
    if not outp and s:
        outp.append(s)
    return outp

def plan_typing(text, p):
    hmin, hmax = p.get('h', (80, 220))
    wmin, wmax = p.get('w', (0, 0))
    wp = _clamp(p.get('wp', 100), 0, 100)
    pmin, pmax = p.get('p', (0, 0))
    think_chance, think_range = p.get('think', (0, (800, 2200)))
    think_chance = _clamp(think_chance, 0, 100)
    think_min, think_max = think_range
    typo_count_min, typo_count_max = p.get('typos', (0, 0))
    if typo_count_max < typo_count_min:
        typo_count_min, typo_count_max = typo_count_max, typo_count_min
    typo_count_min = max(0, typo_count_min)
    typo_count_max = max(0, typo_count_max)
    typo_count_mode = typo_count_max > 0
    typo_char_min, typo_char_max = p.get('typochars', (0, 0))
    if typo_char_max < typo_char_min:
        typo_char_min, typo_char_max = typo_char_max, typo_char_min
    typo_char_min = max(1, typo_char_min)
    typo_char_max = max(0, typo_char_max)
    typo_char_mode = typo_char_max > 0
    typo_min, typo_max = p.get('typo', (0, 0))
    typo_cadence = not typo_char_mode and not typo_count_mode and typo_max > 0
    next_typo_at = max(1, rand_range(typo_min, typo_max)) if typo_cadence else -1
    words_since_typo = 0
    word_mode = (wmax > 0 or pmax > 0 or think_chance > 0 or
                 typo_char_mode or typo_count_mode or typo_cadence)
    cmds = []
    lines = text.replace('\r\n', '\n').replace('\r', '\n').split('\n')
    line_words = [[wd for wd in line.split() if wd] for line in lines]
    pending = []
    typo_positions = {}

    if typo_char_mode:
        candidates = []
        for li, words in enumerate(line_words):
            for wi, word in enumerate(words):
                for pos, ch in enumerate(word):
                    if ch.lower() in '1234567890qwertyuiopasdfghjklzxcvbnm':
                        candidates.append((li, wi, pos))
        cursor = rand_range(typo_char_min, typo_char_max) - 1
        while cursor < len(candidates):
            li, wi, pos = candidates[cursor]
            typo_positions.setdefault((li, wi), []).append(pos)
            cursor += rand_range(typo_char_min, typo_char_max)
    elif typo_count_mode:
        candidates = []
        for li, words in enumerate(line_words):
            for wi, word in enumerate(words):
                for pos, ch in enumerate(word):
                    if ch.lower() in '1234567890qwertyuiopasdfghjklzxcvbnm':
                        candidates.append((li, wi, pos))
        wanted = min(len(candidates), rand_range(typo_count_min, typo_count_max))
        for i in range(wanted):
            j = i + _below(len(candidates) - i)
            candidates[i], candidates[j] = candidates[j], candidates[i]
            li, wi, pos = candidates[i]
            typo_positions.setdefault((li, wi), []).append(pos)
        for positions in typo_positions.values():
            positions.sort()

    def flush():
        s = ''.join(pending)
        pending.clear()
        for i in range(0, len(s), 60):
            cmds.append(('KTEXT', hmin, hmax, s[i:i + 60]))
    for li, line in enumerate(lines):
        if word_mode:
            words = line_words[li]
            for wi, word in enumerate(words):
                tail = ' ' if wi < len(words) - 1 else ''
                typed = word + tail
                selected = typo_positions.get((li, wi), ())
                if selected:
                    start = 0
                    for pos in selected:
                        wrong = _qwerty_neighbor(word[pos])
                        if wrong is None:
                            continue
                        flush()
                        slip = word[start:pos] + wrong
                        for ci in range(0, len(slip), 60):
                            cmds.append(('KTEXT', hmin, hmax, slip[ci:ci + 60]))
                        cmds.append(('DLY', rand_range(max(hmax, 120), hmax * 2 + 200)))
                        cmds.append(('KCOMBO', 8))
                        cmds.append(('DLY', rand_range(hmin, hmax)))
                        start = pos
                    typed = word[start:] + tail
                else:
                    typo_due = typo_cadence and (words_since_typo := (words_since_typo + 1)) >= next_typo_at
                    if typo_due and 2 <= len(word) <= 60:
                        pos = 1 + _below(len(word) - 1)
                        wrong = _qwerty_neighbor(word[pos])
                        if wrong is not None:
                            flush()
                            cmds.append(('KTEXT', hmin, hmax, word[:pos] + wrong))
                            cmds.append(('DLY', rand_range(max(hmax, 120), hmax * 2 + 200)))
                            cmds.append(('KCOMBO', 8))
                            cmds.append(('DLY', rand_range(hmin, hmax)))
                            typed = word[pos:] + tail
                            words_since_typo = 0
                            next_typo_at = max(1, rand_range(typo_min, typo_max))
                segs = _split_punct(typed) if pmax > 0 else [typed]
                for si, seg in enumerate(segs):
                    pending.append(seg)
                    if si < len(segs) - 1:
                        flush()
                        cmds.append(('DLY', rand_range(pmin, pmax)))
                if wi < len(words) - 1:
                    if wmax > 0 and _below(100) < wp:
                        flush()
                        cmds.append(('DLY', rand_range(wmin, wmax)))
                    if think_chance > 0 and think_max > 0 and (_below(100) < think_chance):
                        flush()
                        cmds.append(('DLY', rand_range(think_min, think_max)))
            flush()
        else:
            for i in range(0, len(line), 60):
                cmds.append(('KTEXT', hmin, hmax, line[i:i + 60]))
        if li < len(lines) - 1:
            cmds.append(('KCOMBO', 13))
    return cmds
