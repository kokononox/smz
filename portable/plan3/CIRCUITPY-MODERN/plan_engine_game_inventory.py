"""Route inventory and optional typing preload kept off the Game facade."""
import gc

def _has_type(name):
    try:
        with open("/" + name, "r") as route:
            for raw in route:
                if raw.strip().upper().startswith("TYPE|"):
                    return True
    except OSError:
        pass
    return False

def scan(name):
    offsets = bytearray()
    parallel = False
    needs_type = False
    responses = []
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
                parallel = True
            elif op == "TYPE":
                needs_type = True
            elif op == "SOUNDWATCH" and "|" in line:
                for profile in line.split("|", 1)[1].split(";"):
                    values = profile.split(",")
                    if len(values) >= 7:
                        responses.append(values[6])
            for shift in (0, 8, 16, 24):
                offsets.append((offset >> shift) & 255)
    if not needs_type:
        for response in responses:
            if _has_type(response):
                needs_type = True
                break
    return parallel, needs_type, offsets

def preload_type(ctx, heap):
    gc.collect(); heap(ctx, "before-type-preload"); gc.collect()
    try:
        import plan_engine_login as login
        login._typing(ctx)
    except MemoryError:
        heap(ctx, "type-preload-memoryerror")
        raise
    gc.collect(); heap(ctx, "after-type-preload")