#!/usr/bin/env bash
# Runs the Unity editor pinned in unity/ProjectSettings/ProjectVersion.txt in batch mode.
# Usage: tools/unity.sh <unity args...>   (override the editor binary with UNITY_EDITOR=/path/to/Unity)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/unity"
VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT/ProjectSettings/ProjectVersion.txt")"

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
