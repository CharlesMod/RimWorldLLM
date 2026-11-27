#!/usr/bin/env bash
# Builds the RimWorldLLM release zip: mod DLLs, agent wheel, four easy-engine binaries,
# licenses, BUILD and SHA256SUMS, then verifies the result and prints PASS/FAIL per check.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/package-release.sh [options]

  --version V       release label (default: <modVersion from About.xml>-beta)
  --out DIR         output directory (default: <repo>/dist-release)
  --easyengine DIR  easyEngine checkout (default: $EASYENGINE_DIR, <repo>/../easyEngine
                    if a git checkout, else $HOME/easyEngine)
  --dlls DIR        take prebuilt RimWorldLLM.dll and Newtonsoft.Json.dll from DIR
                    instead of running dotnet build
  -h, --help        this help

Output: <out>/RimWorldLLM-<version>.zip and <zip>.sha256. Refuses a dirty tree in
either repo. Exits non-zero if any check fails. Builds run under nice -n 19.
EOF
}

VERSION=""; OUT=""; EE_DIR=""; DLLS=""
while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION=${2:?}; shift 2;;
    --out) OUT=${2:?}; shift 2;;
    --easyengine) EE_DIR=${2:?}; shift 2;;
    --dlls) DLLS=${2:?}; shift 2;;
    -h|--help) usage; exit 0;;
    *) echo "unknown option: $1" >&2; usage >&2; exit 2;;
  esac
done

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)   # physical path: the build caches absolute paths (a /home -> /var/home link lost Newtonsoft.Json.dll)
GO=${GO:-$HOME/.local/go/bin/go}
UV=${UV:-$(command -v uv || echo "$HOME/.local/bin/uv")}
DOTNET=${DOTNET:-$HOME/.dotnet/dotnet}
NICE="nice -n 19"
MODSRC="$REPO/Mod"
FAILS=0

die() { echo "ERROR: $*" >&2; exit 1; }
for t in zip zipinfo unzip file sha256sum python3 git; do
  command -v "$t" >/dev/null || die "missing tool: $t"
done

# --- resolve easyEngine ---
if [ -z "$EE_DIR" ]; then
  if [ -n "${EASYENGINE_DIR:-}" ]; then EE_DIR=$EASYENGINE_DIR
  elif [ -e "$REPO/../easyEngine/.git" ]; then EE_DIR="$REPO/../easyEngine"
  else EE_DIR="$HOME/easyEngine"; fi
fi
EE_DIR=$(cd "$EE_DIR" 2>/dev/null && pwd) || die "easyEngine dir not found: $EE_DIR"
git -C "$EE_DIR" rev-parse --git-dir >/dev/null 2>&1 || die "not a git checkout: $EE_DIR"
[ -x "$GO" ] || die "go not found at $GO (set GO=)"

# --- dirty-tree refusal ---
dirty=0
for d in "$REPO" "$EE_DIR"; do
  s=$(git -C "$d" status --porcelain --untracked-files=no)
  if [ -n "$s" ]; then echo "DIRTY tracked files in $d:" >&2; echo "$s" >&2; dirty=1; fi
done
u=$(git -C "$REPO" ls-files --others --exclude-standard -- Mod agent)
if [ -n "$u" ]; then echo "DIRTY untracked files under Mod/ or agent/:" >&2; echo "$u" >&2; dirty=1; fi
[ "$dirty" = 0 ] || die "refusing to package a dirty tree"

# --- versions ---
MODVER=$(python3 -I - "$MODSRC/About/About.xml" <<'PY'
import re,sys
m=re.search(r'<modVersion>\s*([^<\s]+)\s*</modVersion>',open(sys.argv[1],encoding='utf-8').read())
print(m.group(1) if m else '')
PY
)
[ -n "$MODVER" ] || die "no <modVersion> in About.xml"
[ -n "$VERSION" ] || VERSION="$MODVER-beta"
RW_SHA=$(git -C "$REPO" rev-parse HEAD)
EE_SHA=$(git -C "$EE_DIR" rev-parse HEAD)
LLAMA_TAG=$(python3 -I - "$EE_DIR/catalog/catalog.json" <<'PY'
import json,sys
print(json.load(open(sys.argv[1]))["llamacpp"]["tag"])
PY
)
UV_VER=$(python3 -I - "$EE_DIR/catalog/catalog.json" <<'PY'
import json,re,sys
c=json.load(open(sys.argv[1]))
v=set()
for e in c.get("uv",[]):
    m=re.search(r'/download/([^/]+)/',e.get("url",""))
    if m: v.add(m.group(1))
print(",".join(sorted(v)))
PY
)
[ -n "$LLAMA_TAG" ] && [ -n "$UV_VER" ] || die "could not read llama.cpp tag / uv version from catalog"

