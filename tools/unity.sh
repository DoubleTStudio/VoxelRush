#!/usr/bin/env bash
# Runs the Unity editor pinned in unity/ProjectSettings/ProjectVersion.txt in batch mode.
# Usage: tools/unity.sh <unity args...>
# Editor override: UNITY_EDITOR, else UNITY_EDITOR_PATH (set by the unity-setup CI action); either may be a binary, .app or install dir.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/unity"
VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT/ProjectSettings/ProjectVersion.txt")"

UNITY_EDITOR="${UNITY_EDITOR:-${UNITY_EDITOR_PATH:-}}"
if [[ -d "$UNITY_EDITOR" ]]; then
  for candidate in "$UNITY_EDITOR/Contents/MacOS/Unity" "$UNITY_EDITOR/Unity.app/Contents/MacOS/Unity" "$UNITY_EDITOR/Unity" "$UNITY_EDITOR/Editor/Unity"; do
    [[ -x "$candidate" ]] && { UNITY_EDITOR="$candidate"; break; }
  done
fi

if [[ -z "${UNITY_EDITOR:-}" ]]; then
  case "$(uname -s)" in
    Darwin) UNITY_EDITOR="/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity" ;;
    Linux)  UNITY_EDITOR="$HOME/Unity/Hub/Editor/$VERSION/Editor/Unity" ;;
    *)      echo "Unsupported OS; set UNITY_EDITOR." >&2; exit 1 ;;
  esac
fi

if [[ ! -x "$UNITY_EDITOR" ]]; then
  echo "Unity $VERSION not found at $UNITY_EDITOR. Install it via Unity Hub or set UNITY_EDITOR." >&2
  exit 1
fi

exec "$UNITY_EDITOR" -batchmode -nographics -projectPath "$PROJECT" "$@"
