/*
 * A route that opens with a settle is the rest between rounds, and a round that
 * a power cut interrupted never reached it.  Entering such a route without that
 * one delay is the whole of this contract: everything the route does after the
 * settle still runs, a route that opens with an action is untouched, and a delay
 * that is not the route's first step is never stepped over.
 */
#include "abvm_vm.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int require(int condition, const char *message) {
    if (!condition) fprintf(stderr, "ABVM settle smoke failure: %s\n", message);
    return condition;
}

/* A fresh route first hands back the actors the previous one held.  That is not
 * part of this contract, so it is drained before the route's own step is read. */
static AbvmEvent settle_tick(AbvmVm *vm, uint32_t now) {
    AbvmEvent event = abvm_tick(vm, now);
    while (event.type == ABVM_EVENT_RELEASE_ALL) event = abvm_tick(vm, now);
    return event;
}

static int route_head(const AbvmVm *vm, uint16_t route_id, AbvmRoute *route,
                      AbvmInstruction *first) {
    for (uint32_t i = 0; i < vm->header.route_count; ++i) {
        memcpy(route, vm->image + vm->header.route_offset + i * sizeof(*route),
               sizeof(*route));
        if (route->route_id != route_id) continue;
        memcpy(first, vm->image + vm->header.code_offset +
               (size_t)route->pc * sizeof(*first), sizeof(*first));
        return 1;
    }
    return 0;
}

int main(int argc, char **argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: abvm_settle_smoke program.abp\n");
        return 2;
    }
    FILE *file = fopen(argv[1], "rb");
    if (!file) return 2;
    fseek(file, 0, SEEK_END);
    long length = ftell(file);
    rewind(file);
    uint8_t *image = (uint8_t *)malloc((size_t)length);
    if (!image || fread(image, 1, (size_t)length, file) != (size_t)length) {
        fclose(file); free(image); return 2;
    }
    fclose(file);

    AbvmVm vm;
    if (!require(abvm_init(&vm, image, (size_t)length), "valid image")) return 1;

    AbvmRoute startup, game;
    AbvmInstruction head;
    if (!require(route_head(&vm, 3u, &startup, &head), "Startup route") ||
        !require(head.opcode == ABVM_OP_DELAY, "Startup opens with a settle") ||
        !require(startup.length > 1u, "settle is not the whole route") ||
        !require(route_head(&vm, 8u, &game, &head), "Game route") ||
        !require(head.opcode != ABVM_OP_DELAY, "Game opens with an action"))
        return 1;

    /* A plain start owes the settle: with no time passed the route has not moved. */
    if (!require(abvm_start_route(&vm, 3u, 1000u), "plain start") ||
        !require(vm.lanes[0].pc == startup.pc, "plain start begins at the settle") ||
        !require(settle_tick(&vm, 1000u).type == ABVM_EVENT_NONE,
                 "plain start waits out the settle"))
        return 1;

    /* The power-cut start leaves that one delay out and reaches the authored
     * step it was hiding, without any time passing. */
    if (!require(abvm_start_route_without_opening_delay(&vm, 3u, 1000u),
                 "start without the settle") ||
        !require(vm.lanes[0].pc == startup.pc + 1u, "opening delay skipped") ||
        !require(vm.lanes[0].due == 1000u, "no settle left owed"))
        return 1;
    AbvmEvent event = settle_tick(&vm, 1000u);
    if (!require(event.type == ABVM_EVENT_ACTION && event.opcode == ABVM_OP_KEY,
                 "first authored step runs") ||
        !require(abvm_complete_action(&vm, event.lane, 1000u), "complete step"))
        return 1;

    /* Only the opening delay was left out: the route's own second delay still
     * holds the lane, exactly as it did before. */
    if (!require(settle_tick(&vm, 1000u).type == ABVM_EVENT_NONE,
                 "the route's later settle still holds") ||
        !require(settle_tick(&vm, 11000u).type == ABVM_EVENT_ACTION,
                 "the route continues after its own settle"))
        return 1;

    /* A route that opens with an action keeps every step it has. */
    if (!require(abvm_start_route_without_opening_delay(&vm, 8u, 1000u),
                 "start Game without a settle") ||
        !require(vm.lanes[0].pc == game.pc, "an action is never stepped over") ||
        !require(settle_tick(&vm, 1000u).type == ABVM_EVENT_ACTION,
                 "Game still runs its first step"))
        return 1;

    free(image);
    printf("ABVM opening-settle smoke passed\n");
    return 0;
}
