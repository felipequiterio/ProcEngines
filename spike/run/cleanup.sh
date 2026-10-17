#!/usr/bin/env bash
# Removes everything the ProcEngines spike added to the test copies (see TEST_COPY_CHANGES.txt).
cd "/e/Kerbal Space Program Instances"
rm -rf ksp_test_A/GameData/{ProcEngineSpike,FerramAerospaceResearch,ModularFlightIntegrator,Waterfall,KerbalEngineer,MechJeb2,MechJebForAll,000_KSPBurst} ksp_test_A/GameData/999_Scale_Redist.dll
rm -rf ksp_test_B/GameData/{ProcEngineSpike,Waterfall}
rm -rf ksp_test_A/saves/pespike ksp_test_A/saves/pespike_career ksp_test_B/saves/pespike ksp_test_B/saves/pespike_career
echo "done; ModuleManager re-patches on the next start of each copy"
