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

    /* An operator WAKE! arm must survive a clock sample that lands while the
     * authored schedule is off: there is nothing to replace it with, and losing
     * it would cancel the only wake of the test. */
    WakeScheduler keptArm = make(2u);
    wake_scheduler_configure(&keptArm, false, 480u, 1200u, 1320u, 360u);
    assert(wake_scheduler_arm_dry(&keptArm, 0u, 60u * 1000u));
    assert(wake_scheduler_armed(&keptArm));
    assert(!wake_scheduler_sync(&keptArm, 5u * 1000u, 600u));
    assert(wake_scheduler_armed(&keptArm));
    assert(keptArm.manual && keptArm.dry);
    assert(keptArm.deadline_ms == 60u * 1000u);
    assert(keptArm.synced && keptArm.synced_minute == 600u);
    assert(!wake_scheduler_due(&keptArm, 59u * 1000u));
    assert(wake_scheduler_due(&keptArm, 60u * 1000u));
    /* An enabled schedule still replaces the test arm with the real window. */
    wake_scheduler_configure(&keptArm, true, 480u, 1200u, 1320u, 360u);
    assert(wake_scheduler_sync(&keptArm, 5u * 1000u, 600u));
    assert(keptArm.armed && !keptArm.manual && !keptArm.dry);
    assert(keptArm.next_start == 1320u);
    /* A malformed minute never anchors the wall clock either. */
    WakeScheduler badMinute = make(2u);
    wake_scheduler_configure(&badMinute, false, 480u, 1200u, 1320u, 360u);
    assert(wake_scheduler_arm_at(&badMinute, 0u, 60u * 1000u));
    assert(!wake_scheduler_sync(&badMinute, 0u, 1440u));
    assert(wake_scheduler_armed(&badMinute) && !badMinute.synced);

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
    assert(!wall.dry);
    assert(wake_scheduler_sync(&wall, 41u * MIN, 600u));
    assert(!wall.manual);
    assert(wall.next_start == 1320u);

    /* A dry test arm behaves identically as a deadline but flags itself so the
     * caller can skip the authored round. */
    WakeScheduler dry = make(2u);
    assert(wake_scheduler_arm_dry(&dry, 0u, 60u * 1000u));
    assert(dry.manual && dry.dry);
    assert(wake_scheduler_armed(&dry));
    assert(wake_scheduler_due(&dry, 60u * 1000u));
    wake_scheduler_disarm(&dry);
    assert(!dry.manual && !dry.dry);
    assert(!wake_scheduler_armed(&dry));
    assert(!wake_scheduler_arm_dry(&dry, 0u, 0u));

    /* Recovery is owed only for the combination a reset cannot undo: the host was
     * asleep, a wake was still pending, and pulse budget is left. */
    assert(wake_scheduler_recovery_needed(true, true, 0u, 3u));
    assert(wake_scheduler_recovery_needed(true, true, 2u, 3u));
    assert(!wake_scheduler_recovery_needed(true, true, 3u, 3u));
    assert(!wake_scheduler_recovery_needed(true, true, 4u, 3u));
    assert(!wake_scheduler_recovery_needed(true, false, 0u, 3u));
    assert(!wake_scheduler_recovery_needed(false, true, 0u, 3u));
    assert(!wake_scheduler_recovery_needed(false, false, 0u, 3u));

    /* The second boot state that needs the same pulse: a board that came back
     * from a power cut with a schedule and no clock at all.  Nothing is owed, so
     * the persisted decision cannot speak for it; the boot state can, and both
     * reasons spend the same budget. */
    assert(wake_scheduler_pulse_budget_left(0u, 3u));
    assert(wake_scheduler_pulse_budget_left(2u, 3u));
    assert(!wake_scheduler_pulse_budget_left(3u, 3u));
    assert(wake_scheduler_boot_clock_needed(true, false, 0u, 3u));
    assert(wake_scheduler_boot_clock_needed(true, false, 2u, 3u));
    /* Exhausted budget, an anchored clock or a schedule that is off all mean the
     * board has nothing to fetch a clock for. */
    assert(!wake_scheduler_boot_clock_needed(true, false, 3u, 3u));
    assert(!wake_scheduler_boot_clock_needed(true, true, 0u, 3u));
    assert(!wake_scheduler_boot_clock_needed(false, false, 0u, 3u));

    /* A consumed test arm hands the deadline back to the authored windows, so one
     * WAKE! run cannot leave the board unarmed for the real window. */
    WakeScheduler handed = make(2u);
    assert(wake_scheduler_sync(&handed, 0u, 600u));
    assert(wake_scheduler_arm_dry(&handed, 10u * MIN, 60u * 1000u));
    assert(handed.manual && handed.dry);
    wake_scheduler_disarm(&handed);
    assert(!wake_scheduler_armed(&handed));
    assert(wake_scheduler_rearm(&handed, 11u * MIN));
    assert(handed.armed && !handed.manual && !handed.dry);
    assert(handed.next_start == 1320u);
    assert(handed.synced_minute == 611u);
    assert(handed.deadline_ms == 718u * MIN);

    /* A window already inside the lead time must stay unarmed: re-arming there
     * would fall back to "one minute from now" and become a wake loop.  The guard
     * must not disarm an arm that is already held either. */
    WakeScheduler atWindow = make(2u);
    assert(wake_scheduler_sync(&atWindow, 0u, 1310u));
    assert(atWindow.next_start == 1320u);
    assert(!wake_scheduler_rearm(&atWindow, 9u * MIN));
    assert(wake_scheduler_armed(&atWindow));
    /* Exactly on the start the other window is next, so the handback is right. */
    assert(wake_scheduler_rearm(&atWindow, 10u * MIN));
    assert(atWindow.next_start == 480u);

    /* No schedule, no anchor, no re-arm. */
    WakeScheduler noSchedule;
    wake_scheduler_init(&noSchedule, 2u);
    assert(!wake_scheduler_rearm(&noSchedule, 0u));
    WakeScheduler noClock = make(2u);
    assert(!wake_scheduler_rearm(&noClock, 0u));
    WakeScheduler offSchedule;
    wake_scheduler_init(&offSchedule, 2u);
    wake_scheduler_configure(&offSchedule, false, 480u, 1200u, 1320u, 360u);
    /* A schedule that is off still anchors the wall clock, but never arms. */
    assert(!wake_scheduler_sync(&offSchedule, 0u, 600u));
    assert(offSchedule.synced);
    assert(!wake_scheduler_rearm(&offSchedule, 1u * MIN));

    puts("wake scheduler: nearest start, lead clamp, gaps, midnight wrap, "
         "re-sync, disarm, malformed-minute, wall anchor, WAKE! arm, "
         "manual-arm survival, re-arm handback, owed-wake recovery and "
         "clockless-boot recovery paths passed");
    return 0;
}
