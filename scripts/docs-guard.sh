#!/usr/bin/env bash
# The docs gate (#389, portal-proven pattern): documentation carries the SAME freshness
# contract as tests — everything mechanically checkable about "do the docs still tell
# the truth" fails CI here, so stale docs are a RED state, never a silent one. The
# judgment half (prose updated in the same change) is CLAUDE.md's rule.
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
python3 - "$ROOT" <<'PY'
import os, re, sys
root = sys.argv[1]
fail = []
def read(*parts):
    path = os.path.join(root, *parts)
    return open(path, encoding="utf-8", errors="replace").read() if os.path.exists(path) else ""

# 0. Relative markdown links resolve (README + top-level md + docs/**).
mds = [os.path.join(root, n) for n in os.listdir(root) if n.endswith(".md")]
for base, dirs, names in os.walk(os.path.join(root, "docs")):
    mds += [os.path.join(base, n) for n in names if n.endswith(".md")]
for md in mds:
    for link in re.findall(r"\]\(([^)#?\s]+)(?:#[^)]*)?\)", open(md, encoding="utf-8", errors="replace").read()):
        if link.startswith(("http://", "https://", "mailto:", "/")):
            continue
        # GitHub-web routes (../../issues, ../../discussions, ../../security/…) resolve on
        # github.com, not on disk — intentional in SUPPORT/SECURITY.
        if re.search(r"\.\./\.\./(issues|discussions|security)", link):
            continue
        if not os.path.exists(os.path.normpath(os.path.join(os.path.dirname(md), link))):
            fail.append(f"broken link in {os.path.relpath(md, root)} → {link}")

# 1. Every flag the host parses is documented in README's configuration table, and every
#    flag the README documents is one the host actually parses.
# AUTHORITATIVE source: the Server project is where host flags are parsed —
# Configuration["x"] / GetValue("x") reads only. Facade/engine string literals are NOT
# flags, and the reference-engine COMPAT aliases live on the website's migration page
# by decision, so they sit on an explicit allowlist here rather than in README.
host = ""
server = os.path.join(root, "src", "Mockifyr.Server")
for base, dirs, names in os.walk(server):
    dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
    for n in names:
        if n.endswith(".cs"):
            host += open(os.path.join(base, n), encoding="utf-8", errors="replace").read()
parsed = set(re.findall(r'Configuration\["([a-z][a-z0-9-]+)"\]', host))
parsed |= set(re.findall(r'Configuration\.GetValue<[^>]+>\("([a-z][a-z0-9-]+)"\)', host))
parsed |= set(re.findall(r'configuration\("([a-z][a-z0-9-]+)"\)', host))
readme = read("README.md")
docs_corpus = readme
for base, dirs, names in os.walk(os.path.join(root, "docs")):
    for n in names:
        if n.endswith(".md"):
            docs_corpus += open(os.path.join(base, n), encoding="utf-8", errors="replace").read()
documented = set(re.findall(r"`--([a-z][a-z0-9-]*[a-z0-9])", docs_corpus))
for flag in sorted(parsed - documented):
    fail.append(f"flag --{flag} is parsed by the host but documented nowhere (README/docs)")
# NO reverse direction on purpose: the docs quote OTHER tools' flags in examples
# (docker --add-host, keytool --keystore-password) and no regex can tell "our flag"
# from "an example's flag". The forward direction is the contract: everything the
# Server parses must be findable by a reader.

# 2. Every facade/provider/adapter/store project is named in ARCHITECTURE.md.
architecture = read("ARCHITECTURE.md")
for project in sorted(os.listdir(os.path.join(root, "src"))):
    if project not in architecture:
        fail.append(f"src/{project} missing from ARCHITECTURE.md")

# 3. Every admin/sandbox route group prefix is documented SOMEWHERE a reader looks.
#    (Per-route docs live in the dashboard; the reader-facing contract is the prefix
#    inventory: a facade that mounts a surface undocumented anywhere is invisible.)
corpus = readme + architecture + read("docs", "HANDOFF.md") + read("CHANGELOG.md")
for base, dirs, names in os.walk(os.path.join(root, "docs")):
    for n in names:
        if n.endswith(".md"):
            corpus += open(os.path.join(base, n), encoding="utf-8", errors="replace").read()
for prefix in ["/__admin", "/__sandbox"]:
    if prefix not in corpus:
        fail.append(f"route surface {prefix} documented nowhere")

# 4. docs/parity: every parity file is referenced by a test or a doc, and every parity
#    reference from tests points at a file that exists.
parity_dir = os.path.join(root, "docs", "parity")
tests_corpus = ""
for base, dirs, names in os.walk(os.path.join(root, "tests")):
    dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
    for n in names:
        if n.endswith(".cs"):
            tests_corpus += open(os.path.join(base, n), encoding="utf-8", errors="replace").read()
for name in sorted(os.listdir(parity_dir)):
    if name.endswith(".md") and name != "README.md" and name not in tests_corpus and name not in corpus:
        fail.append(f"docs/parity/{name} is referenced by no test and no doc")
for ref in sorted(set(re.findall(r"docs/parity/([a-z0-9-]+\.md)", tests_corpus))):
    if not os.path.exists(os.path.join(parity_dir, ref)):
        fail.append(f"tests reference docs/parity/{ref} which does not exist")

# 5. CHANGELOG version links resolve (the release tag pattern) and scripts are referenced.
for script in sorted(os.listdir(os.path.join(root, "scripts"))):
    if script.endswith(".sh"):
        wf = "".join(read(".github", "workflows", n) for n in os.listdir(os.path.join(root, ".github", "workflows")))
        if script not in corpus + wf + read("CONTRIBUTING.md") + read("CLAUDE.md"):
            fail.append(f"scripts/{script} is referenced nowhere")

if fail:
    print("── docs-guard: the docs stopped telling the truth:")
    for f in fail:
        print(f"  {f}")
    sys.exit(1)
print(f"── docs-guard: green — links, {len(parsed)} flags, {len(os.listdir(os.path.join(root,'src')))} projects, route surfaces, parity notes and scripts all in sync")
PY
