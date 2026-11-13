# Makes the CSV files of the model of this code base: its folders, files, C# types and docs, and how they refer to one another.
# It reads the files git tracks, so run it from anywhere inside the repo:  python generate.py
# The output goes beside this script and is the same for the same commit. References are found by name, not by compiling, so a
# type is only linked when its name is unique in the code base and at least 4 letters long. The model is a map, not a proof.
import csv, os, re, subprocess, sys

here = os.path.dirname(os.path.abspath(__file__))
root = subprocess.check_output(["git", "rev-parse", "--show-toplevel"], cwd=here, text=True).strip()
tracked = subprocess.check_output(["git", "ls-files"], cwd=root, text=True).splitlines()

LANGS = {".cs": "csharp", ".razor": "razor", ".js": "javascript", ".css": "css", ".md": "markdown", ".ps1": "powershell",
         ".py": "python", ".json": "json", ".csproj": "project", ".yml": "yaml"}
SKIP = ("wwwroot/lib/", "/Fixtures/", "backups/", "data/")


def keep(p):
    ext = os.path.splitext(p)[1]
    return ext in LANGS and not any(s in p for s in SKIP) and not p.endswith(".Designer.cs") and not p.startswith("models/") \
        and not p.endswith(("package-lock.json", ".min.js"))


def layer(p):
    if p.startswith("tests/"): return "tests"
    if p.startswith(("docs/", "README.md", "CLAUDE.md")): return "docs"
    if p.startswith("src/Linkbase/Core/"): return "core"
    if p.startswith("src/Linkbase/Domains/"): return "domain"
    if p.startswith("src/Linkbase/Data/"): return "data"
    if p.startswith(("src/Linkbase/Components/", "src/Linkbase/wwwroot/")): return "ui"
    if p.startswith(("scripts/", "deploy/")) or "/" not in p: return "tooling"
    return "app"


files = [p for p in tracked if keep(p)]
text = {p: open(os.path.join(root, p), encoding="utf-8-sig", errors="replace").read() for p in files}

DECL = re.compile(r"^[ \t]*(?:(?:public|internal|private|protected|sealed|abstract|static|partial|readonly|file|unsafe)\s+)*"
                  r"(record\s+struct|record\s+class|record|class|struct|interface|enum)\s+(\w+)", re.M)
NS = re.compile(r"^\s*namespace\s+([\w.]+)", re.M)


def base_list(src, pos):
    """The names after the colon of a declaration starting at pos, ignoring a primary constructor and generics."""
    end = len(src)
    depth = 0
    out = []
    i = pos
    while i < end:
        c = src[i]
        if c in "(<": depth += 1
        elif c in ")>": depth -= 1
        elif depth == 0 and c in "{;=": break
        elif depth == 0: out.append(c)
        i += 1
    head = re.sub(r"\bwhere\b.*", "", "".join(out), flags=re.S)
    if ":" not in head: return []
    return [re.sub(r"[^\w]", "", n.split(".")[-1]) for n in head.split(":", 1)[1].split(",") if n.strip()]


types = []  # key, name, kind, namespace, layer, file, bases
for p in files:
    src = text[p]
    if p.endswith(".cs"):
        m = NS.search(src)
        ns = m.group(1) if m else ""
        for d in DECL.finditer(src):
            kind = {"record struct": "record", "record class": "record"}.get(re.sub(r"\s+", " ", d.group(1)), d.group(1))
            types.append((f"{p}::{d.group(2)}", d.group(2), kind, ns, layer(p), p, base_list(src, d.end())))
    elif p.endswith(".razor"):
        name = os.path.splitext(os.path.basename(p))[0]
        inh = re.search(r"^@inherits\s+([\w.]+)", src, re.M)
        types.append((f"{p}::{name}", name, "component", "", layer(p), p, [inh.group(1).split(".")[-1]] if inh else []))

# The same name declared twice in one file (a nested type, say) shares a key, so only the first counts.
seen, deduped = set(), []
for t in types:
    if t[0] not in seen:
        seen.add(t[0])
        deduped.append(t)
types = deduped

