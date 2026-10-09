#!/usr/bin/env python3
"""Reject diagnostic UF2s overlapping the Pico's two calibration sectors.
Default is the conservative standard RP2040/Pico 2 MiB flash budget.
This validates the WHOLE template, not just the injected program length.
"""
import argparse
from pathlib import Path
try:
    from tools.abvm_uf2 import parse
except ModuleNotFoundError:
    from abvm_uf2 import parse

FLASH_BASE = 0x10000000
CALIBRATION_BYTES = 8192

def verify(data, flash_bytes=2*1024*1024):
    if flash_bytes <= CALIBRATION_BYTES:
        raise ValueError("flash budget is too small")
    limit = FLASH_BASE + flash_bytes - CALIBRATION_BYTES
    blocks = parse(data)
    for block in blocks:
        if block.address < FLASH_BASE or block.address+len(block.payload) > limit:
            raise ValueError("UF2 overlaps reserved calibration flash or exceeds Pico flash")
    end = max(block.address+len(block.payload) for block in blocks)
    return {"flash_end": end, "calibration_start": limit, "free_margin_bytes": limit-end}

def main():
    parser=argparse.ArgumentParser();parser.add_argument("uf2", type=Path)
    parser.add_argument("--flash-bytes", type=int, default=2*1024*1024)
    args=parser.parse_args();info=verify(args.uf2.read_bytes(), args.flash_bytes)
    print("Native flash guard OK:", info)
if __name__ == "__main__": main()
