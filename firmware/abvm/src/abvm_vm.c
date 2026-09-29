#include "abvm_vm.h"

#include <string.h>

#define ABVM_HEADER_CRC_OFFSET 52u
#define ABVM_HEADER_PROGRAM_SHA_OFFSET 92u
#define ABVM_ROUTE_POLICY_MASK 0x00ffu
#define ABVM_ROUTE_CLOCK_ACTIVE 0x0100u
#define ABVM_ROUTE_CLOCK_MASK 0x0100u
#define ABVM_ROUTE_PAUSE_RELEASE_HID 0x0200u
#define ABVM_ROUTE_ALLOWED_FLAGS 0x03ffu

typedef struct Sha256 {
    uint32_t state[8];
    uint64_t bits;
    uint8_t block[64];
    uint8_t used;
} Sha256;

static uint32_t rotr(uint32_t value, uint8_t count) {
    return (value >> count) | (value << (32u - count));
}

static void sha_transform(Sha256 *ctx, const uint8_t *data) {
    static const uint32_t k[64] = {
        0x428a2f98u,0x71374491u,0xb5c0fbcfu,0xe9b5dba5u,
        0x3956c25bu,0x59f111f1u,0x923f82a4u,0xab1c5ed5u,
        0xd807aa98u,0x12835b01u,0x243185beu,0x550c7dc3u,
        0x72be5d74u,0x80deb1feu,0x9bdc06a7u,0xc19bf174u,
        0xe49b69c1u,0xefbe4786u,0x0fc19dc6u,0x240ca1ccu,
        0x2de92c6fu,0x4a7484aau,0x5cb0a9dcu,0x76f988dau,
        0x983e5152u,0xa831c66du,0xb00327c8u,0xbf597fc7u,
        0xc6e00bf3u,0xd5a79147u,0x06ca6351u,0x14292967u,
        0x27b70a85u,0x2e1b2138u,0x4d2c6dfcu,0x53380d13u,
        0x650a7354u,0x766a0abbu,0x81c2c92eu,0x92722c85u,
        0xa2bfe8a1u,0xa81a664bu,0xc24b8b70u,0xc76c51a3u,
        0xd192e819u,0xd6990624u,0xf40e3585u,0x106aa070u,
        0x19a4c116u,0x1e376c08u,0x2748774cu,0x34b0bcb5u,
        0x391c0cb3u,0x4ed8aa4au,0x5b9cca4fu,0x682e6ff3u,
        0x748f82eeu,0x78a5636fu,0x84c87814u,0x8cc70208u,
        0x90befffau,0xa4506cebu,0xbef9a3f7u,0xc67178f2u,
    };
    uint32_t w[64], a, b, c, d, e, f, g, h;
    for (uint8_t i = 0; i < 16; ++i) {
        uint8_t p = (uint8_t)(i * 4u);
        w[i] = ((uint32_t)data[p] << 24) |
               ((uint32_t)data[p + 1u] << 16) |
               ((uint32_t)data[p + 2u] << 8) | data[p + 3u];
    }
    for (uint8_t i = 16; i < 64; ++i) {
        uint32_t s0 = rotr(w[i - 15u], 7) ^ rotr(w[i - 15u], 18) ^
                      (w[i - 15u] >> 3);
        uint32_t s1 = rotr(w[i - 2u], 17) ^ rotr(w[i - 2u], 19) ^
                      (w[i - 2u] >> 10);
        w[i] = w[i - 16u] + s0 + w[i - 7u] + s1;
    }
    a=ctx->state[0]; b=ctx->state[1]; c=ctx->state[2]; d=ctx->state[3];
    e=ctx->state[4]; f=ctx->state[5]; g=ctx->state[6]; h=ctx->state[7];
    for (uint8_t i = 0; i < 64; ++i) {
        uint32_t s1 = rotr(e,6)^rotr(e,11)^rotr(e,25);
        uint32_t ch = (e & f) ^ ((~e) & g);
        uint32_t t1 = h + s1 + ch + k[i] + w[i];
        uint32_t s0 = rotr(a,2)^rotr(a,13)^rotr(a,22);
        uint32_t maj = (a & b) ^ (a & c) ^ (b & c);
        uint32_t t2 = s0 + maj;
        h=g; g=f; f=e; e=d+t1; d=c; c=b; b=a; a=t1+t2;
    }
    ctx->state[0]+=a; ctx->state[1]+=b; ctx->state[2]+=c; ctx->state[3]+=d;
    ctx->state[4]+=e; ctx->state[5]+=f; ctx->state[6]+=g; ctx->state[7]+=h;
}

static void sha_init(Sha256 *ctx) {
    static const uint32_t initial[8] = {
        0x6a09e667u,0xbb67ae85u,0x3c6ef372u,0xa54ff53au,
        0x510e527fu,0x9b05688cu,0x1f83d9abu,0x5be0cd19u,
    };
    memcpy(ctx->state, initial, sizeof(initial));
    ctx->bits = 0;
    ctx->used = 0;
}

