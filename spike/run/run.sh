#!/usr/bin/env bash
# Deploy the spike into a test copy and run one harness script.
#   run.sh <A|B> <script> [--nowait]     script = local | rnd | lmpA | lmpB | none
set -e
INST="/e/Kerbal Space Program Instances/ksp_test_$1"
SCRIPT="$2"
SPIKE="/e/Kerbal Space Program Instances/_dev/ProceduralEngines/spike"
DEST="$INST/GameData/ProcEngineSpike"

mkdir -p "$DEST/Plugins" "$DEST/PluginData"
cp -r "$SPIKE/GameData/ProcEngineSpike/." "$DEST/"
cp "$SPIKE/bin/ProcEngineSpike.dll" "$DEST/Plugins/"
if [ "$SCRIPT" = "none" ]; then rm -f "$DEST/PluginData/autotest.txt"; else echo "$SCRIPT" > "$DEST/PluginData/autotest.txt"; fi
rm -f "$DEST/PluginData/results_$SCRIPT.txt"

cd "$INST"
if [ "$3" = "--nowait" ]; then
  ./KSP_x64.exe -popupwindow > /dev/null 2>&1 &
  echo "started $1 ($SCRIPT)"
else
  ./KSP_x64.exe -popupwindow > /dev/null 2>&1
  echo "exited $1 ($SCRIPT)"
fi
