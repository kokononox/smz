"""Tiny Login/DC facade with sequential Core, Mouse and Type imports."""
import gc

_core_module = None
_mouse_module = None
_type_module = None


def _heap(ctx, stage):
    emit = getattr(getattr(ctx, "r", None), "emit", None)
    if emit is not None:
        emit("EVT|DEBUG|LOGIN|stage=%s|free=%d" %
             (stage, getattr(gc, "mem_free", lambda: -1)()))


def _core(ctx=None):
    global _core_module
    if _core_module is None:
        gc.collect(); _heap(ctx, "before-core-import"); gc.collect()
        try:
            import plan_engine_login_core as core
            _core_module = core
        except MemoryError:
            _heap(ctx, "core-import-memoryerror")
            raise
        gc.collect(); _heap(ctx, "after-core-import")
    return _core_module


def _mouse(ctx):
    global _mouse_module
    core = _core(ctx)
    if _mouse_module is None:
        gc.collect(); _heap(ctx, "before-mouse-runtime-import"); gc.collect()
        try:
            import plan_engine_login_mouse as mouse
            _mouse_module = mouse
        except MemoryError:
            _heap(ctx, "mouse-runtime-import-memoryerror")
            raise
        _mouse_module.bind(core)
        gc.collect(); _heap(ctx, "after-mouse-runtime-import")
    return _mouse_module


def _typing(ctx):
    global _type_module
    core = _core(ctx)
    if _type_module is None:
        gc.collect(); _heap(ctx, "before-type-import"); gc.collect()
        try:
            import plan_engine_login_type as typing
            _type_module = typing
        except MemoryError:
            _heap(ctx, "type-import-memoryerror")
            raise
        _type_module.bind(core)
        gc.collect(); _heap(ctx, "after-type-import")
    return _type_module


def PausePlanner(ctx=None):
    return _core(ctx).PausePlanner()


def rand_range(mn, mx):
    return _core().rand_range(mn, mx)


def pct_dec(value):
    return _core().pct_dec(value)


def distance_scaled_move_ms(mn, mx, distance):
    return _core().distance_scaled_move_ms(mn, mx, distance)


def mouse_events(args, ctx, pauses, pos, route_speed):
    return _mouse(ctx).mouse_events(args, ctx, pauses, pos, route_speed)


def run_rmouse(args, ctx, pauses, pos, route_speed):
    return _mouse(ctx).run_rmouse(args, ctx, pauses, pos, route_speed)


def run_type(args, ctx):
    return _typing(ctx).run_type(args, ctx)