static void sha_update(Sha256 *ctx, const uint8_t *data, size_t size) {
    while (size--) {
        ctx->block[ctx->used++] = *data++;
        ctx->bits += 8u;
        if (ctx->used == 64u) {
            sha_transform(ctx, ctx->block);
            ctx->used = 0;
        }
    }
}

static void sha_final(Sha256 *ctx, uint8_t out[32]) {
    ctx->block[ctx->used++] = 0x80u;
    if (ctx->used > 56u) {
        while (ctx->used < 64u) ctx->block[ctx->used++] = 0;
        sha_transform(ctx, ctx->block);
        ctx->used = 0;
    }
    while (ctx->used < 56u) ctx->block[ctx->used++] = 0;
    for (uint8_t i = 0; i < 8; ++i)
        ctx->block[63u - i] = (uint8_t)(ctx->bits >> (i * 8u));
    sha_transform(ctx, ctx->block);
    for (uint8_t i = 0; i < 8; ++i) {
        out[i*4u]=(uint8_t)(ctx->state[i]>>24);
        out[i*4u+1u]=(uint8_t)(ctx->state[i]>>16);
        out[i*4u+2u]=(uint8_t)(ctx->state[i]>>8);
        out[i*4u+3u]=(uint8_t)ctx->state[i];
    }
}

static uint32_t crc32_update(uint32_t crc, uint8_t value) {
    crc ^= value;
    for (uint8_t i = 0; i < 8; ++i)
        crc = (crc >> 1) ^ (0xedb88320u & (uint32_t)-(int32_t)(crc & 1u));
    return crc;
}

static uint16_t read_u16(const uint8_t *p) {
    return (uint16_t)(p[0] | ((uint16_t)p[1] << 8));
}

