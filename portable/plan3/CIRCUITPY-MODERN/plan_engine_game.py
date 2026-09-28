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


def run_game(commands, ctx):
    core, runtime = _load(ctx)
    return runtime.run_game(commands, ctx, core)


def _file_needs_parallel(name):
    with open("/" + name, "r") as route:
        for raw in route:
            line = raw.strip()
            if line and not line.startswith("#") and line.split("|", 1)[0].upper() == "PGROUP":
                return True
    return False


def run_game_file(name, ctx):
    needs_parallel = _file_needs_parallel(name)
    gc.collect()
    core, runtime = _load(ctx)
    if needs_parallel:
        # Compile the scheduler while the heap is still fresh. Waiting until
        # PGROUP leaves enough total bytes but may fragment the largest block.
        gc.collect(); _heap(ctx, "before-parallel-preload"); gc.collect()
        try:
            __import__("plan_engine_game_parallel")
        except MemoryError:
            _heap(ctx, "parallel-preload-memoryerror")
            raise
        gc.collect(); _heap(ctx, "after-parallel-preload")
    commands = core._FileCommands(name)
    try:
        gc.collect()
        _heap(ctx, "file-index|commands=%d|offset-bytes=%d" %
              (len(commands), len(commands.offsets)))
        return runtime.run_game(commands, ctx, core)
    finally:
        commands.close()
