#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP="$SCRIPT_DIR/DotnetSdkManager.App/DotnetSdkManager.App.csproj"

echo "==> Building linux-x64 single-file..."
dotnet publish "$APP" /p:PublishProfile=linux-x64 /p:PublishSingleFile=true -c Release

echo "==> Building win-x64 single-file (cross-compile)..."
dotnet publish "$APP" /p:PublishProfile=win-x64 /p:PublishSingleFile=true -c Release

echo ""
echo "Artifacts:"
find "$SCRIPT_DIR/publish" -type f | sort
