#!/usr/bin/env bash
# Optional production-core benchmark; intentionally not part of the routine CI job.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SDK="${DINDEX_DOTNET_ROOT:-/workspace/shared/dindex-bench-20261017/sdk}"
OUT="${DINDEX_BENCH_OUTPUT:-$(mktemp -d)}"
BASE="${DINDEX_BASE_SOURCE:-/workspace/shared/dindex-pdca-cycle4-candidates/FileIndexService.cs.original}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_TieredCompilation=0
mkdir -p "$OUT/frozen"
cp "$ROOT/src/DIndex/FileIndexService.cs" "$ROOT/src/DIndex/Models.cs" "$OUT/frozen/"
cp "$ROOT/tests/DIndex.Core.Tests/ClipboardStub.cs" "$ROOT/tests/DIndex.Core.Tests/GlobalUsings.cs" "$OUT/frozen/"
cp "$ROOT/tests/DIndex.Core.Benchmarks/"*.cs "$OUT/frozen/"
if [[ -f "$BASE" ]]; then
  cp "$BASE" "$OUT/frozen/FileIndexService.cs.original"
else
  git -C "$ROOT" show 5213f4b1eae5841721a4318b07cd8bae8f72af01:src/DIndex/FileIndexService.cs > "$OUT/frozen/FileIndexService.cs.original"
fi
python3 - "$SDK" "$OUT" <<'PY'
from pathlib import Path
import sys,json
sdk,out=map(Path,sys.argv[1:]); ref=sorted((sdk/'packs/Microsoft.NETCore.App.Ref').glob('*/ref/net8.0'))[-1]
args=['-nologo','-target:exe','-langversion:12','-nullable:enable','-optimize+','-out:'+str(out/'DIndex.Core.Benchmarks.dll')]
args+=['-r:'+str(p) for p in sorted(ref.glob('*.dll'))]
args += [str(p) for p in sorted((out/'frozen').glob('*.cs'))]
(out/'compile.rsp').write_text('\n'.join(args)+'\n')
(out/'DIndex.Core.Benchmarks.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
PY
CSC="$(find "$SDK/sdk" -path '*/Roslyn/bincore/csc.dll' | sort | tail -1)"
"$SDK/dotnet" exec "$CSC" @"$OUT/compile.rsp"
"$SDK/dotnet" "$OUT/DIndex.Core.Benchmarks.dll" "$OUT" "${1:-30}" "${2:-100000,1000000}"
printf 'Results: %s/results.json\n' "$OUT"
