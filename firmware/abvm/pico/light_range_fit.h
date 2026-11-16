#ifndef AMS_LIGHT_RANGE_FIT_H
#define AMS_LIGHT_RANGE_FIT_H
#include <stdbool.h>
#include <stdint.h>
/* Input units are tenths of a lux. Keep measured centres fixed; trim only
 * facing edges, preserving the outer edges. Adjacent inclusive ranges are
 * separated by one sensor unit. The operation changes the arrays only on
 * success, so a collision never damages an existing calibration. */
static inline bool light_ranges_fit(uint32_t low[9], uint32_t high[9],
                                    const uint32_t center[9], uint16_t *changed) {
    uint32_t lo[9],hi[9]; uint8_t order[9]; uint16_t mask=0;
    for(uint8_t i=0;i<9;i++) {
        if(low[i]>center[i]||center[i]>high[i]||high[i]>1000000u)return false;
        lo[i]=low[i];hi[i]=high[i];order[i]=i;
    }
    for(uint8_t i=1;i<9;i++) {
        uint8_t k=order[i],j=i;
        while(j&&center[order[j-1]]>center[k]) {order[j]=order[j-1];--j;}
        order[j]=k;
    }
    for(uint8_t i=1;i<9;i++) {
        uint8_t a=order[i-1],b=order[i];
        if(center[a]>=center[b])return false;
        if(hi[a]<lo[b])continue;
        /* Split inside the overlap, while retaining both measured centres. */
        uint32_t min_split=lo[b]?lo[b]-1u:0u;
        if(min_split<center[a])min_split=center[a];
        uint32_t max_split=hi[a];
        if(max_split>=center[b])max_split=center[b]-1u;
        if(min_split>max_split)return false;
        uint32_t split=center[a]+(center[b]-center[a])/2u;
        if(split<min_split)split=min_split;
        if(split>max_split)split=max_split;
        if(hi[a]>split){hi[a]=split;mask|=(uint16_t)(1u<<a);}
        if(lo[b]<=split){lo[b]=split+1u;mask|=(uint16_t)(1u<<b);}
    }
    for(uint8_t i=0;i<9;i++){low[i]=lo[i];high[i]=hi[i];}
    if(changed) *changed=mask;
    return true;
}
#endif