static uint32_t read_u32(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static int fail(AbvmVm *vm, const char *message) {
    vm->status = ABVM_STATUS_FAULT;
    vm->fault = message;
    return 0;
}

static int time_reached(uint32_t now, uint32_t due) {
    return (int32_t)(now - due) >= 0;
}

static uint32_t random_next(AbvmVm *vm) {
    uint32_t x = vm->prng ? vm->prng : 0x6d2b79f5u;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    vm->prng = x;
    return x;
}

static uint32_t random_range(AbvmVm *vm, uint32_t lo, uint32_t hi) {
    if (hi <= lo) return lo;
    return lo + random_next(vm) % (hi - lo + 1u);
}

static int instruction_at(const AbvmVm *vm, uint32_t pc,
                          AbvmInstruction *out) {
    if (pc >= vm->header.code_count) return 0;
    memcpy(out, vm->image + vm->header.code_offset +
           pc * sizeof(*out), sizeof(*out));
    return 1;
}

static int route_at(const AbvmVm *vm, uint32_t index, AbvmRoute *out) {
    if (index >= vm->header.route_count) return 0;
    memcpy(out, vm->image + vm->header.route_offset +
           index * sizeof(*out), sizeof(*out));
    return 1;
}

static int find_route(const AbvmVm *vm, uint16_t route_id, AbvmRoute *out) {
    for (uint32_t i = 0; i < vm->header.route_count; ++i) {
        if (!route_at(vm, i, out)) return 0;
        if (out->route_id == route_id) return 1;
    }
    return 0;
}

static int constant_at(const AbvmVm *vm, uint16_t wanted, uint8_t kind,
                       const uint8_t **payload, uint32_t *size) {
    uint32_t cursor = vm->header.constant_offset;
    uint32_t end = cursor + vm->header.constant_size;
    uint16_t index = 0;
    while (cursor < end) {
        if (cursor + 8u > end) return 0;
        uint8_t actual = vm->image[cursor];
        uint32_t length = read_u32(vm->image + cursor + 4u);
        cursor += 8u;
        if (cursor + length > end) return 0;
        if (index == wanted) {
            if (kind && actual != kind) return 0;
            *payload = vm->image + cursor;
            *size = length;
            return 1;
        }
        cursor = (cursor + length + 3u) & ~3u;
        ++index;
    }
    return 0;
}

static int known_opcode(uint8_t opcode) {
    switch (opcode) {
        case ABVM_OP_END: case ABVM_OP_DELAY: case ABVM_OP_KEY:
        case ABVM_OP_KDOWN: case ABVM_OP_KUP: case ABVM_OP_TYPE:
        case ABVM_OP_RMOUSE: case ABVM_OP_LOOP_ENTER:
        case ABVM_OP_LOOP_NEXT: case ABVM_OP_RPKG_ENTER:
        case ABVM_OP_ITEM_END: case ABVM_OP_SCOPE_BEGIN:
        case ABVM_OP_LANE_END: case ABVM_OP_WATCH: case ABVM_OP_JUMP:
            return 1;
        default: return 0;
    }
}

static int verify_image(AbvmVm *vm) {
    const AbvmHeader *h = &vm->header;
    if (h->magic != ABVM_MAGIC || h->format_version != ABVM_FORMAT_VERSION ||
        h->vm_abi != ABVM_VM_ABI || h->header_size != ABVM_HEADER_SIZE ||
        h->file_size != vm->image_size)
        return fail(vm, "header identity");
    uint64_t code_end = (uint64_t)h->code_offset +
                        (uint64_t)h->code_count * ABVM_INSTRUCTION_SIZE;
    uint64_t route_end = (uint64_t)h->route_offset +
                         (uint64_t)h->route_count * ABVM_ROUTE_SIZE;
    if (h->code_offset != ABVM_HEADER_SIZE ||
        code_end != h->constant_offset ||
        (uint64_t)h->constant_offset + h->constant_size != h->route_offset ||
        route_end != h->resource_offset ||
        (uint64_t)h->resource_offset + h->resource_size != h->file_size ||
        h->resource_size != ABVM_RESOURCE_SIZE)
        return fail(vm, "section layout");

    uint32_t crc = 0xffffffffu;
    for (size_t i = 0; i < vm->image_size; ++i) {
        uint8_t value = (i >= ABVM_HEADER_CRC_OFFSET &&
                         i < ABVM_HEADER_CRC_OFFSET + 4u) ? 0 : vm->image[i];
        crc = crc32_update(crc, value);
    }
    if ((crc ^ 0xffffffffu) != h->crc32) return fail(vm, "crc32");

    uint8_t header[ABVM_HEADER_SIZE], digest[32];
    memcpy(header, vm->image, sizeof(header));
    memset(header + ABVM_HEADER_CRC_OFFSET, 0, 4);
    memset(header + ABVM_HEADER_PROGRAM_SHA_OFFSET, 0, 32);
    Sha256 sha;
    sha_init(&sha);
    sha_update(&sha, header, sizeof(header));
    sha_update(&sha, vm->image + sizeof(header), vm->image_size-sizeof(header));
    sha_final(&sha, digest);
    if (memcmp(digest, vm->image + ABVM_HEADER_PROGRAM_SHA_OFFSET, 32))
        return fail(vm, "program sha256");

    memcpy(&vm->resources, vm->image + h->resource_offset,
           sizeof(vm->resources));
    if (vm->resources.version != 1u ||
        vm->resources.size != ABVM_RESOURCE_SIZE ||
        vm->resources.max_frames > ABVM_LIMIT_FRAMES ||
        vm->resources.max_lanes > ABVM_LIMIT_LANES ||
        vm->resources.max_actors > ABVM_LIMIT_ACTORS ||
        vm->resources.max_events > ABVM_LIMIT_EVENTS ||
        vm->resources.max_interrupts > ABVM_LIMIT_INTERRUPTS ||
        vm->resources.sound_profiles > ABVM_LIMIT_SOUND_PROFILES)
        return fail(vm, "resource certificate");

    uint32_t cursor = h->constant_offset;
    uint32_t const_end = cursor + h->constant_size;
    while (cursor < const_end) {
        if (cursor + 8u > const_end) return fail(vm, "constant header");
        uint32_t size = read_u32(vm->image + cursor + 4u);
        cursor += 8u;
        if (cursor + size > const_end) return fail(vm, "constant payload");
        cursor = (cursor + size + 3u) & ~3u;
    }
    if (cursor != const_end) return fail(vm, "constant alignment");

    for (uint32_t pc = 0; pc < h->code_count; ++pc) {
        AbvmInstruction ins;
        const uint8_t *payload;
        uint32_t size;
        if (!instruction_at(vm, pc, &ins) || !known_opcode(ins.opcode))
            return fail(vm, "opcode");
        if ((ins.opcode == ABVM_OP_TYPE &&
             !constant_at(vm, ins.operand_a, ABVM_CONST_TYPE,&payload,&size)) ||
            (ins.opcode == ABVM_OP_RMOUSE &&
             !constant_at(vm, ins.operand_a,ABVM_CONST_MOUSE,&payload,&size)) ||
            (ins.opcode == ABVM_OP_RPKG_ENTER &&
             !constant_at(vm, ins.operand_a,ABVM_CONST_RANGES,&payload,&size)) ||
            (ins.opcode == ABVM_OP_SCOPE_BEGIN &&
             !constant_at(vm, ins.operand_a,ABVM_CONST_SCOPE,&payload,&size)))
            return fail(vm, "constant reference");
        if ((ins.opcode == ABVM_OP_LOOP_ENTER ||
             ins.opcode == ABVM_OP_RPKG_ENTER ||
             ins.opcode == ABVM_OP_SCOPE_BEGIN ||
             ins.opcode == ABVM_OP_WATCH) &&
            (ins.operand_d <= pc || ins.operand_d > h->code_count))
            return fail(vm, "forward target");
        if (ins.opcode == ABVM_OP_JUMP && ins.operand_d >= h->code_count)
            return fail(vm, "jump target");
        if (ins.opcode == ABVM_OP_LOOP_ENTER) {
            AbvmInstruction tail;
            if (!instruction_at(vm, ins.operand_d - 1u, &tail) ||
                tail.opcode != ABVM_OP_LOOP_NEXT ||
                tail.operand_b != pc + 1u)
                return fail(vm, "loop descriptor");
        }
        if (ins.opcode == ABVM_OP_RPKG_ENTER) {
            uint16_t count = size >= 2u ? read_u16(payload) : 0;
            if (!count || count > ABVM_MAX_PACKAGE_ITEMS ||
                size != 2u + (uint32_t)count * 8u)
                return fail(vm, "package descriptor");
            for (uint16_t item = 0; item < count; ++item) {
                uint32_t first=read_u32(payload+2u+(uint32_t)item*8u);
                uint32_t last=read_u32(payload+6u+(uint32_t)item*8u);
                AbvmInstruction tail;
                if (first <= pc || first > last || last >= ins.operand_d ||
                    !instruction_at(vm,last,&tail) ||
                    tail.opcode != ABVM_OP_ITEM_END)
                    return fail(vm, "package range");
            }
        }
        if (ins.opcode == ABVM_OP_SCOPE_BEGIN) {
            if (size != 20u || payload[0] != 2u ||
                payload[1] != ABVM_SCOPE_CANCEL_ON_TERMINAL_LANE ||
                payload[2] >= 2u)
                return fail(vm, "scope descriptor");
            for (uint8_t lane = 0; lane < 2u; ++lane) {
                uint32_t first=read_u32(payload+4u+(uint32_t)lane*8u);
                uint32_t last=read_u32(payload+8u+(uint32_t)lane*8u);
                AbvmInstruction tail;
                if (first <= pc || first > last || last >= ins.operand_d ||
                    !instruction_at(vm,last,&tail) ||
                    tail.opcode != ABVM_OP_LANE_END ||
                    !!(tail.flags & 1u) != (lane == payload[2]))
                    return fail(vm, "scope range");
                for (uint32_t nested = first; nested < last; ++nested) {
                    AbvmInstruction child;
                    if (!instruction_at(vm,nested,&child) ||
                        child.opcode == ABVM_OP_SCOPE_BEGIN)
                        return fail(vm, "nested scope");
                }
            }
        }
        if (ins.opcode == ABVM_OP_WATCH) {
            if (ins.operand_b > ins.operand_c)
                return fail(vm, "watch descriptor");
            if (ins.flags == 3u) {
                if (!constant_at(vm, ins.operand_a, ABVM_CONST_LIGHT,
                                 &payload, &size) || size != 16u ||
                    read_u32(payload) > read_u32(payload + 4u) ||
                    read_u32(payload + 4u) > 1000000u ||
                    read_u32(payload + 8u) > 3600000u || payload[12] > 1u)
                    return fail(vm, "light descriptor");
            } else if (ins.flags == 2u) {
                if (!constant_at(vm, ins.operand_a, ABVM_CONST_SOUND,
                                 &payload, &size) || size != 8u ||
                    !read_u16(payload) || !read_u16(payload + 2u) ||
                    read_u16(payload + 2u) > 1023u ||
                    !read_u32(payload + 4u) ||
                    read_u32(payload + 4u) > 65535u)
                    return fail(vm, "sound descriptor");
            } else if (ins.flags != 1u || !ins.operand_a) {
                return fail(vm, "watch descriptor version");
            }
            for (uint32_t nested=pc+1u;nested<ins.operand_d;++nested) {
                AbvmInstruction child;
                if (!instruction_at(vm,nested,&child) ||
                    child.opcode == ABVM_OP_WATCH)
                    return fail(vm, "nested watch");
            }
        }
    }

    uint32_t previous_end = 0;
    for (uint32_t i = 0; i < h->route_count; ++i) {
        AbvmRoute route;
        uint16_t policy;
        if (!route_at(vm, i, &route) || !route.length ||
            route.pc + route.length > h->code_count ||
            (route.policy_flags & ~ABVM_ROUTE_ALLOWED_FLAGS) ||
            !(route.policy_flags & ABVM_ROUTE_PAUSE_RELEASE_HID))
            return fail(vm, "route");
        policy = route.policy_flags & ABVM_ROUTE_POLICY_MASK;
        if (policy > ABVM_ROUTE_ABORT_AND_RESTART)
            return fail(vm, "route policy");
        if (i && route.pc < previous_end)
            return fail(vm, "route overlap");
        previous_end = route.pc + route.length;
        AbvmInstruction last;
        if (!instruction_at(vm, route.pc + route.length - 1u, &last) ||
            last.opcode != ABVM_OP_END)
            return fail(vm, "route end");
    }
    return 1;
}

int abvm_init(AbvmVm *vm, const uint8_t *image, size_t image_size) {
    if (!vm || !image || image_size < ABVM_HEADER_SIZE) return 0;
    memset(vm, 0, sizeof(*vm));
    vm->image = image;
    vm->image_size = image_size;
    memcpy(&vm->header, image, sizeof(vm->header));
    vm->prng = 1u;
    vm->status = ABVM_STATUS_IDLE;
    return verify_image(vm);
}

static int load_route(AbvmVm *vm, const AbvmRoute *route, uint32_t now) {
    memset(vm->lanes, 0, sizeof(vm->lanes));
    memset(&vm->scope, 0, sizeof(vm->scope));
    vm->route_id = route->route_id;
    vm->route_flags = route->policy_flags;
    vm->lane_count = 1;
    vm->lanes[0].pc = route->pc;
    vm->lanes[0].end_pc = route->pc + route->length;
    vm->lanes[0].due = now;
    vm->lanes[0].active = 1;
    vm->now = now;
    vm->status = ABVM_STATUS_RUNNING;
    return 1;
}

int abvm_start_route(AbvmVm *vm, uint16_t route_id, uint32_t now) {
    AbvmRoute route;
    if (!vm || vm->status == ABVM_STATUS_FAULT ||
        !find_route(vm, route_id, &route)) return 0;
    memset(&vm->suspended, 0, sizeof(vm->suspended));
    vm->pending_release = 1;
    return load_route(vm, &route, now);
}

int abvm_interrupt_route(AbvmVm *vm, uint16_t route_id, uint32_t now) {
    AbvmRoute route;
    if (!vm || vm->status != ABVM_STATUS_RUNNING || vm->suspended.valid ||
        !find_route(vm, route_id, &route) ||
        (route.policy_flags & ABVM_ROUTE_POLICY_MASK) !=
            ABVM_ROUTE_INTERRUPT_AND_RESUME)
        return 0;
    vm->suspended.valid = 1;
    vm->suspended.lane_count = vm->lane_count;
    vm->suspended.route_flags = vm->route_flags;
    vm->suspended.route_id = vm->route_id;
    memcpy(vm->suspended.lanes, vm->lanes, sizeof(vm->lanes));
    memcpy(&vm->suspended.scope, &vm->scope, sizeof(vm->scope));
    vm->pending_release = 1;
    return load_route(vm, &route, now);
}

int abvm_pause(AbvmVm *vm, uint32_t now) {
    if (!vm || vm->status != ABVM_STATUS_RUNNING) return 0;
    vm->status = ABVM_STATUS_PAUSED;
    vm->paused_at = now;
    vm->now = now;
    vm->pending_release = 1;
    return 1;
}

static void shift_deadlines(AbvmVm *vm, uint32_t delta) {
    for (uint8_t i = 0; i < ABVM_MAX_LANES; ++i) {
        AbvmLane *lane = &vm->lanes[i];
        lane->due += delta;
        if (lane->blocked == ABVM_BLOCK_WATCH) lane->watch_deadline += delta;
        for (uint8_t j = 0; j < lane->frame_count; ++j)
            if (lane->frames[j].type == ABVM_FRAME_LOOP &&
                lane->frames[j].mode)
                lane->frames[j].deadline += delta;
    }
}

int abvm_resume(AbvmVm *vm, uint32_t now) {
    if (!vm || vm->status != ABVM_STATUS_PAUSED) return 0;
    if ((vm->route_flags & ABVM_ROUTE_CLOCK_MASK) ==
        ABVM_ROUTE_CLOCK_ACTIVE)
        shift_deadlines(vm, now - vm->paused_at);
    vm->now = now;
    vm->status = ABVM_STATUS_RUNNING;
    return 1;
}

void abvm_stop(AbvmVm *vm, uint32_t now) {
    if (!vm) return;
    vm->now = now;
    memset(vm->lanes, 0, sizeof(vm->lanes));
    memset(&vm->scope, 0, sizeof(vm->scope));
    memset(&vm->suspended, 0, sizeof(vm->suspended));
    vm->status = ABVM_STATUS_STOPPED;
    vm->pending_release = 1;
}

static AbvmEvent event_of(AbvmVm *vm, uint8_t type, uint8_t lane,
                          const AbvmInstruction *ins, const char *message) {
    AbvmEvent event;
    memset(&event, 0, sizeof(event));
    event.type = type;
    event.lane = lane;
    event.route_id = vm->route_id;
    event.message = message;
    if (ins) {
        event.opcode=ins->opcode; event.flags=ins->flags;
        event.operand_a=ins->operand_a; event.constant_id=ins->operand_a;
        event.operand_b=ins->operand_b; event.operand_c=ins->operand_c;
        event.operand_d=ins->operand_d;
    }
    return event;
}

static int push_frame(AbvmVm *vm, AbvmLane *lane, AbvmFrame **frame) {
    if (lane->frame_count >= vm->resources.max_frames ||
        lane->frame_count >= ABVM_MAX_FRAMES)
        return fail(vm, "frame overflow");
    *frame = &lane->frames[lane->frame_count++];
    memset(*frame, 0, sizeof(**frame));
    return 1;
}

static int package_select(AbvmVm *vm, AbvmLane *lane, AbvmFrame *frame) {
    if (!frame->selected_mask) return 0;
    uint8_t selected = 0;
    if (frame->mode == 1u) {
        uint8_t choices = 0;
        for (uint8_t i=0;i<frame->item_count;++i)
            if (frame->selected_mask & (1u<<i)) ++choices;
        uint8_t pick = (uint8_t)(random_next(vm) % choices);
        for (uint8_t i=0;i<frame->item_count;++i)
            if ((frame->selected_mask & (1u<<i)) && !pick--) {
                selected=i; break;
            }
    } else {
        while (!(frame->selected_mask & (1u<<selected))) ++selected;
    }
    frame->selected_mask &= ~(1u << selected);
    frame->next_item = selected;
    const uint8_t *payload;
    uint32_t size;
    if (!constant_at(vm, frame->constant_id, ABVM_CONST_RANGES,
                     &payload, &size))
        return fail(vm, "package constant");
    lane->pc = read_u32(payload + 2u + (uint32_t)selected * 8u);
    return 1;
}

static void finish_lane(AbvmVm *vm, uint8_t lane_index) {
    AbvmLane *lane = &vm->lanes[lane_index];
    lane->active = 0;
    if (!vm->scope.active) return;
    int resume = vm->scope.policy == ABVM_SCOPE_CANCEL_ON_ANY ||
        (vm->scope.policy == ABVM_SCOPE_CANCEL_ON_TERMINAL_LANE &&
         lane_index == vm->scope.terminal_lane);
    if (vm->scope.policy == ABVM_SCOPE_JOIN_ALL &&
        !vm->lanes[0].active && !vm->lanes[1].active)
        resume = 1;
    if (!resume) return;
    uint8_t parent_index = vm->scope.parent_lane;
    uint8_t other_index = (uint8_t)(1u - parent_index);
    memset(&vm->lanes[other_index], 0, sizeof(vm->lanes[other_index]));
    lane = &vm->lanes[parent_index];
    lane->active = 1;
    lane->blocked = ABVM_BLOCK_NONE;
    lane->pc = vm->scope.parent_pc;
    lane->end_pc = vm->scope.parent_end_pc;
    lane->frame_count = vm->scope.parent_frame_count;
    lane->due = vm->now;
    vm->lane_count = 1;
    memset(&vm->scope, 0, sizeof(vm->scope));
}

AbvmEvent abvm_tick(AbvmVm *vm, uint32_t now) {
    if (!vm) {
        AbvmEvent empty; memset(&empty,0,sizeof(empty)); return empty;
    }
    vm->now = now;
    if (vm->pending_release) {
        vm->pending_release = 0;
        return event_of(vm, ABVM_EVENT_RELEASE_ALL, 0, 0, "release-all");
    }
    if (vm->status == ABVM_STATUS_FAULT)
        return event_of(vm, ABVM_EVENT_FAULT, 0, 0, vm->fault);
    if (vm->status != ABVM_STATUS_RUNNING)
        return event_of(vm, ABVM_EVENT_NONE, 0, 0, 0);

    uint8_t active = 0;
    for (uint8_t i = 0; i < ABVM_MAX_LANES; ++i) {
        AbvmLane *lane = &vm->lanes[i];
        if (!lane->active) continue;
        ++active;
        if (lane->blocked == ABVM_BLOCK_WATCH &&
            time_reached(now, lane->watch_deadline)) {
            lane->blocked = ABVM_BLOCK_NONE;
            lane->pc = lane->watch_after_pc;
            lane->due = now;
        }
    }
    if (!active) {
        if (vm->suspended.valid) {
            vm->lane_count = vm->suspended.lane_count;
            vm->route_flags = vm->suspended.route_flags;
            vm->route_id = vm->suspended.route_id;
            memcpy(vm->lanes, vm->suspended.lanes, sizeof(vm->lanes));
            memcpy(&vm->scope, &vm->suspended.scope, sizeof(vm->scope));
            memset(&vm->suspended, 0, sizeof(vm->suspended));
            return event_of(vm, ABVM_EVENT_INTERRUPT_RESUME, 0, 0,
                            "interrupt-resume");
        }
        vm->status = ABVM_STATUS_COMPLETE;
        return event_of(vm, ABVM_EVENT_ROUTE_COMPLETE, 0, 0,
                        "route-complete");
    }

    for (uint8_t lane_index = 0; lane_index < ABVM_MAX_LANES; ++lane_index) {
        AbvmLane *lane = &vm->lanes[lane_index];
        if (!lane->active || lane->blocked != ABVM_BLOCK_NONE ||
            !time_reached(now, lane->due))
            continue;
        AbvmInstruction ins;
        if (!instruction_at(vm, lane->pc, &ins)) {
            fail(vm, "pc");
            return event_of(vm,ABVM_EVENT_FAULT,lane_index,0,vm->fault);
        }
        switch (ins.opcode) {
            case ABVM_OP_END:
            case ABVM_OP_LANE_END:
                finish_lane(vm, lane_index);
                break;
            case ABVM_OP_DELAY:
                lane->pc++;
                lane->due = now + random_range(vm,ins.operand_b,ins.operand_c);
                break;
            case ABVM_OP_KEY: case ABVM_OP_KDOWN: case ABVM_OP_KUP:
            case ABVM_OP_TYPE: case ABVM_OP_RMOUSE:
                lane->pc++;
                lane->blocked = ABVM_BLOCK_ACTION;
                return event_of(vm,ABVM_EVENT_ACTION,lane_index,&ins,"action");
            case ABVM_OP_LOOP_ENTER: {
                AbvmFrame *frame;
                if (!push_frame(vm,lane,&frame)) break;
                frame->type=ABVM_FRAME_LOOP; frame->mode=ins.flags;
                frame->body_pc=lane->pc+1u; frame->after_pc=ins.operand_d;
                frame->remaining=ins.flags ? 0u : ins.operand_b;
                frame->deadline=ins.flags ? now+ins.operand_b : 0u;
                lane->pc++;
                break;
            }
            case ABVM_OP_LOOP_NEXT: {
                if (!lane->frame_count ||
                    lane->frames[lane->frame_count-1u].type!=ABVM_FRAME_LOOP) {
                    fail(vm,"loop frame"); break;
                }
                AbvmFrame *frame=&lane->frames[lane->frame_count-1u];
                int again=frame->mode ? !time_reached(now,frame->deadline) :
                    (!frame->remaining || frame->remaining>1u);
                if (again) {
                    if (frame->remaining) --frame->remaining;
                    lane->pc=frame->body_pc;
                } else {
                    --lane->frame_count; lane->pc++;
                }
                break;
            }
            case ABVM_OP_RPKG_ENTER: {
                const uint8_t *payload; uint32_t size; AbvmFrame *frame;
                if (!constant_at(vm,ins.operand_a,ABVM_CONST_RANGES,
                                 &payload,&size) ||
                    !push_frame(vm,lane,&frame)) break;
                frame->type=ABVM_FRAME_PACKAGE; frame->mode=ins.flags;
                frame->constant_id=ins.operand_a; frame->after_pc=ins.operand_d;
                frame->item_count=(uint8_t)read_u16(payload);
                uint32_t all=frame->item_count==32u ? 0xffffffffu :
                             (1u<<frame->item_count)-1u;
                if (ins.flags==2u) {
                    uint32_t take=random_range(vm,ins.operand_b,ins.operand_c);
                    frame->selected_mask=0;
                    while (take-- && frame->selected_mask!=all)
                        frame->selected_mask |=
                            1u<<(random_next(vm)%frame->item_count);
                } else frame->selected_mask=all;
                if (!package_select(vm,lane,frame)) lane->pc=ins.operand_d;
                break;
            }
            case ABVM_OP_ITEM_END: {
                if (!lane->frame_count ||
                    lane->frames[lane->frame_count-1u].type!=
                        ABVM_FRAME_PACKAGE) {
                    fail(vm,"package frame"); break;
                }
                AbvmFrame *frame=&lane->frames[lane->frame_count-1u];
                if (!package_select(vm,lane,frame)) {
                    lane->pc=frame->after_pc; --lane->frame_count;
                }
                break;
            }
            case ABVM_OP_SCOPE_BEGIN: {
                const uint8_t *payload; uint32_t size;
                if (vm->scope.active ||
                    !constant_at(vm,ins.operand_a,ABVM_CONST_SCOPE,
                                 &payload,&size)) { fail(vm,"scope"); break; }
                vm->scope.active=1; vm->scope.policy=payload[1];
                vm->scope.terminal_lane=payload[2];
                vm->scope.parent_lane=lane_index;
                vm->scope.parent_frame_count=lane->frame_count;
                vm->scope.parent_pc=ins.operand_d;
                vm->scope.parent_end_pc=lane->end_pc;
                uint32_t start0=read_u32(payload+4u);
                uint32_t end0=read_u32(payload+8u);
                uint32_t start1=read_u32(payload+12u);
                uint32_t end1=read_u32(payload+16u);
                lane->pc=start0; lane->end_pc=end0+1u; lane->due=now;
                lane->terminal=payload[2]==lane_index;
                memset(&vm->lanes[1],0,sizeof(vm->lanes[1]));
                vm->lanes[1].pc=start1; vm->lanes[1].end_pc=end1+1u;
                vm->lanes[1].due=now; vm->lanes[1].active=1;
                vm->lanes[1].terminal=payload[2]==1u;
                vm->lane_count=2;
                break;
            }
            case ABVM_OP_WATCH: {
                uint16_t profile = ins.operand_a;
                uint8_t kind = 0u;
                if (ins.flags == 3u) {
                    const uint8_t *payload; uint32_t size;
                    if (!constant_at(vm, ins.operand_a, ABVM_CONST_LIGHT,
                                     &payload, &size) || size != 16u) {
                        fail(vm, "light descriptor"); break;
                    }
                    profile = 0u; kind = ABVM_CONST_LIGHT;
                } else if (ins.flags == 2u) {
                    const uint8_t *payload; uint32_t size;
                    if (!constant_at(vm, ins.operand_a, ABVM_CONST_SOUND,
                                     &payload, &size) || size != 8u) {
                        fail(vm, "sound descriptor"); break;
                    }
                    profile = read_u16(payload); kind = ABVM_CONST_SOUND;
                } else kind = ABVM_CONST_SOUND;
                lane->blocked=ABVM_BLOCK_WATCH;
                lane->watch_profile=profile;
                lane->watch_constant=ins.operand_a;
                lane->watch_kind=kind;
                lane->watch_after_pc=ins.operand_d;
                lane->watch_deadline=now+
                    random_range(vm,ins.operand_b,ins.operand_c);
                AbvmEvent event=event_of(vm,ABVM_EVENT_WATCH_ARMED,
                                         lane_index,&ins,"watch-armed");
                event.operand_a=profile;
                event.operand_b=lane->watch_deadline-now;
                event.operand_c=event.operand_b;
                return event;
            }
            case ABVM_OP_JUMP:
                lane->pc=ins.operand_d;
                break;
            default:
                fail(vm,"dispatch opcode");
                break;
        }
        if (vm->status==ABVM_STATUS_FAULT)
            return event_of(vm,ABVM_EVENT_FAULT,lane_index,&ins,vm->fault);
        return event_of(vm,ABVM_EVENT_NONE,lane_index,0,0);
    }
    return event_of(vm, ABVM_EVENT_NONE, 0, 0, 0);
}

int abvm_complete_action(AbvmVm *vm, uint8_t lane, uint32_t now) {
    if (!vm || lane>=ABVM_MAX_LANES ||
        vm->lanes[lane].blocked!=ABVM_BLOCK_ACTION) return 0;
    vm->lanes[lane].blocked=ABVM_BLOCK_NONE;
    vm->lanes[lane].due=now;
    return 1;
}

int abvm_constant(const AbvmVm *vm, uint16_t constant_id, uint8_t kind,
                  const uint8_t **payload, uint32_t *size) {
    if (!vm || !payload || !size) return 0;
    return constant_at(vm, constant_id, kind, payload, size);
}

int abvm_sound_detected(AbvmVm *vm, uint16_t profile, uint32_t now) {
    if (!vm) return 0;
    for (uint8_t i=0;i<ABVM_MAX_LANES;++i) {
        AbvmLane *lane=&vm->lanes[i];
        if (lane->active && lane->blocked==ABVM_BLOCK_WATCH &&
            lane->watch_kind==ABVM_CONST_SOUND && lane->watch_profile==profile) {
            lane->blocked=ABVM_BLOCK_NONE;
            lane->pc++;
            lane->due=now;
            return 1;
        }
    }
    return 0;
}

int abvm_light_detected(AbvmVm *vm, uint8_t lane_index,
                        uint16_t constant_id, uint32_t now) {
    if (!vm || lane_index >= ABVM_MAX_LANES) return 0;
    AbvmLane *lane = &vm->lanes[lane_index];
    if (!lane->active || lane->blocked != ABVM_BLOCK_WATCH ||
        lane->watch_kind != ABVM_CONST_LIGHT ||
        lane->watch_constant != constant_id) return 0;
    lane->blocked = ABVM_BLOCK_NONE;
    lane->pc++;
    lane->due = now;
    return 1;
}

const char *abvm_status_name(uint8_t status) {
    static const char *names[] = {
        "idle","running","paused","stopped","complete","fault"
    };
    return status < sizeof(names)/sizeof(names[0]) ? names[status] : "unknown";
}