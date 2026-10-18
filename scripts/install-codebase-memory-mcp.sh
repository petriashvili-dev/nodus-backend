#!/usr/bin/env bash
set -euo pipefail

readonly installer_url="https://raw.githubusercontent.com/DeusData/codebase-memory-mcp/main/install.sh"

case "$(uname -s)" in
  Darwin|Linux) ;;
  *)
    echo "This installer supports macOS and Linux. On Windows, follow the PowerShell steps in the codebase-memory-mcp README." >&2
    exit 1
    ;;
esac

if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required to download the official installer." >&2
  exit 1
fi

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  cat <<'USAGE'
Install or update codebase-memory-mcp for the locally detected coding agents.

Usage: scripts/install-codebase-memory-mcp.sh

The official installer places the binary under ~/.local/bin by default and
configures detected local agents. Restart the agent after it completes.
USAGE
  exit 0
fi

if [[ $# -gt 0 ]]; then
  echo "Unknown option: $1" >&2
  echo "Run with --help for usage." >&2
  exit 2
fi

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/nodus-codebase-memory.XXXXXX")"
trap 'rm -rf "$tmp_dir"' EXIT

curl \
  --fail \
  --location \
  --proto '=https' \
  --proto-redir '=https' \
  --max-redirs 5 \
  --silent \
  --show-error \
  "$installer_url" \
  --output "$tmp_dir/install.sh"

# Run outside the checkout so machine-specific setup stays out of the repo.
(
  cd "$HOME"
  bash "$tmp_dir/install.sh"
)
