#!/usr/bin/env bash
# -----------------------------------------------------------------------------
#  Universe Creator - headless self test (macOS / Linux)
#
#  Runs the package self test WITHOUT opening the Unity editor, captures the
#  compile log, and prints a plain-language verdict. Use it after installing
#  the package, after a Unity or URP upgrade, and before reporting a problem.
#
#  Usage:
#      chmod +x uc_selftest.sh
#      ./uc_selftest.sh
#
#  The script finds the project and the editor by itself. Override anything:
#      ./uc_selftest.sh --project ~/Work/MyGame \
#                       --unity "/Applications/Unity/Hub/Editor/6000.5.2f1/Unity.app/Contents/MacOS/Unity"
#
#  Exit codes
#      0  PASS          - compiled clean, every check passed
#      1  FAIL          - compiled clean, one or more checks failed
#      2  COMPILE ERROR - Unity never reached the test
#      3  ENVIRONMENT   - project, editor or lock problem; nothing was run
#      4  NO RESULT     - Unity ran but wrote no verdict (crash, licence, OOM)
# -----------------------------------------------------------------------------

set -u

PROJECT=""
UNITY="${UNITY_PATH:-}"
LOG=""
FORCE=0
METHOD="UniverseCreator.Editor.Diagnostics.UCSelfTest.RunBatch"

while [ $# -gt 0 ]; do
  case "$1" in
    --project) PROJECT="$2"; shift 2 ;;
    --unity)   UNITY="$2";   shift 2 ;;
    --log)     LOG="$2";     shift 2 ;;
    --force)   FORCE=1;      shift ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1"; exit 3 ;;
  esac
done

ok()   { printf '  OK    %s\n' "$1"; }
warn() { printf '  WARN  %s\n' "$1"; }
bad()  { printf '  ERROR %s\n' "$1"; }
rule() { printf -- '--------------------------------------------------------------------\n'; }

echo
echo "Universe Creator - headless self test"
rule

# ---------------------------------------------------------------- project ----
# Walk up from this script until a folder holds both Assets and ProjectSettings.
if [ -z "$PROJECT" ]; then
  probe="$(cd "$(dirname "$0")" && pwd)"
  while [ -n "$probe" ] && [ "$probe" != "/" ]; do
    if [ -d "$probe/Assets" ] && [ -d "$probe/ProjectSettings" ]; then
      PROJECT="$probe"
      break
    fi
    probe="$(dirname "$probe")"
  done
fi

if [ -z "$PROJECT" ]; then
  bad "Could not find a Unity project above this script."
  echo "        Pass one explicitly:  ./uc_selftest.sh --project /path/to/project"
  exit 3
fi

PROJECT="$(cd "$PROJECT" && pwd)"
if [ ! -f "$PROJECT/ProjectSettings/ProjectVersion.txt" ]; then
  bad "Not a Unity project: $PROJECT"
  exit 3
fi
ok "project    $PROJECT"

# The package must actually be installed, or -executeMethod cannot resolve.
ENTRY="$PROJECT/Assets/UniverseCreator/Core/Editor/Diagnostics/UCSelfTest.cs"
if [ ! -f "$ENTRY" ]; then
  bad "Universe Creator is not installed in this project."
  echo "        Expected: Assets/UniverseCreator/Core/Editor/Diagnostics/UCSelfTest.cs"
  echo "        Import the package first, then run this again."
  exit 3
fi
ok "package    Assets/UniverseCreator"

# ----------------------------------------------------------------- editor ----
VERSION="$(sed -n 's/^m_EditorVersion: *//p' "$PROJECT/ProjectSettings/ProjectVersion.txt" | head -1 | tr -d '\r')"

if [ -z "$UNITY" ]; then
  case "$(uname -s)" in
    Darwin) HUBS="/Applications/Unity/Hub/Editor:$HOME/Applications/Unity/Hub/Editor" ; SUFFIX="Unity.app/Contents/MacOS/Unity" ;;
    *)      HUBS="$HOME/Unity/Hub/Editor:/opt/unity/editors:/opt/Unity/Hub/Editor"    ; SUFFIX="Editor/Unity" ;;
  esac

  IFS=':' read -r -a hublist <<< "$HUBS"
  for hub in "${hublist[@]}"; do
    [ -d "$hub" ] || continue

    if [ -n "$VERSION" ] && [ -x "$hub/$VERSION/$SUFFIX" ]; then
      UNITY="$hub/$VERSION/$SUFFIX"
      break
    fi

    # No exact match: take the newest installed editor and say so.
    for cand in $(ls -1 "$hub" 2>/dev/null | sort -r); do
      if [ -x "$hub/$cand/$SUFFIX" ]; then
        UNITY="$hub/$cand/$SUFFIX"
        break
      fi
    done
    [ -n "$UNITY" ] && break
  done
fi

if [ -z "$UNITY" ] || [ ! -x "$UNITY" ]; then
  bad "Could not find the Unity executable."
  echo "        Pass it:   ./uc_selftest.sh --unity \"/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity\""
  echo "        Or set it:  export UNITY_PATH=\"<path to the Unity binary>\""
  exit 3
fi

ok "editor     $UNITY"
case "$UNITY" in
  *"/$VERSION/"*) : ;;
  *) warn "the project was made with $VERSION - this editor is a different version"
     warn "an upgrade prompt in batch mode can look exactly like a test failure" ;;
