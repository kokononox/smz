#!/usr/bin/env python3
"""Adapt pico1's validated firmware identity for the native ABVM Pico SDK UF2."""
from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

HEX16_RE = re.compile(r"^0x[0-9A-F]{4}$")
ASCII_RE = re.compile(r"^[\x20-\x7e]+$")


def _c_string(value: str) -> str:
    return json.dumps(value, ensure_ascii=True)


def _checked_identity(config: dict) -> dict:
    required = ("usb_manufacturer", "usb_product", "usb_vid", "usb_pid", "serial_prefix")
    missing = [key for key in required if key not in config]
    if missing:
        raise ValueError("pico1 validated config is missing: " + ", ".join(missing))
    for key in ("usb_manufacturer", "usb_product", "serial_prefix"):
        value = config[key]
        if not isinstance(value, str) or not value or not ASCII_RE.fullmatch(value):
            raise ValueError(f"{key} must be non-empty printable ASCII")
    for key in ("usb_vid", "usb_pid"):
        value = config[key]
        if not isinstance(value, str) or not HEX16_RE.fullmatch(value):
            raise ValueError(f"{key} must be canonical pico1 hex (0x0000..0xFFFF)")
    if len(config["serial_prefix"]) > 24:
        raise ValueError("serial_prefix exceeds pico1's 24-character contract")
    return config


def render_header(config: dict) -> str:
    cfg = _checked_identity(config)
    return "\n".join([
        "/* Generated from a pico1-validated configuration. Do not edit. */",
        "#ifndef ABVM_PICO1_CONFIG_H",
        "#define ABVM_PICO1_CONFIG_H",
        "",
        f"#define ABVM_USB_VID {cfg['usb_vid']}u",
        f"#define ABVM_USB_PID {cfg['usb_pid']}u",
        f"#define ABVM_USB_MANUFACTURER {_c_string(cfg['usb_manufacturer'])}",
        f"#define ABVM_USB_PRODUCT {_c_string(cfg['usb_product'])}",
        f"#define ABVM_USB_SERIAL_PREFIX {_c_string(cfg['serial_prefix'])}",
        '#define ABVM_USB_CDC_NAME "ABVM Diagnostics"',
        '#define ABVM_USB_HID_NAME "ABVM Keyboard"',
        "",
        "#endif",
        "",
    ])


def write_native_assets(validated_config: Path, pico1_manifest: Path,
                        program: Path, revision_file: Path, out_dir: Path) -> None:
    cfg = _checked_identity(json.loads(validated_config.read_text(encoding="utf-8")))
    upstream = json.loads(pico1_manifest.read_text(encoding="utf-8"))
    revision = revision_file.read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("PICO1_REVISION must contain one full lowercase commit SHA")
    out_dir.mkdir(parents=True, exist_ok=True)
    header = out_dir / "abvm_pico1_config.h"
    header.write_text(render_header(cfg), encoding="utf-8", newline="\n")

    program_bytes = program.read_bytes()
    if program_bytes[:4] != b"ABP1":
        raise ValueError("program is not an ABP1 image")
    canonical = json.dumps(cfg, ensure_ascii=True, indent=2, sort_keys=True) + "\n"
    audit = {
        "schema": 1,
        "backend": "pico-sdk-native-abvm",
        "pico1_revision": revision,
        "pico1_manifest": upstream,
        "configuration": cfg,
        "config_sha256": hashlib.sha256(canonical.encode()).hexdigest(),
        "program_sha256": hashlib.sha256(program_bytes).hexdigest(),
        "generated_header_sha256": hashlib.sha256(header.read_bytes()).hexdigest(),
        "applied_fields": [
            "usb_manufacturer", "usb_product", "usb_vid", "usb_pid", "serial_prefix"
        ],
        "not_applicable_to_native_backend": [
            "circuitpython_version", "source_board", "target_board", "drive_label",
            "maintenance_pin", "drive_mode", "runtime_profile", "language"
        ],
        "usb_classes": ["CDC", "HID keyboard"],
        "mass_storage_enabled": False,
    }
    (out_dir / "abvm-firmware-manifest.json").write_text(
        json.dumps(audit, ensure_ascii=True, indent=2, sort_keys=True) + "\n",
        encoding="utf-8", newline="\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--validated-config", type=Path, required=True)
    parser.add_argument("--pico1-manifest", type=Path, required=True)
    parser.add_argument("--program", type=Path, required=True)
    parser.add_argument("--revision-file", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    try:
        write_native_assets(args.validated_config, args.pico1_manifest, args.program,
                            args.revision_file, args.out)
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        parser.error(str(exc))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
