#!/usr/bin/env bash
# Runs the SQL Server conformance cases and prints the failures.
#   tools/conf.sh [filter] [-v]     e.g. tools/conf.sh 03_math
cd "$(dirname "$0")/.."
dotnet build tests/LocalDbSharp.Conformance -nologo -v q 2>&1 | grep -E " error " | sed 's/\[C:.*//' | sort -u
dotnet run --no-build --project tests/LocalDbSharp.Conformance -- "$@" 2>&1 | cut -c1-${WIDTH:-400}