esac

# -------------------------------------------------------------- pre-flight ----
# Unity holds an exclusive lock on an open project; batch mode would refuse it.
if [ -f "$PROJECT/Temp/UnityLockfile" ]; then
  if [ "$FORCE" -eq 1 ]; then
    warn "the project looks open in the editor - continuing because --force was passed"
  else
    bad "This project is already open in the Unity editor."
    echo "        Close it and run again (a second process cannot take the project lock)."
    echo "        Or pass --force to try anyway."
    exit 3
  fi
fi

# A mid-test crash on a busy machine is a memory problem, not a code problem.
FREE_MB=""
if command -v free >/dev/null 2>&1; then
  FREE_MB="$(free -m | awk '/^Mem:/ {print $7 ? $7 : $4}')"
elif command -v vm_stat >/dev/null 2>&1; then
  FREE_MB="$(vm_stat | awk '/page size of/ {ps=$8} /Pages free/ {f=$3} /Pages inactive/ {i=$3} END {gsub(/\./,"",f); gsub(/\./,"",i); if (ps=="") ps=4096; print int((f+i)*ps/1048576)}')"
fi
if [ -n "$FREE_MB" ] && [ "$FREE_MB" -lt 3000 ] 2>/dev/null; then
  warn "only ${FREE_MB} MB RAM free - a batch editor needs roughly 3-4 GB"
  warn "close other Unity instances first, or expect an out-of-memory abort mid-test"
elif [ -n "$FREE_MB" ]; then
  ok "memory     ${FREE_MB} MB free"
fi

[ -n "$LOG" ] || LOG="$PROJECT/uc_selftest.log"
ok "log        $LOG"

# ------------------------------------------------------------------- run -----
rule
echo "Running. A first run compiles the whole project and can take several minutes."
echo

STARTED=$(date +%s)
"$UNITY" -batchmode -quit -nographics \
         -projectPath "$PROJECT" \
         -logFile "$LOG" \
         -executeMethod "$METHOD"
CODE=$?
ELAPSED=$(( $(date +%s) - STARTED ))

echo "Editor exited after ${ELAPSED}s (exit code $CODE)."
echo

# --------------------------------------------------------------- verdict -----
rule
echo "RESULT"
rule

if [ ! -f "$LOG" ]; then
  bad "No log was written, so Unity never really started."
  echo
  echo "Most likely causes, in order:"
  echo "  1. Another Unity process holds the project lock -> close the editor."
  echo "  2. The editor is not activated -> open it once by hand and sign in."
  echo "  3. The log folder is not writable -> pass --log /tmp/uc_selftest.log."
  exit 4
fi

CS_COUNT="$(grep -c -E 'error CS[0-9]{4}' "$LOG" || true)"
if [ "${CS_COUNT:-0}" -gt 0 ]; then
  bad "COMPILE ERROR - $CS_COUNT C# error line(s). The test never ran."
  echo
  grep -E 'error CS[0-9]{4}' "$LOG" | head -20 | sed 's/^/    /'
  [ "$CS_COUNT" -gt 20 ] && echo "    ... and $((CS_COUNT - 20)) more (see the log)"
  echo
  echo "What this usually means:"
  echo "  - Another asset in the project declares a type this package also uses."
  echo "  - The package was imported into a Unity version older than it needs."
  echo "  - Only part of the package was imported (a partial or interrupted import)."
  echo
  echo "Log: $LOG"
  exit 2
fi
ok "compile    no C# errors"

# The verdict block RunBatch writes: header, checks run, failures, then any
# failure lines. Print it verbatim - it is the artefact support asks for.
if ! grep -q '=== UC SELF TEST' "$LOG"; then
  bad "The project compiled, but the self test produced no verdict."
  echo

  probe_sig() {
    if grep -q -i -E "$1" "$LOG"; then
      warn "$2"
      echo "        first match: $(grep -i -m1 -E "$1" "$LOG")"
    fi
  }
  probe_sig 'Multiple Unity instances'                             'another Unity process was running - close the editor and retry'
  probe_sig 'LICENSE SYSTEM|no valid unity editor licen'           'the editor is not activated - open Unity once by hand and sign in'
  probe_sig 'out of memory|could not allocate memory'              'the machine ran out of RAM mid-test - close other apps and retry'
  probe_sig 'executeMethod.*could not be found|find method'        'the editor assembly did not load - reimport Assets/UniverseCreator'
  probe_sig 'Crash!!!|Fatal Error|Unhandled Exception'             'the editor crashed - the log holds the stack above this line'

  echo
  echo "Log: $LOG"
  exit 4
fi

echo
grep -A 24 '=== UC SELF TEST' "$LOG" | sed '/^[[:space:]]*$/q' | sed 's/^/    /'
echo

if grep -q '=== UC SELF TEST: PASS' "$LOG"; then
  ok "self test  PASS"
  echo
  echo "The install is sound: the package compiles and every check agrees."
  echo "Anything you see wrong in the scene after this is a setting, not a broken install."
  echo
  echo "Log: $LOG"
  exit 0
fi

bad "self test  FAIL"
echo
echo "The package compiled but a check disagreed. The FAIL lines above name the area."
echo "Send this log with your report - it is the whole diagnosis:"
echo "    $LOG"
exit 1