[ -n "$OUT" ] || OUT="$REPO/dist-release"
mkdir -p "$OUT"; OUT=$(cd "$OUT" && pwd)
ZIPNAME="RimWorldLLM-$VERSION.zip"
ZIP="$OUT/$ZIPNAME"

# staging: only removed if this run created it
STAGE="$OUT/.stage-$$"
[ ! -e "$STAGE" ] || die "staging dir exists: $STAGE"
mkdir "$STAGE"
cleanup() { [ -n "${STAGE:-}" ] && [ -d "$STAGE" ] && rm -rf -- "$STAGE"; }
trap cleanup EXIT
ROOT="$STAGE/RimWorldLLM"; mkdir "$ROOT"

echo "== RimWorldLLM $VERSION (mod $MODVER) RimWorldLLM ${RW_SHA:0:12} easyEngine ${EE_SHA:0:12}"

# --- 1. mod DLLs ---
ASM="$ROOT/1.7/Assemblies"; mkdir -p "$ASM"
if [ -n "$DLLS" ]; then
  for f in RimWorldLLM.dll Newtonsoft.Json.dll; do
    [ -f "$DLLS/$f" ] || die "missing $DLLS/$f"; cp "$DLLS/$f" "$ASM/"
  done
else
  echo "== dotnet build (Release)"
  [ -x "$DOTNET" ] || DOTNET=$(command -v dotnet || true)
  [ -x "$DOTNET" ] || die "dotnet not found"
  # --no-incremental: an incremental build can skip the copy-local step (obj/*.CopyComplete) after IncrementalClean removed Newtonsoft.Json.dll
  blog="$STAGE/dotnet-build.log"
  env -u RIMWORLD_DIR -u RimWorldDir $NICE "$DOTNET" build "$MODSRC/RimWorldLLM.csproj" -c Release --no-incremental \
    -p:RimWorldDir= -p:BuildingWithScript=true >"$blog" 2>&1 || { tail -30 "$blog" >&2; die "dotnet build failed"; }
  w=$(grep -E '^\s*[0-9]+ Warning\(s\)' "$blog" | grep -oE '[0-9]+' | head -1 || true)
  [ "${w:-x}" = 0 ] || { grep -i 'warning' "$blog" | head -20 >&2; die "dotnet build warnings: ${w:-unknown} (baseline 0)"; }
  for f in RimWorldLLM.dll Newtonsoft.Json.dll; do
    [ -f "$MODSRC/1.7/Assemblies/$f" ] || die "build did not produce $f"
    cp "$MODSRC/1.7/Assemblies/$f" "$ASM/"
  done
fi

# --- 2. mod content (whitelist) ---
cp -r "$MODSRC/About" "$MODSRC/Defs" "$MODSRC/Languages" "$ROOT/"
mkdir -p "$ROOT/Textures"; cp -r "$MODSRC/Textures/RimWorldLLM" "$ROOT/Textures/"

# --- 3. agent wheel ---
echo "== agent wheel"
( cd "$REPO/agent" && UV="$UV" PATH="$(dirname "$UV"):$PATH" $NICE ./build-wheel.sh ) >"$STAGE/wheel.log" 2>&1 \
  || { tail -20 "$STAGE/wheel.log" >&2; die "wheel build failed"; }
shopt -s nullglob
wheels=("$REPO"/agent/dist/rimworldllm_agent-*-py3-none-any.whl)
shopt -u nullglob
[ "${#wheels[@]}" = 1 ] || die "expected exactly one wheel in agent/dist, found ${#wheels[@]}"
mkdir -p "$ROOT/Engine/agent"; cp "${wheels[0]}" "$ROOT/Engine/agent/"
WHEEL=$(basename "${wheels[0]}")

