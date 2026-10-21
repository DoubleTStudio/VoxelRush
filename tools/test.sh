#!/usr/bin/env bash
# Runs test tiers and writes NUnit XML to artifacts/test-results/.
# Usage: tools/test.sh [domain|editmode|playmode|all]   (default: all)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RESULTS="$ROOT/artifacts/test-results"
mkdir -p "$RESULTS"

run_domain() {
  dotnet test "$ROOT/tests/VoxelRush.Domain.Tests" --logger "trx;LogFileName=$RESULTS/domain.trx"
}

run_unity() {
  local platform="$1"
  # -runTests exits on its own; -quit would abort the run.
  # Pin a client target: after a server build the editor stays on Dedicated Server and compiles out Presentation.
  # UNITY_TEST_BUILD_TARGET="" skips the switch (fresh CI checkouts already start on a client target).
  local target="${UNITY_TEST_BUILD_TARGET-Android}"
  local target_args=()
  [[ -n "$target" ]] && target_args=(-buildTarget "$target")
  "$ROOT/tools/unity.sh" "${target_args[@]+"${target_args[@]}"}" -runTests -testPlatform "$platform" \
    -testResults "$RESULTS/$platform.xml" -logFile "$RESULTS/$platform.log"
}

case "${1:-all}" in
  domain)   run_domain ;;
  editmode) run_unity EditMode ;;
  playmode) run_unity PlayMode ;;
  all)      run_domain; run_unity EditMode; run_unity PlayMode ;;
  *)        echo "Unknown tier '$1'." >&2; exit 2 ;;
esac