counts = {}
for t in types: counts[t[1]] = counts.get(t[1], 0) + 1
unique = {t[1]: t for t in types if counts[t[1]] == 1 and len(t[1]) >= 4}
# Common words that are also type names are too noisy to count as a reference.
for noisy in ("Index", "Home", "Page", "Error", "Main", "Layout", "Item", "Name", "Node", "Edge", "Link", "Type", "Kind", "Test"):
    unique.pop(noisy, None)

# Folders
folders = {"": "linkbase"}
for p in files:
    d = os.path.dirname(p)
    while d and d not in folders:
        folders[d] = os.path.basename(d)
        d = os.path.dirname(d)


def parent(d): return os.path.dirname(d) if d else None


def w(name, header, rows):
    with open(os.path.join(here, name), "w", newline="", encoding="utf-8") as f:
        cw = csv.writer(f, lineterminator="\n")
        cw.writerow(header)
        cw.writerows(rows)


w("folders.csv", ["path", "name", "parent"], [(d or ".", n, (parent(d) or ".") if d else "") for d, n in sorted(folders.items())])

source_files = [p for p in files if not (p.endswith(".md") and layer(p) == "docs")]
w("files.csv", ["path", "name", "language", "layer", "lines", "folder"],
  [(p, os.path.basename(p), LANGS[os.path.splitext(p)[1]], layer(p), text[p].count("\n") + 1, os.path.dirname(p) or ".") for p in source_files])

w("types.csv", ["key", "name", "kind", "namespace", "layer", "file"], [(k, n, kd, ns, ly, f) for k, n, kd, ns, ly, f, _ in types])

# Type IMPLEMENTS Type: only bases that are unique names in the code base. Interfaces and base classes alike.
w("implements.csv", ["type", "base"], sorted({(t[0], unique[b][0]) for t in types for b in t[6] if b in unique and unique[b][0] != t[0]}))

# Type TESTS Type: FooTests tests Foo.
tests = set()
for t in types:
    if t[4] == "tests" and t[1].endswith("Tests"):
        target = unique.get(t[1][:-5])
        if target and target[4] != "tests": tests.add((t[0], target[0]))
w("tests.csv", ["test", "subject"], sorted(tests))

# File USES Type: the file names a type that is defined elsewhere.
ident = re.compile(r"[A-Za-z_]\w+")
uses = set()
for p in source_files:
    if not p.endswith((".cs", ".razor")): continue
    own = {t[1] for t in types if t[5] == p}
    for n in set(ident.findall(text[p])):
        if n in unique and n not in own: uses.add((p, unique[n][0]))
w("uses.csv", ["file", "type"], sorted(uses))

# Docs
by_base = {}
for p in source_files: by_base.setdefault(os.path.basename(p), []).append(p)
docs = [p for p in files if p.endswith(".md") and layer(p) == "docs"]
doc_rows, mentions, covers, doc_links = [], set(), set(), set()
for p in docs:
    src = text[p]
    h = re.search(r"^#\s+(.+)$", src, re.M)
    doc_rows.append((p, h.group(1).strip() if h else os.path.basename(p), src.count("\n") + 1, "history" if "/history/" in p else "current"))
    for tick in re.findall(r"`([^`\n]+)`", src):
        name = re.sub(r"[^\w.]", "", tick.split("(")[0]).split(".")[-1]
        if name in unique: mentions.add((p, unique[name][0]))
        if tick in text and tick != p and tick in source_files: covers.add((p, tick))
        elif "/" not in tick and tick in by_base and len(by_base[tick]) == 1: covers.add((p, by_base[tick][0]))
    for target in re.findall(r"\]\(([^)#\s]+\.md)", src):
        q = os.path.normpath(os.path.join(os.path.dirname(p), target)).replace("\\", "/")
        if q in docs and q != p: doc_links.add((p, q))
w("docs.csv", ["path", "name", "lines", "state"], doc_rows)
w("doc_mentions.csv", ["doc", "type"], sorted(mentions))
w("doc_covers.csv", ["doc", "file"], sorted(covers))
w("doc_links.csv", ["doc", "target"], sorted(doc_links))
print(f"{len(folders)} folders, {len(source_files)} files, {len(types)} types, {len(docs)} docs; "
      f"{len(uses)} uses, {len(mentions)} mentions, {len(covers)} covers")