# --- 4. engine binaries ---
TARGETS="linux-amd64 windows-amd64 darwin-arm64 darwin-amd64"
# Go marks vcs.modified=true for any untracked file; tolerate only the checkout's own .claude/ dir.
EE_UNTRACKED_OK=0
if [ -z "$(git -C "$EE_DIR" status --porcelain | grep -vE '^\?\? \.claude/$' || true)" ]; then EE_UNTRACKED_OK=1; fi
mod_ok() { grep -q 'vcs.modified=false$' <<<"$1" || { [ "$EE_UNTRACKED_OK" = 1 ] && grep -q 'vcs.modified=true$' <<<"$1"; }; }
engine_bin() { case "$1" in windows-*) echo easy-engine.exe;; *) echo easy-engine;; esac; }
engine_fresh() {
  local t b out
  for t in $TARGETS; do
    b="$EE_DIR/dist/$t/$(engine_bin "$t")"
    [ -f "$b" ] || return 1
    out=$("$GO" version -m "$b" 2>/dev/null) || return 1
    grep -q "vcs.revision=$EE_SHA\$" <<<"$out" || return 1
    mod_ok "$out" || return 1
  done
}
if engine_fresh; then
  echo "== engine: reusing $EE_DIR/dist (revision matches HEAD)"
else
  echo "== engine: building (stale or missing dist)"
  ( cd "$EE_DIR" && GO="$GO" $NICE ./build.sh ) >"$STAGE/engine.log" 2>&1 \
    || { tail -30 "$STAGE/engine.log" >&2; die "easyEngine build failed"; }
  engine_fresh || die "engine binaries lack vcs.revision=$EE_SHA / vcs.modified=false after build"
fi
for t in $TARGETS; do
  mkdir -p "$ROOT/Engine/$t"
  cp "$EE_DIR/dist/$t/$(engine_bin "$t")" "$ROOT/Engine/$t/"
done
chmod 755 "$ROOT/Engine/linux-amd64/easy-engine" "$ROOT/Engine/darwin-arm64/easy-engine" "$ROOT/Engine/darwin-amd64/easy-engine"

# --- 5. licenses ---
mkdir -p "$ROOT/Licenses"
cp "$REPO/LICENSE" "$ROOT/LICENSE"
for f in CHANGELOG.md README.md; do [ -f "$REPO/$f" ] && cp "$REPO/$f" "$ROOT/$f"; done
cp "$HOME/.nuget/packages/newtonsoft.json/13.0.3/LICENSE.md" "$ROOT/Licenses/Newtonsoft.Json-LICENSE.md"
cp "$EE_DIR/LICENSE" "$ROOT/Licenses/easyEngine-LICENSE.txt"
SYSVER=$(sed -nE 's/^require golang.org\/x\/sys (v[^ ]+).*/\1/p' "$EE_DIR/go.mod" | head -1)
[ -n "$SYSVER" ] || die "golang.org/x/sys not found in easyEngine go.mod"
MODCACHE=$("$GO" env GOMODCACHE)
cp "$MODCACHE/golang.org/x/sys@$SYSVER/LICENSE" "$ROOT/Licenses/golang-x-sys-LICENSE.txt"
cp "$("$GO" env GOROOT)/LICENSE" "$ROOT/Licenses/go-stdlib-LICENSE.txt"

