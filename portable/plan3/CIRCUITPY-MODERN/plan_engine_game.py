"""Tiny Game facade: imports core and runtime sequentially to cap RP2040 peaks."""
import gc
import plan_engine_game_inventory as inventory

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
    return inventory.scan(name)

def _run_game_file(name, ctx, resume):
    gc.collect(); _heap(ctx, "before-file-index-reserve"); gc.collect()
    needs_parallel, needs_type, offsets = _file_inventory(name)
    gc.collect(); _heap(ctx, "after-file-index-reserve|commands=%d|offset-bytes=%d" %
                       (len(offsets) // 4, len(offsets)))
    if needs_parallel:
        for module, stage in (("plan_engine_game_sound", "sound"),
                              ("plan_engine_game_parallel", "parallel"),
                              ("plan_engine_game_events", "events"),
                              ("plan_engine_game_response", "response"),
                              ("plan_engine_game_actions", "actions")):
            gc.collect(); _heap(ctx, "before-" + stage + "-preload"); gc.collect()
            try:
                __import__(module)
            except MemoryError:
                _heap(ctx, stage + "-preload-memoryerror")
                raise
            gc.collect(); _heap(ctx, "after-" + stage + "-preload")
    if needs_type:
        inventory.preload_type(ctx, _heap)
    core, runtime = _load(ctx)
    commands = core._FileCommands(name, offsets)
    gc.collect()
    _heap(ctx, "file-index|commands=%d|offset-bytes=%d" %
          (len(commands), len(commands.offsets)))
    try:
        signal = runtime.run_game(commands, ctx, core, resume)
        if signal is not None:
            signal["_game_cursor"].commands = None
        return signal
    finally:
        commands.close()

def run_game_file(name, ctx):
    # finally: commands.close()
    return _run_game_file(name, ctx, None)

def resume_game_file(name, ctx, resume):
    return _run_game_file(name, ctx, resume)
