#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "shift_identity_runtime.h"

/* The operator's own schedule: day 04:30-14:30, night 18:30-04:30, and the
 * two uncovered gaps (14:30-18:30 and 04:30-04:30 is covered by night). */
#define DS 270u
#define DE 870u
#define NS 1110u
#define NE 270u

int main(void) {
    /* A minute inside a window names that window. */
    assert(shift_kind_at_minute(270u, DS, DE, NS, NE) == SHIFT_DAY);
    assert(shift_kind_at_minute(273u, DS, DE, NS, NE) == SHIFT_DAY);
    assert(shift_kind_at_minute(869u, DS, DE, NS, NE) == SHIFT_DAY);
    assert(shift_kind_at_minute(1110u, DS, DE, NS, NE) == SHIFT_NIGHT);
    assert(shift_kind_at_minute(1439u, DS, DE, NS, NE) == SHIFT_NIGHT);
    assert(shift_kind_at_minute(0u, DS, DE, NS, NE) == SHIFT_NIGHT);
    assert(shift_kind_at_minute(269u, DS, DE, NS, NE) == SHIFT_NIGHT);

    /* End is exclusive and start is inclusive, so the boundary minute belongs
     * to the window that is beginning. */
    assert(shift_kind_at_minute(870u, DS, DE, NS, NE) == SHIFT_GLOBAL);
    assert(shift_kind_at_minute(1109u, DS, DE, NS, NE) == SHIFT_GLOBAL);

    /* An uncovered minute owns no window: the last round's check must ignore it
     * instead of switching the machine on a guess. */
    assert(shift_kind_at_minute(900u, DS, DE, NS, NE) == SHIFT_GLOBAL);

    /* Out of range and unconfigured schedules own nothing. */
    assert(shift_kind_at_minute(1440u, DS, DE, NS, NE) == SHIFT_GLOBAL);
    assert(shift_kind_at_minute(0u, 0u, 0u, 0u, 0u) == SHIFT_GLOBAL);
    assert(shift_kind_at_minute(600u, 0u, 0u, 0u, 0u) == SHIFT_GLOBAL);
    assert(shift_kind_at_minute(600u, 480u, 480u, 1320u, 360u) == SHIFT_GLOBAL);

    /* The runtime's own answer must be the same function of the same minute, so
     * a board holding an anchor agrees with a board holding a fresh reply. */
    ShiftIdentityRuntime s;
    memset(&s, 0, sizeof(s));
    s.schedule_enabled = true;
    s.day_start = DS; s.day_end = DE; s.night_start = NS; s.night_end = NE;
    assert(shift_identity_expected(&s) == SHIFT_GLOBAL); /* no clock yet */
    s.clock_received = true;
    for (uint16_t m = 0u; m < 1440u; ++m) {
        s.minute = m;
        assert(shift_identity_expected(&s) == shift_kind_at_minute(m, DS, DE, NS, NE));
    }
    /* A v1 descriptor carries no schedule at all. */
    s.schedule_enabled = false;
    assert(shift_identity_expected(&s) == SHIFT_GLOBAL);

    printf("shift-identity smoke ok\n");
    return 0;
}