# --- 6. BUILD ---
cat >"$ROOT/BUILD" <<EOF
RimWorldLLM $VERSION
mod_version: $MODVER
release: $VERSION
rimworldllm_commit: $RW_SHA
easyengine_commit: $EE_SHA
agent_wheel: $WHEEL
llamacpp_tag: $LLAMA_TAG
uv_version: $UV_VER
build_date_utc: $(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF

# --- 7. SHA256SUMS (every file but itself) ---
( cd "$ROOT" && find . -type f ! -name SHA256SUMS | LC_ALL=C sort | sed 's|^\./||' | xargs -d '\n' sha256sum >SHA256SUMS )

# --- 8. zip ---
rm -f -- "$ZIP" "$ZIP.sha256"
( cd "$STAGE" && zip -X -r -q "$ZIP" RimWorldLLM )
( cd "$OUT" && sha256sum "$ZIPNAME" >"$ZIPNAME.sha256" )

# --- 9. verify (against the zip itself) ---
VDIR="$STAGE/verify"; mkdir "$VDIR"
unzip -q "$ZIP" -d "$VDIR"
V="$VDIR/RimWorldLLM"
LIST=$(zipinfo -1 "$ZIP")

pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; FAILS=$((FAILS+1)); }
check() { local name=$1; shift; if "$@" >/dev/null 2>&1; then pass "$name"; else fail "$name"; fi; }
nomatch() { # name, ERE over zip listing: pass if no entry matches
  local m; m=$(grep -E "$2" <<<"$LIST" || true)
  if [ -z "$m" ]; then pass "$1"; else fail "$1: $(head -3 <<<"$m" | tr '\n' ' ')"; fi
}

# expected files
miss=""
exp=(About/About.xml 1.7/Assemblies/RimWorldLLM.dll 1.7/Assemblies/Newtonsoft.Json.dll
     Languages/English/Keyed/RimWorldLLM.xml LICENSE BUILD SHA256SUMS
     Licenses/Newtonsoft.Json-LICENSE.md Licenses/easyEngine-LICENSE.txt
     Licenses/golang-x-sys-LICENSE.txt Licenses/go-stdlib-LICENSE.txt)
