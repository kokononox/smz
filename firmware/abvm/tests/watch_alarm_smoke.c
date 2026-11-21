#include "abvm_vm.h"

#include <stdio.h>
#include <stdlib.h>

static int require(int ok, const char *what) {
    if (!ok) fprintf(stderr, "Watch alarm smoke failure: %s\n", what);
    return ok;
}

static int next_watch(AbvmVm *vm, uint32_t *now, AbvmEvent *watch) {
    for (unsigned i = 0; i < 100u; ++i) {
        AbvmEvent event = abvm_tick(vm, *now);
        if (event.type == ABVM_EVENT_WATCH_ARMED) {
            *watch = event;
            return 1;
        }
        if (event.type == ABVM_EVENT_ACTION) {
            if (!abvm_complete_action(vm, event.lane, *now)) return 0;
        } else if (event.type == ABVM_EVENT_FAULT ||
                   event.type == ABVM_EVENT_ROUTE_COMPLETE ||
                   event.type == ABVM_EVENT_WATCH_TIMEOUT_ALARM) {
            return 0;
        }
        ++*now;
    }
    return 0;
}

static int timeout_watch(AbvmVm *vm, uint32_t *now, AbvmEvent watch,
                         uint32_t expected_streak, AbvmEvent *next) {
    *now += watch.operand_b;
    AbvmEvent event = abvm_tick(vm, *now);
    if (expected_streak) {
        if (!require(event.type == ABVM_EVENT_WATCH_TIMEOUT_ALARM,
                     "threshold timeout raises alarm event") ||
            !require(event.operand_a == watch.operand_a,
                     "alarm carries sound profile") ||
            !require(event.operand_b == expected_streak,
                     "alarm carries consecutive miss count"))
            return -1;
        return 1;
    }
    if (event.type == ABVM_EVENT_WATCH_TIMEOUT_ALARM)
        return require(0, "alarm is suppressed below threshold") ? 0 : -1;
    if (event.type == ABVM_EVENT_WATCH_ARMED) {
        *next = event;
        return 0;
    }
    ++*now;
    if (!next_watch(vm, now, next)) return -1;
    return 0;
}

int main(int argc, char **argv) {
    if (argc != 2) return 2;
    FILE *file = fopen(argv[1], "rb");
    if (!file) return 2;
    fseek(file, 0, SEEK_END);
    long length = ftell(file);
    rewind(file);
    uint8_t *image = (uint8_t *)malloc((size_t)length);
    if (!image || fread(image, 1, (size_t)length, file) != (size_t)length) {
        fclose(file);
        free(image);
        return 2;
    }
    fclose(file);

    AbvmVm vm;
    if (!require(abvm_init(&vm, image, (size_t)length), "valid program") ||
        !require(abvm_start_route(&vm, 8u, 0u), "start Game")) return 1;

    uint32_t now = 0u;
    AbvmEvent watch;
    if (!require(next_watch(&vm, &now, &watch), "first Watch armed")) return 1;
    AbvmEvent after;
    if (!require(timeout_watch(&vm, &now, watch, 0u, &after) == 0,
                 "first miss stays quiet")) return 1;
    watch = after;
    if (!require(timeout_watch(&vm, &now, watch, 2u, &after) == 1,
                 "second consecutive miss alarms")) return 1;

    /* A successful Catch resets the streak before the next cast. */
    if (!require(next_watch(&vm, &now, &watch), "third Watch armed") ||
        !require(abvm_sound_detected(&vm, watch.operand_a, now),
                 "Catch detected")) return 1;
    if (!require(next_watch(&vm, &now, &watch), "next cast armed") ||
        !require(timeout_watch(&vm, &now, watch, 0u, &after) == 0,
                 "first miss after Catch stays quiet")) return 1;

    free(image);
    puts("ABVM consecutive sound-timeout alarm smoke passed");
    return 0;
}