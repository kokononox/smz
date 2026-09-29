"""Flash-backed Splash/Whisper response runner, preloaded before Game fragmentation."""
import gc

_core = None

def bind(core):
    global _core
    _core = core


def _response_commands(ctx, name):
    if not name.endswith(".txt") or "/" in name or "\\" in name:
        raise ValueError("unsafe sound response route")
    commands = []
    allowed = ("PLAN", "SCREEN", "SPEED", "DELAY", "KEY", "KDOWN", "KUP",
               "WHEEL", "RAW", "RMOUSE", "TYPE", "RPKG", "PKGITEM", "ENDPKG",
               "LOOP", "LOOPTIME", "ENDLOOP", "BEEP", "LABEL", "GOTO")
    for raw in ctx.read_plan_file(name).splitlines():
        line = raw.strip()
        if not line or line.startswith("#"): continue
        parts = line.split("|", 1)
        op = parts[0].upper(); args = parts[1] if len(parts) == 2 else ""
        if op not in allowed: raise ValueError("unsupported sound response " + op)
        commands.append((op, args))
    return commands


def run(ctx, name, state, execute):
    if not name.endswith(".txt") or "/" in name or "\\" in name:
        raise ValueError("unsafe sound response route")
    file_backed = False
    _core._emit_heap(ctx, "before-response-file")
    try:
        try:
            commands = _core._FileCommands(name)
            file_backed = True
        except OSError:
            # Host simulations provide virtual response files through Context.
            commands = _response_commands(ctx, name)
        labels = {}
        allowed = ("PLAN", "SCREEN", "SPEED", "DELAY", "KEY", "KDOWN", "KUP",
                   "WHEEL", "RAW", "RMOUSE", "TYPE", "RPKG", "PKGITEM", "ENDPKG",
                   "LOOP", "LOOPTIME", "ENDLOOP", "BEEP", "LABEL", "GOTO")
        for index, item in enumerate(commands):
            if item[0] not in allowed:
                raise ValueError("unsupported sound response " + item[0])
            if item[0] == "LABEL": labels[item[1]] = index
        _core._emit_heap(ctx, "after-response-index|commands=%d" % len(commands))
        ctx.log("sound response start " + name)
        execute(commands, 0, len(commands), ctx, state, labels)
        ctx.log("sound response done " + name)
    finally:
        if file_backed:
            commands.close()
        gc.collect()

