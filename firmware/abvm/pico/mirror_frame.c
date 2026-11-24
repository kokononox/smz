#include "mirror_frame.h"

#include <string.h>

#define FRAME_SYNC0 0xA5u
#define FRAME_SYNC1 0x5Au
#define FRAME_VERSION 1u
#define FRAME_TYPE_EVENT 1u
#define EVENT_MOUSE_MOVE 0x02u
#define EVENT_MOUSE_BUTTON 0x03u
#define EVENT_MOUSE_WHEEL 0x04u
#define EVENT_HEARTBEAT 0x05u
#define EVENT_STATE_SNAPSHOT 0x07u

static uint16_t get_u16_le(const uint8_t *p) {
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static uint32_t get_u32_le(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

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

void mirror_frame_parser_reset(MirrorFrameParser *parser) {
    if (parser) memset(parser, 0, sizeof(*parser));
}

bool mirror_frame_parser_feed(MirrorFrameParser *parser, uint8_t byte,
                              MirrorFrame *frame) {
    if (!parser || !frame) return false;
    if (parser->count == 0u) {
        if (byte == FRAME_SYNC0) {
            parser->bytes[0] = byte;
            parser->count = 1u;
        }
        return false;
    }
    if (parser->count == 1u) {
        if (byte == FRAME_SYNC1) {
            parser->bytes[1] = byte;
            parser->count = 2u;
        } else if (byte == FRAME_SYNC0) {
            parser->bytes[0] = byte;
        } else {
            parser->count = 0u;
        }
        return false;
    }

    if (parser->count >= MIRROR_FRAME_MAX_SIZE) {
        mirror_frame_parser_reset(parser);
        return false;
    }
    parser->bytes[parser->count++] = byte;
    if (parser->count == 7u) {
        const uint8_t payload_length = parser->bytes[6];
        if (payload_length > MIRROR_FRAME_MAX_PAYLOAD) {
            mirror_frame_parser_reset(parser);
            return false;
        }
        parser->expected_size =
            (uint8_t)(MIRROR_FRAME_HEADER_SIZE + payload_length +
                      MIRROR_FRAME_CRC_SIZE);
    }
    if (parser->expected_size == 0u ||
        parser->count < parser->expected_size) return false;

    const uint8_t payload_length = parser->bytes[6];
    const uint16_t received_crc =
        get_u16_le(&parser->bytes[MIRROR_FRAME_HEADER_SIZE + payload_length]);
    const uint16_t calculated_crc =
        crc16_ccitt(&parser->bytes[2], 9u + payload_length);
    const bool valid = parser->bytes[2] == FRAME_VERSION &&
                       received_crc == calculated_crc;
    if (valid) {
        frame->type = parser->bytes[3];
        frame->sequence = get_u16_le(&parser->bytes[4]);
        frame->payload_length = payload_length;
        frame->timestamp_us = get_u32_le(&parser->bytes[7]);
        if (payload_length != 0u) {
            memcpy(frame->payload,
                   &parser->bytes[MIRROR_FRAME_HEADER_SIZE], payload_length);
        }
    }
    mirror_frame_parser_reset(parser);
    return valid;
}

bool mirror_frame_valid_event(const MirrorFrame *frame) {
    if (!frame || frame->type != FRAME_TYPE_EVENT ||
        frame->payload_length == 0u) return false;
    switch (frame->payload[0]) {
        case EVENT_MOUSE_MOVE:
            return frame->payload_length == 5u;
        case EVENT_MOUSE_BUTTON:
            return frame->payload_length == 3u &&
                   frame->payload[1] >= 1u && frame->payload[1] <= 5u &&
                   frame->payload[2] <= 1u;
        case EVENT_MOUSE_WHEEL:
            return frame->payload_length == 4u && frame->payload[3] <= 1u;
        case EVENT_HEARTBEAT:
            return frame->payload_length == 1u;
        case EVENT_STATE_SNAPSHOT:
            if (frame->payload_length != 9u ||
                (frame->payload[8] & 0xE0u) != 0u) return false;
            for (unsigned i = 2u; i < 8u; ++i) {
                if (frame->payload[i] >= 0xE0u) return false;
            }
            return true;
        default:
            return false;
    }
}