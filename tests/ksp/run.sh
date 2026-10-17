#!/usr/bin/env bash
# Build, deploy ProcEngines (+ the in-game test driver) into a test copy and run one script.
#   tests/ksp/run.sh <A|B> <sandbox|career|none> [--nowait]
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
INST="/e/Kerbal Space Program Instances/ksp_test_$1"
SCRIPT="$2"
dotnet build -c Release "$ROOT/tests/ProcEngines.KspTests" > /dev/null
DEST="$INST/GameData/ProcEngines"
rm -rf "$DEST"; mkdir -p "$DEST/Plugins"
cp -r "$ROOT/GameData/ProcEngines/." "$DEST/"
cp "$ROOT/src/ProcEngines/bin/ProcEngines.dll" "$ROOT/src/ProcEngines.Model/bin/ProcEngines.Model.dll" "$DEST/Plugins/"
T="$INST/GameData/ProcEnginesTests"
mkdir -p "$T/Plugins" "$T/PluginData"
cp "$ROOT/tests/ProcEngines.KspTests/bin/ProcEngines.KspTests.dll" "$T/Plugins/"
if [ "$SCRIPT" = "none" ]; then rm -f "$T/PluginData/autotest.txt"; else echo "$SCRIPT" > "$T/PluginData/autotest.txt"; fi
rm -f "$T/PluginData/results_$SCRIPT.txt"
cd "$INST"
# windowed for the run (settings.cfg restored on exit) so test runs don't take over the screen
cp settings.cfg settings.cfg.pe_run_bak
sed -i 's/^FULLSCREEN = True/FULLSCREEN = False/' settings.cfg
if [ "$3" != "--nowait" ]; then trap 'mv -f settings.cfg.pe_run_bak settings.cfg' EXIT; fi
if [ "$3" = "--nowait" ]; then ./KSP_x64.exe -popupwindow > /dev/null 2>&1 & echo "started $1 ($SCRIPT)"; else ./KSP_x64.exe -popupwindow > /dev/null 2>&1; echo "exited $1 ($SCRIPT)"; fi
