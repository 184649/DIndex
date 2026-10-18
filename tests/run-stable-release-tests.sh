#!/usr/bin/env bash
# Test-only fallback using an existing official .NET 8 SDK; production stays net10.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SDK="${1:-${DOTNET_ROOT:-}}"
if [[ -z "$SDK" ]]; then echo 'Pass an existing .NET 8 SDK root.' >&2; exit 2; fi
OUT="${2:-$(mktemp -d "${TMPDIR:-/tmp}/dindex-stable-tests.XXXXXXXX")}"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
python3 - "$ROOT" "$SDK" "$OUT" <<'PY'
from pathlib import Path
import json, re, sys
root, sdk, out = map(Path, sys.argv[1:])
key = lambda p: tuple(int(n) for n in re.findall(r'\d+', p.name))
refs = sorted((sdk/'packs/Microsoft.NETCore.App.Ref').glob('8.*'), key=key)
compilers = sorted((sdk/'sdk').glob('8.*/Roslyn/bincore/csc.dll'), key=lambda p:key(p.parents[2]))
if not refs or not compilers: raise SystemExit('A complete preinstalled official .NET 8 SDK is required.')
(out/'Generated.cs').write_text('''global using System;
global using System.IO;
global using System.Linq;
global using System.Collections.Generic;
[assembly: System.Reflection.AssemblyVersion("1.1.2.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.2.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.1.2")]
''')
args = ['-nologo','-target:exe','-langversion:12','-nullable:enable','-warnaserror+','-optimize+','-out:'+str(out/'StableReleaseVerifier.dll')]
args += ['-r:'+str(p) for p in sorted((refs[-1]/'ref/net8.0').glob('*.dll'))]
args += ['-resource:'+str(root/'scripts/StableReleaseVerifier/fixture.json')+',DIndexUpdater.LegacyUpdate.json']
args += [str(p) for p in sorted((root/'scripts/StableReleaseVerifier').glob('*.cs'))] + [str(out/'Generated.cs')]
(out/'compile.rsp').write_text('\n'.join('"'+arg+'"' for arg in args)+'\n')
(out/'compiler-path.txt').write_text(str(compilers[-1]))
(out/'StableReleaseVerifier.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':'8.0.0'}}}))
PY
"$SDK/dotnet" exec "$(cat "$OUT/compiler-path.txt")" @"$OUT/compile.rsp" 2>&1 | tee "$OUT/compile.log"
"$SDK/dotnet" "$OUT/StableReleaseVerifier.dll" self-test | tee "$OUT/run.log"
echo "Test evidence: $OUT"
