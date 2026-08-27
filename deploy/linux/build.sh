#!/usr/bin/env bash
# Build and publish DfoGmTool for Linux.
# Requires the .NET 10 SDK. The default output is self-contained so it can be
# started directly by the accompanying systemd unit.
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
OUTPUT_DIR="$PROJECT_DIR/dist"
SELF_CONTAINED=true
RUNTIME_IDENTIFIER="linux-x64"

usage() {
  cat <<'EOF'
Usage: ./deploy/linux/build.sh [options]

Options:
  --framework-dependent  Do not bundle the .NET runtime in the publish output.
  --runtime <rid>        Target runtime identifier (default: linux-x64).
  --output <directory>   Publish directory (default: dist).
  -h, --help             Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --framework-dependent) SELF_CONTAINED=false ;;
    --runtime)
      RUNTIME_IDENTIFIER="${2:?--runtime requires a value}"
      shift
      ;;
    --output)
      OUTPUT_DIR="${2:?--output requires a value}"
      shift
      ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

command -v dotnet >/dev/null || {
  echo "dotnet SDK was not found. Install the .NET 10 SDK first." >&2
  exit 1
}

PUBLISH_ARGS=(
  publish "$PROJECT_DIR/DfoGmTool.csproj"
  --configuration Release
  --runtime "$RUNTIME_IDENTIFIER"
  --self-contained "$SELF_CONTAINED"
  --output "$OUTPUT_DIR"
  -p:DebugType=none
  -p:DebugSymbols=false
)

dotnet "${PUBLISH_ARGS[@]}"
printf 'Published to: %s\n' "$OUTPUT_DIR"
