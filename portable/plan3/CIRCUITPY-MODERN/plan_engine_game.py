"""Tiny Game facade: imports core and runtime sequentially to cap RP2040 peaks."""
import gc

GameAbort = RuntimeError


def _heap(ctx, stage):
    emit = getattr(getattr(ctx, "r", None), "emit", None)
    if emit is not None:
        emit("EVT|DEBUG|GAME|stage=%s|free=%d" % (stage, getattr(gc, "mem_free", lambda: -1)()))


def _load(ctx):
    global GameAbort
    gc.collect(); _heap(ctx, "before-core-import"); gc.collect()
    try:
        import plan_engine_game_core as core
    except MemoryError:
        _heap(ctx, "core-import-memoryerror")
        raise
    GameAbort = core.GameAbort
    gc.collect(); _heap(ctx, "after-core-import"); gc.collect()
    try:
        import plan_engine_game_runtime as runtime
    except MemoryError:
        _heap(ctx, "runtime-import-memoryerror")
        raise
    gc.collect(); _heap(ctx, "after-runtime-import")
    return core, runtime


def run_game(commands, ctx, resume=None):
    core, runtime = _load(ctx)
    return runtime.run_game(commands, ctx, core, resume)


def service_sound_exit(ctx, signal):
    import plan_engine_game_runtime as runtime
    return runtime.service_sound_exit(ctx, signal)


def _file_inventory(name):
    offsets = bytearray()
    needs_parallel = False
    with open("/" + name, "r") as route:
        while True:
            offset = route.tell()
            raw = route.readline()
            if not raw:
                break
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            op = line.split("|", 1)[0].upper()
            if op == "PGROUP":
                needs_parallel = True
            for shift in (0, 8, 16, 24):
                offsets.append((offset >> shift) & 255)
    return needs_parallel, offsets


def _run_game_file(name, ctx, resume):
    # Reserve the contiguous Flash index before compiler fragmentation.
    gc.collect(); _heap(ctx, "before-file-index-reserve"); gc.collect()
    needs_parallel, offsets = _file_inventory(name)
    gc.collect(); _heap(ctx, "after-file-index-reserve|commands=%d|offset-bytes=%d" %
                       (len(offsets) // 4, len(offsets)))
    if needs_parallel:
        # Compile bounded Parallel units before Core/Runtime fragmentation.
        for module, stage in (("plan_engine_game_sound", "sound"),
                              ("plan_engine_game_parallel", "parallel"),
                              ("plan_engine_game_events", "events"),
                              ("plan_engine_game_response", "response")):
            gc.collect(); _heap(ctx, "before-" + stage + "-preload"); gc.collect()
            try:
                __import__(module)
            except MemoryError:
                _heap(ctx, stage + "-preload-memoryerror")
                raise
            gc.collect(); _heap(ctx, "after-" + stage + "-preload")
    core, runtime = _load(ctx)
    commands = core._FileCommands(name, offsets)
    gc.collect()
    _heap(ctx, "file-index|commands=%d|offset-bytes=%d" %
          (len(commands), len(commands.offsets)))
    try:
        signal = runtime.run_game(commands, ctx, core, resume)
        if signal is not None:
            # Do not retain the closed Flash handle through the response.  The
            # cursor is rebound to the freshly opened file on resume.
            signal["_game_cursor"].commands = None
        return signal
    finally:
        # A SoundWatch unwind no longer needs the source file/index.  Closing
        # it deterministically prevents one open file and 1.5 KB index from
        # surviving until the next cast's garbage collection.
        commands.close()


def run_game_file(name, ctx):
    # The shared worker retains the original ownership contract:
    # `finally:` always executes `commands.close()`.
    return _run_game_file(name, ctx, None)


def resume_game_file(name, ctx, resume):
    return _run_game_file(name, ctx, resume)
