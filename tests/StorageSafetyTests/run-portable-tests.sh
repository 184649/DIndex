#!/usr/bin/env bash
# Package-free Linux/macOS fallback when dotnet build cannot initialize MSBuild/NuGet.
# Usage: ./run-portable-tests.sh /path/to/dotnet-sdk-root [output-directory]
set -euo pipefail
TEST_DIR="$(cd "$(dirname "$0")" && pwd)"
SDK="${1:-${DOTNET_ROOT:-}}"
if [[ -z "$SDK" ]]; then
  DOTNET_BIN="$(command -v dotnet || true)"
  if [[ -z "$DOTNET_BIN" ]]; then echo "Set DOTNET_ROOT or pass an existing .NET 8 SDK root." >&2; exit 2; fi
  SDK="$(dirname "$(readlink -f "$DOTNET_BIN")")"
fi
OUTPUT="${2:-$(mktemp -d "${TMPDIR:-/tmp}/dindex-storage-tests.XXXXXXXX")}"
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
python3 - "$TEST_DIR" "$SDK" "$OUTPUT" <<'PY'
from pathlib import Path
import json, re, sys, xml.etree.ElementTree as ET
root, sdk, output = map(Path, sys.argv[1:])
key = lambda p: tuple(int(n) for n in re.findall(r'\d+', p.name))
compilers = sorted((sdk/'sdk').glob('8.*/Roslyn/bincore/csc.dll'), key=lambda p:key(p.parents[2]))
references = sorted((sdk/'packs/Microsoft.NETCore.App.Ref').glob('8.*'), key=key)
if not compilers or not references: raise SystemExit('A complete official .NET 8 SDK is required.')
project = ET.parse(root/'StorageSafetyTests.csproj')
sources = sorted(root.glob('*.cs')) + [(root/node.attrib['Include']).resolve() for node in project.findall('.//Compile')]
arguments = ['-nologo', '-target:exe', '-main:StorageSafetyTests.Program', '-langversion:12', '-nullable:enable', '-warnaserror+', '-optimize+', '-out:'+str(output/'StorageSafetyTests.dll')]
arguments += ['-r:'+str(path) for path in sorted((references[-1]/'ref/net8.0').glob('*.dll'))]
arguments += [str(path) for path in sources]
(output/'compile.rsp').write_text('\n'.join('"'+arg+'"' for arg in arguments)+'\n')
(output/'compiler-path.txt').write_text(str(compilers[-1]))
(output/'StorageSafetyTests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
PY
"$SDK/dotnet" exec "$(cat "$OUTPUT/compiler-path.txt")" @"$OUTPUT/compile.rsp" 2>&1 | tee "$OUTPUT/compile.log"
"$SDK/dotnet" "$OUTPUT/StorageSafetyTests.dll" "$OUTPUT/results.json" | tee "$OUTPUT/run.log"
echo "Results: $OUTPUT/results.json"
