import gc
_core=None; _response_run=None; _sound_support=None; _actions=None
def _event_module():
    import plan_engine_game_events as module
    module.bind(_core); return module
def _events(commands, start, end, ctx, state): return _event_module().events(commands, start, end, ctx, state)
def _prepare_response(ctx):
    global _response_run
    if _response_run is not None: return
    _core._emit_heap(ctx, "before-response-bind")
    import plan_engine_game_response as module
    module.bind(_core)
    _response_run = module.run
    _core._emit_heap(ctx, "after-response-bind")
def _run_response(ctx, name, state):
    if _response_run is None: _prepare_response(ctx)
    return _response_run(ctx, name, state, _run)
def _sound_module():
    global _sound_support
    if _sound_support is None:
        import plan_engine_game_sound as module
        module.bind(_core); _sound_support = module
    return _sound_support
def _resolve_sound_watch(ctx): return _sound_module().resolve_sound_watch(ctx)
def _service_pending_response(ctx, state): return _sound_module().service_pending_response(ctx, state, _run_response)
def service_sound_exit(ctx, signal): return _sound_module().service_sound_exit(ctx, signal, _run_response)
def _action_module():
    global _actions
    if _actions is None:
        import plan_engine_game_actions as module
        _actions = module
    _actions.bind(_core, _run_response, _service_pending_response)
    return _actions
def _parallel(commands, start, end, ctx, state):
    gc.collect(); _core._emit_heap(ctx, "before-parallel-import"); gc.collect()
    try:
        import plan_engine_game_parallel as parallel
    except MemoryError:
        _core._emit_heap(ctx, "parallel-import-memoryerror")
        raise
    gc.collect(); _core._emit_heap(ctx, "after-parallel-import")
    return parallel.run(commands, start, end, ctx, state, _core,
                        _events, _response_run, _run, _resolve_sound_watch)
def _run(commands, start, end, ctx, state, labels, cursor=None):
    if cursor is None:
        cursor = _event_module().Cursor(commands, start, end, ctx)
    while True:
        item = cursor.next()
        if item is None: return
        op, args = item[0], item[1]
        if _action_module().leaf(op, args, ctx, state):
            pass
        elif op == "PGROUP":
            signal = _parallel(commands, item[2], item[3], ctx, state)
            if signal is not None:
                return _event_module().exit_signal(ctx, cursor, labels, signal)
        elif op == "LABEL":
            pass
        elif op == "GOTO":
            target = labels.get(args)
            if target is None: raise ValueError("GOTO label not found")
            cursor.jump(target)
        else:
            raise ValueError("unsupported Game command " + op)
        if state["watch"] and ctx.r.arm.sound_result is True:
            return _event_module().exit_signal(ctx, cursor, labels)
        _service_pending_response(ctx, state)
def run_game(commands, ctx, core, resume=None):
    global _core
    _core = core
    _prepare_response(ctx)
    labels, cursor, state = _event_module().session(commands, ctx, resume)
    gc.collect()
    result = None
    try:
        result = _run(commands, 0, len(commands), ctx, state, labels, cursor)
        return result
    except RuntimeError as exc:
        if str(exc) == "route aborted": _core._abort()
        raise
    finally:
        if result is None:
            state.clear(); gc.collect()
