#include "mirror_frame.h"

#include <assert.h>
#include <stdio.h>
#include <string.h>

static uint16_t crc16_ccitt(const uint8_t *data, size_t length) {
    uint16_t crc = 0xFFFFu;
    for (size_t i = 0; i < length; ++i) {
        crc ^= (uint16_t)data[i] << 8;
        for (uint8_t bit = 0; bit < 8u; ++bit) {
            crc = (crc & 0x8000u) != 0u
                ? (uint16_t)((crc << 1) ^ 0x1021u)
                : (uint16_t)(crc << 1);
        }
    }
    return crc;
}

static size_t make_frame(uint8_t *out, const uint8_t *payload,
                         uint8_t payload_length) {
    out[0] = 0xA5u;
    out[1] = 0x5Au;
    out[2] = 1u;
    out[3] = 1u;
    out[4] = 1u;
    out[5] = 0u;
    out[6] = payload_length;
    out[7] = 0x78u;
    out[8] = 0x56u;
    out[9] = 0x34u;
    out[10] = 0x12u;
    memcpy(&out[11], payload, payload_length);
    const uint16_t crc = crc16_ccitt(&out[2], 9u + payload_length);
    out[11u + payload_length] = (uint8_t)crc;
    out[12u + payload_length] = (uint8_t)(crc >> 8);
    return 13u + payload_length;
}

int main(void) {
    MirrorFrameParser parser;
    MirrorFrame frame;
    uint8_t bytes[MIRROR_FRAME_MAX_SIZE];
    const uint8_t snapshot[] = {0x07u, 0x02u, 0x04u, 0u, 0u, 0u, 0u, 0u, 1u};
    const size_t length = make_frame(bytes, snapshot, sizeof(snapshot));

    mirror_frame_parser_reset(&parser);
    bool complete = false;
    for (size_t i = 0; i < length; ++i) {
        if (mirror_frame_parser_feed(&parser, bytes[i], &frame)) complete = true;
    }
    assert(complete);
    assert(frame.sequence == 1u);
    assert(frame.timestamp_us == 0x12345678u);
    assert(mirror_frame_valid_event(&frame));

    bytes[length - 1u] ^= 0x80u;
    mirror_frame_parser_reset(&parser);
    for (size_t i = 0; i < length; ++i) {
        assert(!mirror_frame_parser_feed(&parser, bytes[i], &frame));
    }

    const uint8_t bad_button[] = {0x03u, 6u, 1u};
    frame.type = 1u;
    frame.payload_length = sizeof(bad_button);
    memcpy(frame.payload, bad_button, sizeof(bad_button));
    assert(!mirror_frame_valid_event(&frame));

    puts("mirror frame parser smoke: PASS");
    return 0;
}