#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SDK="${DINDEX_DOTNET_ROOT:-/workspace/shared/dindex-bench-20261017/sdk}"
OUT="${DINDEX_TEST_OUTPUT:-$(mktemp -d)}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
mkdir -p "$OUT"
python3 - "$ROOT" "$SDK" "$OUT" <<'PY'
from pathlib import Path
import sys,json
r,sdk,out=map(Path,sys.argv[1:])
ref=sorted((sdk/'packs/Microsoft.NETCore.App.Ref').glob('*/ref/net8.0'))[-1]
args=['-nologo','-target:exe','-langversion:12','-nullable:enable','-optimize+','-out:'+str(out/'DIndex.Core.Tests.dll')]
args+=['-r:'+str(p) for p in sorted(ref.glob('*.dll'))]
args += [str(r/'src/DIndex'/p) for p in ['FileIndexService.cs','Models.cs']]
args += [str(r/'tests/DIndex.Core.Tests'/p) for p in ['GlobalUsings.cs','ClipboardStub.cs','Program.cs','EmptyCacheTests.cs','ProgressiveIndexTests.cs','IndexStateAssertions.cs']]
(out/'compile.rsp').write_text('\n'.join(args)+'\n')
(out/'DIndex.Core.Tests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
PY
CSC="$(find "$SDK/sdk" -path '*/Roslyn/bincore/csc.dll' | sort | tail -1)"
"$SDK/dotnet" exec "$CSC" @"$OUT/compile.rsp"
"$SDK/dotnet" "$OUT/DIndex.Core.Tests.dll"
