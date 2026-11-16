"""Compile and run the physical-button light calibration tolerance contract."""
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
PICO = ROOT / "firmware/abvm/pico"
SMOKE = ROOT / "firmware/abvm/tests/abvm_calibration_tolerance_smoke.c"


def main() -> None:
    compiler = shutil.which("cc") or shutil.which("gcc") or shutil.which("clang")
    if compiler is None:
        raise SystemExit("a C11 compiler is required for the calibration tolerance smoke test")

    with tempfile.TemporaryDirectory() as temp:
        temp_path = Path(temp)
        stubs = temp_path / "stubs"
        stubs.mkdir()
        # Opaque VM/event types are sufficient for this host-side runtime contract.
        (stubs / "abvm_vm.h").write_text(
            "#ifndef TEST_ABVM_VM_H\n#define TEST_ABVM_VM_H\n"
            "#include <stdint.h>\ntypedef struct AbvmVm { uint8_t unused; } AbvmVm;\n"
            "typedef struct AbvmEvent { uint8_t unused; } AbvmEvent;\n#endif\n",
            encoding="utf-8",
        )
        binary = temp_path / "calibration-tolerance-smoke"
        subprocess.run(
            [
                compiler,
                "-std=c11",
                "-Wall",
                "-Wextra",
                "-Werror",
                "-I",
                str(stubs),
                "-I",
                str(PICO),
                str(PICO / "calibration_runtime.c"),
                str(SMOKE),
                "-o",
                str(binary),
            ],
            check=True,
            cwd=ROOT,
        )
        subprocess.run([str(binary)], check=True, cwd=ROOT)


if __name__ == "__main__":
    main()