for t in $TARGETS; do exp+=("Engine/$t/$(engine_bin "$t")"); done
for f in "$MODSRC"/Defs/*.xml; do exp+=("Defs/$(basename "$f")"); done
for f in "$MODSRC"/Textures/RimWorldLLM/Bubbles/*.png; do exp+=("Textures/RimWorldLLM/Bubbles/$(basename "$f")"); done
for f in "${exp[@]}"; do [ -s "$V/$f" ] || miss="$miss $f"; done
[ -z "$miss" ] && pass "expected files present (${#exp[@]})" || fail "missing:$miss"

nbub=$(find "$V/Textures/RimWorldLLM/Bubbles" -name '*.png' | wc -l)
nsrc=$(find "$MODSRC/Textures/RimWorldLLM/Bubbles" -name '*.png' | wc -l)
[ "$nbub" = "$nsrc" ] && [ "$nbub" -gt 0 ] && pass "bubble PNGs: $nbub" || fail "bubble PNGs: $nbub vs source $nsrc"
nw=$(find "$V/Engine/agent" -name '*.whl' | wc -l)
[ "$nw" = 1 ] && pass "exactly one wheel ($WHEEL)" || fail "wheel count $nw"

# mod identity (the mod was called HermesVoice before 2.1.0)
grep -q '<name>RimWorldLLM</name>' "$V/About/About.xml" && grep -q '<packageId>charleszard.rimworldllm</packageId>' "$V/About/About.xml" \
  && pass "About.xml name RimWorldLLM, packageId charleszard.rimworldllm" || fail "About.xml name/packageId"
case "$WHEEL" in rimworldllm_agent-*-py3-none-any.whl) pass "wheel is rimworldllm_agent";; *) fail "wheel name $WHEEL";; esac
old=$(grep -E -i 'hermesvoice|hermes-engine' <<<"$LIST" || true)
[ -z "$old" ] && pass "no old names in file paths" || fail "old names in paths: $(head -3 <<<"$old" | tr '\n' ' ')"

# top folder + exec bits
tops=$(grep -vE '^RimWorldLLM/' <<<"$LIST" || true)
[ -z "$tops" ] && pass "single top folder RimWorldLLM/" || fail "entries outside RimWorldLLM/: $(head -3 <<<"$tops")"
for t in linux-amd64 darwin-arm64 darwin-amd64; do
  p=$(zipinfo "$ZIP" "RimWorldLLM/Engine/$t/easy-engine" | awk '{print $1}')
  [ "$p" = "-rwxr-xr-x" ] && pass "exec bit $t ($p)" || fail "exec bit $t ($p)"
done

# binary formats
ft() { file -b "$V/Engine/$1/$(engine_bin "$1")"; }
grep -q 'ELF 64-bit.*x86-64' <<<"$(ft linux-amd64)" && pass "linux-amd64 is ELF x86-64" || fail "linux-amd64: $(ft linux-amd64)"
{ grep -q 'PE32' <<<"$(ft windows-amd64)" && [ "$(head -c2 "$V/Engine/windows-amd64/easy-engine.exe")" = MZ ]; } && pass "windows-amd64 is PE (MZ)" || fail "windows-amd64: $(ft windows-amd64)"
grep -q 'Mach-O.*arm64' <<<"$(ft darwin-arm64)" && pass "darwin-arm64 is Mach-O arm64" || fail "darwin-arm64: $(ft darwin-arm64)"
grep -q 'Mach-O.*x86_64' <<<"$(ft darwin-amd64)" && pass "darwin-amd64 is Mach-O x86_64" || fail "darwin-amd64: $(ft darwin-amd64)"

# engine revision
okrev=1
for t in $TARGETS; do
  o=$("$GO" version -m "$V/Engine/$t/$(engine_bin "$t")")
  grep -q "vcs.revision=$EE_SHA\$" <<<"$o" && mod_ok "$o" || okrev=0
done
[ "$okrev" = 1 ] && pass "engine binaries built from easyEngine $EE_SHA, unmodified" || fail "engine vcs info mismatch"

# wheel contents
WL=$(unzip -Z1 "$V/Engine/agent/$WHEEL")
bad=$(grep -E '(^|/)(tests?/|test_[^/]*\.py$|conftest\.py$|bench|\.env$)' <<<"$WL" || true)
[ -z "$bad" ] && pass "wheel holds no tests/bench/conftest/.env" || fail "wheel contains: $(head -3 <<<"$bad" | tr '\n' ' ')"

# forbidden entries
nomatch "no .pdb" '\.pdb$'
nomatch "no 0Harmony" '0Harmony'
nomatch "no Mod.Tests / tests / test_*.py / conftest.py" '(^|/)(Mod\.Tests|tests?)/|(^|/)test_[^/]*\.py$|(^|/)conftest\.py$'
nomatch "no bench/" '(^|/)bench/'
nomatch "no Textures/src" 'Textures/src(/|$)'
nomatch "no *.jsonl" '\.jsonl$'
nomatch "no bridge.json/engine-profile.json/.env/*.pem/*.key" '(^|/)(bridge\.json|engine-profile\.json|\.env)$|\.(pem|key)$'
nomatch "no Source/ or csproj" '(^|/)Source/|\.csproj$'

# secrets in text files (wheel searched unpacked)
SCAN="$STAGE/scan"; mkdir "$SCAN"; unzip -q "$V/Engine/agent/$WHEEL" -d "$SCAN"
hits=$(grep -rIlE 'sk-[A-Za-z0-9]{20,}|ghp_|hf_[A-Za-z0-9]{20,}|BEGIN .*PRIVATE KEY' "$V" "$SCAN" 2>/dev/null || true)
[ -z "$hits" ] && pass "no secret patterns in text files" || fail "secret patterns in: $hits"

# SHA256SUMS integrity and coverage
( cd "$V" && sha256sum -c --quiet SHA256SUMS ) >/dev/null 2>&1 && pass "SHA256SUMS verifies" || fail "SHA256SUMS verify"
nfiles=$(cd "$V" && find . -type f ! -name SHA256SUMS | wc -l)
nsum=$(wc -l <"$V/SHA256SUMS")
[ "$nfiles" = "$nsum" ] && pass "SHA256SUMS covers all $nfiles files" || fail "SHA256SUMS covers $nsum of $nfiles"
unzip -tq "$ZIP" >/dev/null && pass "zip integrity" || fail "zip integrity"

if [ "$FAILS" != 0 ]; then
  echo "== $FAILS check(s) FAILED; zip left at $ZIP" >&2
  exit 1
fi

bytes=$(stat -c %s "$ZIP")
echo "== ALL CHECKS PASSED"
echo "zip:    $ZIP"
echo "size:   $bytes bytes ($(awk -v b="$bytes" 'BEGIN{printf "%.1f", b/1048576}') MB)"
echo "sha256: $(cut -d' ' -f1 "$ZIP.sha256")"
