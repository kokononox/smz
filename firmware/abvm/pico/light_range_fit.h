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

/*
 * Physical calibration has one newly measured, explicitly selected profile.
 * Keep that profile's requested range intact; move only the facing edge(s) of
 * neighboring profiles. If a neighbor's measured centre would be excluded,
 * the requested range cannot be represented without overlap, so fail unchanged.
 * Any remaining collisions between non-selected profiles use the regular fit.
 */
static inline bool light_ranges_fit_preserving_selected(
    uint32_t low[9], uint32_t high[9], const uint32_t center[9],
    uint8_t selected, uint16_t *changed) {
    if(selected>=9u)return false;
    uint32_t lo[9],hi[9];
    uint16_t mask=0u;
    for(uint8_t i=0;i<9u;i++) {
        if(low[i]>center[i]||center[i]>high[i]||high[i]>1000000u)return false;
        lo[i]=low[i];hi[i]=high[i];
    }

    const uint32_t selected_low=lo[selected];
    const uint32_t selected_high=hi[selected];
    for(uint8_t i=0;i<9u;i++) {
        if(i==selected)continue;
        if(center[i]<center[selected]) {
            if(hi[i]>=selected_low) {
                if(!selected_low)return false;
                uint32_t fitted_high=selected_low-1u;
                if(fitted_high<center[i])return false;
                if(fitted_high<hi[i]) {
                    hi[i]=fitted_high;
                    mask|=(uint16_t)(1u<<i);
                }
            }
        } else if(center[i]>center[selected]) {
            if(lo[i]<=selected_high) {
                if(selected_high>=1000000u)return false;
                uint32_t fitted_low=selected_high+1u;
                if(fitted_low>center[i])return false;
                if(fitted_low>lo[i]) {
                    lo[i]=fitted_low;
                    mask|=(uint16_t)(1u<<i);
                }
            }
        }
    }

    uint16_t other_changes=0u;
    if(!light_ranges_fit(lo,hi,center,&other_changes))return false;
    if(lo[selected]!=selected_low||hi[selected]!=selected_high)return false;
    mask|=other_changes;
    for(uint8_t i=0;i<9u;i++) {
        low[i]=lo[i];
        high[i]=hi[i];
    }
    if(changed)*changed=mask;
    return true;
}
#endif
