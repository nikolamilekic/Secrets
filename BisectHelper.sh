#!/usr/bin/env bash

set -eu
set -o pipefail

dotnet fake run build.fsx --target BisectHelper "$@"
