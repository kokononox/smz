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
    int paused = 0, interrupted = 0, resumed = 0, join_all_seen = 0;
    for (unsigned fetch = 0; fetch < 50000u; ++fetch) {
        AbvmEvent event = abvm_tick(&vm, now);
        if (vm.scope.active && vm.scope.policy==ABVM_SCOPE_JOIN_ALL)
            join_all_seen=1;
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
        !require(join_all_seen, "JOIN_ALL scope executed") ||
        !require(paused && interrupted && resumed, "control paths executed"))
        return 1;

    /* Both JOIN_ALL lanes begin with keyboard actions.  Keep the first
     * action pending and prove the second lane waits instead of reaching the
     * hardware actor and failing busy. */
    if (!require(abvm_init(&vm,image,(size_t)length),"join reinitialize") ||
        !require(abvm_start_route(&vm,8u,0u),"join start Game")) return 1;
    AbvmEvent first_join_action; first_join_action.type=ABVM_EVENT_NONE;
    now=0u;
    for(unsigned fetch=0;fetch<1000u;++fetch){
        AbvmEvent event=abvm_tick(&vm,now++);
        if(event.type==ABVM_EVENT_FAULT)return 1;
        if(event.type==ABVM_EVENT_ACTION){
            if(vm.scope.active&&vm.scope.policy==ABVM_SCOPE_JOIN_ALL){
                first_join_action=event;break;
            }
            if(!require(abvm_complete_action(&vm,event.lane,now),
                        "complete pre-join action"))return 1;
        }
    }
    if(!require(first_join_action.type==ABVM_EVENT_ACTION,
                "first JOIN_ALL keyboard action"))return 1;
    AbvmEvent queued=abvm_tick(&vm,now++);
    if(!require(queued.type==ABVM_EVENT_NONE,
                "same actor lane waits while keyboard is busy") ||
       !require(abvm_complete_action(&vm,first_join_action.lane,now),
                "complete first JOIN_ALL action"))return 1;
    AbvmEvent second_join_action;second_join_action.type=ABVM_EVENT_NONE;
    for(unsigned fetch=0;fetch<20u;++fetch){
        AbvmEvent event=abvm_tick(&vm,now++);
        if(event.type==ABVM_EVENT_ACTION){second_join_action=event;break;}
        if(event.type==ABVM_EVENT_FAULT)return 1;
    }
    if(!require(second_join_action.type==ABVM_EVENT_ACTION &&
                second_join_action.lane!=first_join_action.lane,
                "second JOIN_ALL lane runs after actor release") ||
       !require(abvm_complete_action(&vm,second_join_action.lane,now),
                "complete second JOIN_ALL action"))return 1;

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