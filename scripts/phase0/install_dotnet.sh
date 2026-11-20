#!/usr/bin/env bash
# Phase 0 setup: install .NET 10 SDK into tools/dotnet10 (old tools/dotnet lost its host/fxr).
set -e
cd /tmp/dotnet-setup
curl -sSL -o dotnet-install.sh https://dot.net/v1/dotnet-install.sh
bash dotnet-install.sh --channel 10.0 --install-dir /home/z/my-project/tools/dotnet10
/home/z/my-project/tools/dotnet10/dotnet --version
echo "DOTNET INSTALL OK"
