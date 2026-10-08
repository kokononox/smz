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

    /* Wall anchor: unknown until a sample arrives, then it tracks the Pico clock
     * across midnight and across days. */
    WakeScheduler wall = make(2u);
    uint16_t minute = 1234u;
    assert(!wake_scheduler_wall_minute(&wall, 0u, &minute));
    assert(wake_scheduler_sync(&wall, 10u * MIN, 1430u));
    assert(wake_scheduler_wall_minute(&wall, 10u * MIN, &minute) && minute == 1430u);
    /* 23:50 plus thirty minutes wraps to 00:20. */
    assert(wake_scheduler_wall_minute(&wall, 40u * MIN, &minute) && minute == 20u);
    assert(wake_scheduler_wall_minute(&wall, 40u * MIN + 3u * 24u * 60u * MIN, &minute) &&
           minute == 20u);

    /* A WAKE! test arm is honoured even while the authored schedule is off, and
     * without an anchor it simply reports no target minute. */
    WakeScheduler manual;
    wake_scheduler_init(&manual, 2u);
    wake_scheduler_configure(&manual, false, 480u, 1200u, 1320u, 360u);
    assert(!wake_scheduler_arm_at(&manual, 0u, 0u));
    assert(!wake_scheduler_armed(&manual));
    assert(wake_scheduler_arm_at(&manual, 5u * MIN, 60u * 1000u));
    assert(wake_scheduler_armed(&manual));
    assert(manual.next_start == 0u);
    assert(!wake_scheduler_due(&manual, 5u * MIN + 59999u));
    assert(wake_scheduler_due(&manual, 6u * MIN));

    /* With an anchor, a test arm labels a real minute of day. */
    WakeScheduler labeled = make(2u);
    assert(wake_scheduler_sync(&labeled, 0u, 60u));
    assert(wake_scheduler_arm_at(&labeled, 0u, 30u * MIN));
    assert(labeled.next_start == 90u);

    /* A bridge sample always supersedes a test arm. */
    assert(wake_scheduler_arm_at(&wall, 40u * MIN, 60u * 1000u));
    assert(wall.manual);
    assert(wake_scheduler_sync(&wall, 41u * MIN, 600u));
    assert(!wall.manual);
    assert(wall.next_start == 1320u);

    /* Recovery is owed only for the combination a reset cannot undo: the host was
     * asleep, a wake was still pending, and pulse budget is left. */
    assert(wake_scheduler_recovery_needed(true, true, 0u, 3u));
    assert(wake_scheduler_recovery_needed(true, true, 2u, 3u));
    assert(!wake_scheduler_recovery_needed(true, true, 3u, 3u));
    assert(!wake_scheduler_recovery_needed(true, true, 4u, 3u));
    assert(!wake_scheduler_recovery_needed(true, false, 0u, 3u));
    assert(!wake_scheduler_recovery_needed(false, true, 0u, 3u));
    assert(!wake_scheduler_recovery_needed(false, false, 0u, 3u));

    puts("wake scheduler: nearest start, lead clamp, gaps, midnight wrap, "
         "re-sync, disarm, malformed-minute, wall anchor, WAKE! arm and "
         "bounded-recovery paths passed");
    return 0;
}
