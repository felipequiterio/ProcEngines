#!/usr/bin/env bash
# Removes everything the ProcEngines spike and test runs added to the test copies (see TEST_COPY_CHANGES.txt).
cd "/e/Kerbal Space Program Instances"
rm -rf ksp_test_A/GameData/{ProcEngineSpike,ProcEngines,ProcEnginesTests,FerramAerospaceResearch,ModularFlightIntegrator,Waterfall,KerbalEngineer,MechJeb2,MechJebForAll,000_KSPBurst} ksp_test_A/GameData/999_Scale_Redist.dll
rm -rf ksp_test_B/GameData/{ProcEngineSpike,ProcEngines,ProcEnginesTests,Waterfall}
rm -rf ksp_test_A/saves/{pespike,pespike_career,procengines_sandbox,procengines_career} ksp_test_B/saves/{pespike,pespike_career,procengines_sandbox,procengines_career}
echo "done; ModuleManager re-patches on the next start of each copy"
