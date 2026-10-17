#!/usr/bin/env bash
set -euo pipefail
# Usage: script FIXTURE_DIRECTORY RETAINED_COUNT [FileIndexService.cs] [OUTPUT_DIRECTORY] [VERIFY_SEARCH=true]
# Files must already exist. This measures the actual core scan, not Windows GUI latency.
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SDK="${DINDEX_DOTNET_ROOT:-/workspace/shared/dindex-bench-20261017/sdk}"
SOURCE="${3:-$ROOT/src/DIndex/FileIndexService.cs}"
OUT="${4:-$(mktemp -d)}"
mkdir -p "$OUT"
python3 - "$ROOT" "$SDK" "$SOURCE" "$OUT" <<'PY'
from pathlib import Path
import sys,json
root,sdk,source,out=map(Path,sys.argv[1:])
ref=sorted((sdk/'packs/Microsoft.NETCore.App.Ref').glob('*/ref/net8.0'))[-1]
args=['-nologo','-target:exe','-langversion:12','-nullable:enable','-optimize+','-out:'+str(out/'InitialResultsBenchmark.dll')]
args+=['-r:'+str(p) for p in sorted(ref.glob('*.dll'))]
args += [str(source),str(root/'src/DIndex/Models.cs')]
args += [str(root/'tests/DIndex.Core.Tests'/p) for p in ['GlobalUsings.cs','ClipboardStub.cs','InitialResultsBenchmark.cs']]
(out/'compile.rsp').write_text('\n'.join(args)+'\n')
(out/'InitialResultsBenchmark.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
PY
CSC="$(find "$SDK/sdk" -path '*/Roslyn/bincore/csc.dll' | sort | tail -1)"
"$SDK/dotnet" exec "$CSC" @"$OUT/compile.rsp"
"$SDK/dotnet" "$OUT/InitialResultsBenchmark.dll" "$1" "$2" "${5:-true}"
