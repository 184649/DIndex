#!/usr/bin/env bash
# Historical isolated experiment; optional, excluded from ordinary CI.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
SDK="${DINDEX_DOTNET_ROOT:-/workspace/shared/dindex-bench-20261017/sdk}"
OUT="${DINDEX_EMPTY_BENCH_OUTPUT:-$(mktemp -d)}"
VARIANT="${1:-v2}"
case "$VARIANT" in v1) MAIN=Program;; v2) MAIN=LimitProbe;; *) echo 'Use v1 or v2' >&2; exit 2;; esac
if [[ -e "$OUT/results" || -e "$OUT/frozen" ]]; then echo "Choose a new output directory: $OUT" >&2; exit 2; fi
mkdir -p "$OUT/build" "$OUT/results" "$OUT/frozen"
cp "$ROOT/frozen/"*.cs "$OUT/frozen/"
cp "$ROOT/$MAIN.cs" "$ROOT/Tests.cs" "$OUT/"
if [[ "$VARIANT" == v1 ]]; then
  cp "$ROOT/variants/v1/CachedFileIndexService.cs" "$OUT/frozen/"
  cp "$ROOT/variants/v1/Tests.cs" "$OUT/Tests.cs"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_TieredCompilation=0
python3 - "$OUT" "$SDK" "$MAIN" <<'PY'
from pathlib import Path
import sys,json
r,sdk=map(Path,sys.argv[1:3]); main=sys.argv[3]
ref=sorted((sdk/'packs/Microsoft.NETCore.App.Ref').glob('*/ref/net8.0'))[-1]
for name in ['Tests',main]:
 args=['-nologo','-target:exe','-langversion:12','-nullable:enable','-optimize+','-out:'+str(r/'build'/f'{name}.dll')]
 args+=['-r:'+str(p) for p in sorted(ref.glob('*.dll'))]
 args += [str(p) for p in sorted((r/'frozen').glob('*.cs'))]+[str(r/f'{name}.cs')]
 (r/'build'/f'{name}.rsp').write_text('\n'.join(args)+'\n')
 (r/'build'/f'{name}.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
PY
CSC="$(find "$SDK/sdk" -path '*/Roslyn/bincore/csc.dll' | sort | tail -1)"
for name in Tests "$MAIN"; do "$SDK/dotnet" exec "$CSC" @"$OUT/build/$name.rsp"; done
"$SDK/dotnet" "$OUT/build/Tests.dll" | tee "$OUT/results/tests.log"
"$SDK/dotnet" "$OUT/build/$MAIN.dll" "$OUT" | tee "$OUT/results/benchmark.log"
printf 'Results: %s/results\n' "$OUT"
