from pathlib import Path
import sys
import re

from grpc_tools import protoc
from setuptools.command.build_py import build_py as _build_py


class build_py(_build_py):
    def run(self):
        generate_protos()
        super().run()


def generate_protos() -> None:
    proto_dir = Path("protos")
    out_dir = Path("models")
    out_dir.mkdir(parents=True, exist_ok=True)

    for proto_file in proto_dir.glob("*.proto"):
        protoc.main([
            "grpc_tools.protoc",
            f"-I{proto_dir}",
            f"--python_out={out_dir}",
            f"--grpc_python_out={out_dir}",
            f"--pyi_out={out_dir}",
            str(proto_file),
        ])
    fix_relative_imports(out_dir)

def build_main() -> int:
    """CLI entry point to generate protos without running a full build."""
    generate_protos()
    return 0

def fix_relative_imports(out_dir: Path) -> None:
    """Make *_pb2_grpc.py (and any others) use package-relative imports."""
    for py_file in out_dir.glob("*.py"):
        text = py_file.read_text(encoding="utf-8")

        # Replace any top-level `import X_pb2 as X__pb2` with `from . import X_pb2 as X__pb2`
        new_text = re.sub(
            r'^import (\w+_pb2) as (\w+__pb2)\s*$',
            r'from . import \1 as \2',
            text,
            flags=re.MULTILINE,
        )

        if new_text != text:
            py_file.write_text(new_text, encoding="utf-8")

if __name__ == "__main__":
    sys.exit(build_main())
