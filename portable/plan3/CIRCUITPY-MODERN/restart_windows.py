"""Humanized Windows restart sequence used only after natural cycle expiry."""
import random

WIN, X, UP, ENTER, SHIFT, TAB = 91, 88, 38, 13, 16, 9
_STEPS = (
    (UP, (45, 100), (110, 240)),
    (UP, (45, 100), (180, 360)),
    (ENTER, (55, 120), (280, 520)),
    (UP, (45, 100), (350, 700)),
    (ENTER, (55, 120), None),
)


def _wait(ctx, rng, bounds):
    if bounds is not None and not ctx.sleep_ms(rng.randint(bounds[0], bounds[1])):
        raise RuntimeError("restart sequence aborted")


def _tap(ctx, rng, vk, hold, after):
    ctx.kdown(vk)
    _wait(ctx, rng, hold)
    ctx.kup(vk)
    _wait(ctx, rng, after)


def _shift_tab(ctx, rng):
    ctx.kdown(SHIFT)
    _wait(ctx, rng, (320, 480))
    ctx.kdown(TAB)
    _wait(ctx, rng, (160, 240))
    ctx.kup(TAB)
    _wait(ctx, rng, (180, 300))
    ctx.kup(SHIFT)


def perform(ctx, rng=None):
    rng = rng or random
    ctx.kdown(WIN)
    _wait(ctx, rng, (25, 60))
    ctx.kdown(X)
    _wait(ctx, rng, (45, 95))
    ctx.kup(X)
    _wait(ctx, rng, (25, 60))
    ctx.kup(WIN)
    _wait(ctx, rng, (450, 850))
    for vk, hold, after in _STEPS:
        _tap(ctx, rng, vk, hold, after)
    _wait(ctx, rng, (5200, 6000))
    _shift_tab(ctx, rng)
    _wait(ctx, rng, (300, 650))
    _tap(ctx, rng, ENTER, (65, 130), None)