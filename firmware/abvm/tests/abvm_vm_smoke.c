#include "abvm_vm.h"

#include <stdio.h>
#include <stdlib.h>

static int require(int condition, const char *message) {
    if (!condition) fprintf(stderr, "ABVM smoke failure: %s\n", message);
    return condition;
}

int main(int argc, char **argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: abvm_vm_smoke program.abp\n");
        return 2;
    }
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
    if (!require(abvm_init(&vm, image, (size_t)length), "valid image") ||
        !require(abvm_start_route(&vm, 8u, 0u), "start Game"))
        return 1;

    uint32_t now = 0;
    unsigned actions = 0, watches = 0;
    int paused = 0, interrupted = 0, resumed = 0;
    for (unsigned fetch = 0; fetch < 50000u; ++fetch) {
        AbvmEvent event = abvm_tick(&vm, now);
        if (event.type == ABVM_EVENT_FAULT) {
            fprintf(stderr, "ABVM fault: %s\n", event.message);
            return 1;
        }
        if (event.type == ABVM_EVENT_ACTION) {
            ++actions;
            if (!require(abvm_complete_action(&vm, event.lane, now),
                         "complete action")) return 1;
        } else if (event.type == ABVM_EVENT_WATCH_ARMED) {
            ++watches;
            int accepted = event.flags == 3u
                ? abvm_light_detected(&vm,event.lane,event.constant_id,now)
                : abvm_sound_detected(&vm,event.operand_a,now);
            if (!require(accepted, event.flags == 3u
                         ? "light detect" : "sound detect")) return 1;
        } else if (event.type == ABVM_EVENT_INTERRUPT_RESUME) {
            resumed = 1;
        } else if (event.type == ABVM_EVENT_ROUTE_COMPLETE) {
            break;
        }

        if (!paused && actions >= 3u && vm.route_id == 8u) {
            uint32_t saved_pc = vm.lanes[0].pc;
            if (!require(abvm_pause(&vm, now), "pause") ||
                !require(abvm_tick(&vm, now).type==ABVM_EVENT_RELEASE_ALL,
                         "pause release-all") ||
                !require(abvm_resume(&vm, now+500u), "resume") ||
                !require(vm.lanes[0].pc==saved_pc, "resume exact PC"))
                return 1;
            now += 500u;
            paused = 1;
        }
        if (!interrupted && actions >= 6u && vm.route_id == 8u) {
            uint32_t saved_pc = vm.lanes[0].pc;
            if (!require(abvm_interrupt_route(&vm,10u,now),"Whisper interrupt") ||
                !require(!abvm_interrupt_route(&vm,10u,now),
                         "reject nested interrupt") ||
                !require(abvm_tick(&vm,now).type==ABVM_EVENT_RELEASE_ALL,
                         "interrupt release-all") ||
                !require(vm.suspended.lanes[0].pc==saved_pc,
                         "suspended exact PC"))
                return 1;
            interrupted = 1;
        }
        now += 100u;
    }

    if (!require(vm.status == ABVM_STATUS_COMPLETE, "Game complete") ||
        !require(actions > 10u, "actions executed") ||
        !require(watches > 0u, "Watch executed") ||
        !require(paused && interrupted && resumed, "control paths executed"))
        return 1;

    if (!require(abvm_init(&vm,image,(size_t)length),"reinitialize") ||
        !require(abvm_start_route(&vm,8u,0u),"restart Game"))
        return 1;
    (void)abvm_tick(&vm,0u);
    abvm_stop(&vm,25u);
    if (!require(abvm_tick(&vm,25u).type==ABVM_EVENT_RELEASE_ALL,
                 "stop release-all") ||
        !require(vm.status==ABVM_STATUS_STOPPED,"stopped state"))
        return 1;

    uint32_t fuzz = 7u;
    for (unsigned i = 0; i < 100u; ++i) {
        fuzz ^= fuzz << 13; fuzz ^= fuzz >> 17; fuzz ^= fuzz << 5;
        size_t offset = fuzz % (size_t)length;
        uint8_t mask = (uint8_t)(1u << (fuzz & 7u));
        image[offset] ^= mask;
        if (!require(!abvm_init(&vm,image,(size_t)length),
                     "reject corruption fuzz"))
            return 1;
        image[offset] ^= mask;
    }
    free(image);
    printf("ABVM native loader/scheduler smoke passed\n");
    return 0;
}