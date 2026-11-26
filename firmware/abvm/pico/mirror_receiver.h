#ifndef AMS_ABVM_MIRROR_RECEIVER_H
#define AMS_ABVM_MIRROR_RECEIVER_H

#include <stdbool.h>

void mirror_receiver_init(void);
void mirror_receiver_start(void);
void mirror_receiver_stop(void);
bool mirror_receiver_active(void);
bool mirror_receiver_cleanup_pending(void);
void mirror_receiver_service(void);

#endif