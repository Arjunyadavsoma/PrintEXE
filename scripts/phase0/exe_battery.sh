#!/usr/bin/env bash
# Phase 0b — PrintEXE local battery on fix/e2e-printshop (origin/main tree).
# Tests are skipped locally BY DESIGN: every test project targets
# net10.0-windows (UseWPF) and requires the Microsoft.WindowsDesktop.App
# runtime, which does not exist on Linux — see 03-tests-linux.laptop-lineage.log
# for the exact error. The authoritative Windows test evidence is the
# windows-latest CI log saved under docs/audit/phase0/ci-windows-run-44552138078/.
set -u
EXE=/home/z/my-project/repos/PrintEXE
AUD="$EXE/docs/audit/phase0"
mkdir -p "$AUD"
cd "$EXE"
export DOTNET_ROOT=/home/z/my-project/tools/dotnet10
export PATH="$DOTNET_ROOT:$PATH"
unset DOTNET_ROOT_OLD 2>/dev/null || true

{ dotnet --version; echo "tree: $(git log --oneline -1)"; } > "$AUD/00-env.log" 2>&1

echo "── dotnet restore ──"
dotnet restore PrintShopCompanion.sln > "$AUD/01-restore.log" 2>&1
echo "restore exit=$?" | tee -a "$AUD/01-restore.log"

echo "── dotnet build ──"
dotnet build --no-restore -v q --nologo PrintShopCompanion.sln > "$AUD/02-build.log" 2>&1
echo "build exit=$?" | tee -a "$AUD/02-build.log"
grep -E "Error\(s\)|Warning\(s\)" "$AUD/02-build.log" | tail -2
