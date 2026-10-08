#include <assert.h>
#include <stdio.h>
#include "wake_scheduler.h"

#define MIN 60000u

/* Day 08:00-20:00, night 22:00-06:00 with intentional gaps. */
static WakeScheduler make(uint16_t lead) {
    WakeScheduler w;
    wake_scheduler_init(&w, lead);
    wake_scheduler_configure(&w, true, 480u, 1200u, 1320u, 360u);
    return w;
}

int main(void) {
    /* Strictly-after distance, including the exact-start case. */
    assert(wake_scheduler_minutes_until(0u, 0u) == 1440u);
    assert(wake_scheduler_minutes_until(479u, 480u) == 1u);
    assert(wake_scheduler_minutes_until(480u, 480u) == 1440u);
    assert(wake_scheduler_minutes_until(1439u, 0u) == 1u);
    assert(wake_scheduler_minutes_until(1440u, 0u) == 0u);

    /* Nearest upcoming window start wins. */
    uint16_t start = 0u;
    assert(wake_scheduler_next_window_start(600u, 480u, 1320u, &start) && start == 1320u);
    assert(wake_scheduler_next_window_start(1400u, 480u, 1320u, &start) && start == 480u);
    assert(wake_scheduler_next_window_start(0u, 480u, 1320u, &start) && start == 480u);
    /* Inside the day window the night start is nearer, and vice versa. */
    assert(wake_scheduler_next_window_start(1000u, 480u, 1320u, &start) && start == 1320u);
    assert(wake_scheduler_next_window_start(200u, 480u, 1320u, &start) && start == 480u);
    /* A sample exactly on the day start targets the night window instead. */
    assert(wake_scheduler_next_window_start(480u, 480u, 1320u, &start) && start == 1320u);
    assert(!wake_scheduler_next_window_start(1440u, 480u, 1320u, &start));
    assert(!wake_scheduler_next_window_start(0u, 1440u, 1320u, &start));

    /* Day shift sample at 10:00 -> wake at 21:58 the same day. */
    WakeScheduler w = make(2u);
    assert(!wake_scheduler_armed(&w));
    assert(!wake_scheduler_due(&w, 0u));
    assert(wake_scheduler_sync(&w, 1000u, 600u));
    assert(wake_scheduler_armed(&w));
    assert(w.next_start == 1320u);
    assert(w.deadline_ms == 1000u + (720u - 2u) * MIN);
    assert(!wake_scheduler_due(&w, w.deadline_ms - 1u));
    assert(wake_scheduler_due(&w, w.deadline_ms));
    assert(wake_scheduler_due(&w, w.deadline_ms + 5u * MIN));

    /* Night shift sample at 02:00 -> wake at 07:58 the same morning. */
    assert(wake_scheduler_sync(&w, 7u * MIN, 120u));
    assert(w.next_start == 480u);
    assert(w.deadline_ms == 7u * MIN + (360u - 2u) * MIN);

    /* A sample inside a gap still targets the next window start. */
    assert(wake_scheduler_sync(&w, 0u, 1260u));
    assert(w.next_start == 1320u);
    assert(w.deadline_ms == (60u - 2u) * MIN);

    /* A window closer than the lead time must not schedule a past deadline. */
    WakeScheduler tight = make(30u);
    assert(wake_scheduler_sync(&tight, 5u * MIN, 1310u));
    assert(tight.next_start == 1320u);
    assert(tight.deadline_ms == 5u * MIN + MIN);
    assert(!wake_scheduler_due(&tight, 5u * MIN));
    assert(wake_scheduler_due(&tight, 6u * MIN));

    /* Zero lead wakes exactly on the window start. */
    WakeScheduler exact = make(0u);
    assert(wake_scheduler_sync(&exact, 0u, 600u));
    assert(exact.deadline_ms == 720u * MIN);

    /* Re-sync always replaces the previous deadline. */
    assert(wake_scheduler_sync(&w, 100u * MIN, 600u));
    uint32_t first = w.deadline_ms;
    assert(wake_scheduler_sync(&w, 101u * MIN, 600u));
    assert(w.deadline_ms == first + MIN);
    assert(w.synced_minute == 600u);
    assert(w.synced_at == 101u * MIN);

    /* Disabling the schedule disarms; re-enabling does not silently re-arm. */
    wake_scheduler_configure(&w, false, 480u, 1200u, 1320u, 360u);
    assert(!wake_scheduler_armed(&w));
    assert(!wake_scheduler_due(&w, 1000u * MIN));
    assert(!wake_scheduler_sync(&w, 0u, 600u));
    wake_scheduler_configure(&w, true, 480u, 1200u, 1320u, 360u);
    assert(!wake_scheduler_armed(&w));
    assert(!wake_scheduler_due(&w, 1000u * MIN));
    assert(wake_scheduler_sync(&w, 0u, 600u));

    /* An out-of-range minute from a malformed reply never arms. */
    assert(!wake_scheduler_sync(&w, 0u, 1440u));
    assert(!wake_scheduler_armed(&w));

    /* Manual disarm (operator stop) is respected until the next sync. */
    assert(wake_scheduler_sync(&w, 0u, 600u));
    wake_scheduler_disarm(&w);
    assert(!wake_scheduler_due(&w, 2000u * MIN));

    /* Midnight wrap: 23:00 sample, day start is the nearest window start. */
    assert(wake_scheduler_sync(&w, 0u, 1380u));
    assert(w.next_start == 480u);
    assert(w.deadline_ms == (540u - 2u) * MIN);

    puts("wake scheduler: nearest start, lead clamp, gaps, midnight wrap, "
         "re-sync, disarm and malformed-minute paths passed");
    return 0;
}
