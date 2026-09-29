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


def run_game_file(name, ctx):
    # Reserve the contiguous Flash index before compiler fragmentation.
    gc.collect(); _heap(ctx, "before-file-index-reserve"); gc.collect()
    needs_parallel, offsets = _file_inventory(name)
    gc.collect(); _heap(ctx, "after-file-index-reserve|commands=%d|offset-bytes=%d" %
                       (len(offsets) // 4, len(offsets)))
    if needs_parallel:
        # Compile the scheduler before Core/Runtime.  Bundle 381 proved that
        # retaining the reserved index is safe, but importing Core/Runtime
        # first fragments the last 1388-byte compiler block.
        gc.collect(); _heap(ctx, "before-parallel-preload"); gc.collect()
        try:
            __import__("plan_engine_game_parallel")
        except MemoryError:
            _heap(ctx, "parallel-preload-memoryerror")
            raise
        gc.collect(); _heap(ctx, "after-parallel-preload")
    core, runtime = _load(ctx)
    commands = core._FileCommands(name, offsets)
    try:
        gc.collect()
        _heap(ctx, "file-index|commands=%d|offset-bytes=%d" %
              (len(commands), len(commands.offsets)))
        return runtime.run_game(commands, ctx, core)
    finally:
        commands.close()
