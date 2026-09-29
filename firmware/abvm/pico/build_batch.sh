#!/usr/bin/env bash
set -Eeuo pipefail
if [[ $# -lt 3 ]]; then
  echo "usage: $0 PROGRAM.abp BATCH.json OUTPUT_DIR [PICO1_ROOT]" >&2
  exit 2
fi
PROGRAM="$(realpath "$1")"
BATCH="$(realpath "$2")"
OUTPUT_DIR="$(mkdir -p "$3" && realpath "$3")"
PICO1_ROOT="${4:-}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIGS="$OUTPUT_DIR/.configs"
rm -rf "$CONFIGS"; mkdir -p "$CONFIGS"
python3 - "$BATCH" "$CONFIGS" <<'PY'
import json, pathlib, sys
batch=json.loads(pathlib.Path(sys.argv[1]).read_text())
profiles=batch.get("profiles", [])
defaults=batch.get("defaults", {})
if not profiles: raise SystemExit("batch has no profiles")
for i,profile in enumerate(profiles,1):
    cfg={**defaults, **profile}
    name=cfg["target_board"]
    pathlib.Path(sys.argv[2], f"{i:02d}-{name}.json").write_text(
        json.dumps(cfg, indent=2, sort_keys=True)+"\n")
PY
count=0
for config in "$CONFIGS"/*.json; do
  name="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["target_board"])' "$config")"
  build="$OUTPUT_DIR/.build-$name"
  args=(-S "$ROOT" -B "$build" -G Ninja -DPICO_BOARD=pico
        -DABVM_PROGRAM="$PROGRAM" -DABVM_FIRMWARE_CONFIG="$config")
  [[ -z "$PICO1_ROOT" ]] || args+=(-DPICO1_ROOT="$PICO1_ROOT")
  cmake "${args[@]}"; cmake --build "$build" --parallel
  mkdir -p "$OUTPUT_DIR/$name"
  cp "$build/ams_abvm_pico.uf2" "$OUTPUT_DIR/$name/$name.uf2"
  cp "$build/pico1/"{build-config.json,build-manifest.json,abvm-firmware-manifest.json} "$OUTPUT_DIR/$name/"
  (cd "$OUTPUT_DIR/$name" && sha256sum "$name.uf2" build-config.json build-manifest.json abvm-firmware-manifest.json > SHA256SUMS.txt)
  count=$((count + 1))
done
echo "Built $count personalized native UF2 bundles in $OUTPUT_DIR"
