#ifndef AMS_ABVM_MIRROR_FRAME_H
#define AMS_ABVM_MIRROR_FRAME_H

#include <stdbool.h>
#include <stdint.h>

#define MIRROR_FRAME_HEADER_SIZE 11u
#define MIRROR_FRAME_MAX_PAYLOAD 16u
#define MIRROR_FRAME_CRC_SIZE 2u
#define MIRROR_FRAME_MAX_SIZE \
    (MIRROR_FRAME_HEADER_SIZE + MIRROR_FRAME_MAX_PAYLOAD + MIRROR_FRAME_CRC_SIZE)

typedef struct MirrorFrame {
    uint8_t type;
    uint8_t payload_length;
    uint16_t sequence;
    uint32_t timestamp_us;
    uint8_t payload[MIRROR_FRAME_MAX_PAYLOAD];
} MirrorFrame;

typedef struct MirrorFrameParser {
    uint8_t bytes[MIRROR_FRAME_MAX_SIZE];
    uint8_t count;
    uint8_t expected_size;
} MirrorFrameParser;

void mirror_frame_parser_reset(MirrorFrameParser *parser);
bool mirror_frame_parser_feed(MirrorFrameParser *parser, uint8_t byte,
                             MirrorFrame *frame);
bool mirror_frame_valid_event(const MirrorFrame *frame);

#endif