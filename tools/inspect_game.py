"""
Search and decompile the game's own assemblies.

Modding Cities: Skylines II means writing against code with no published source and no
documentation. Guessing at what a game system does to an entity is how you end up with a
mod that works until it does not. This fetches the ILSpy decompiler as a NuGet package,
hosts it on the same .NET runtime tools/build.py uses, and points it at
Cities2_Data/Managed.

    python tools/inspect_game.py find Highlight            # types whose name matches
    python tools/inspect_game.py members MeshColorSystem   # methods and fields of a type
    python tools/inspect_game.py show Game.Rendering.MeshColorSystem
    python tools/inspect_game.py grep m_ColorSet --types "*ColorSystem"

Requires: Python 3.8+, and `pip install pythonnet` on first run.
"""

import argparse
import fnmatch
import glob
import io
import os
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, ".buildcache")

DEFAULT_GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II"

# ILSpy 7.2 rather than a newer line: 8.x wants System.Reflection.Metadata 8, and the
# runtime the game's launcher installs is .NET 6, which already supplies its own copy of
# that assembly. Loading a second version of it fails outright, so this stays on the
# release whose dependencies the shared framework already satisfies.
PACKAGES = [
    ("icsharpcode.decompiler", "7.2.1.6856", "lib/netstandard2.0/ICSharpCode.Decompiler.dll"),
]

RUNTIME_CONFIG = (
    '{"runtimeOptions":{"tfm":"net6.0",'
    '"framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}'
)


def fetch(name, version, dll):
    target = os.path.join(CACHE, name, dll.replace("/", os.sep))
    if not os.path.exists(target):
        url = "https://www.nuget.org/api/v2/package/%s/%s" % (name, version)
        print("Fetching %s %s ..." % (name, version), file=sys.stderr)
        with urllib.request.urlopen(url) as response:
            data = response.read()
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            archive.extractall(os.path.join(CACHE, name))
    if not os.path.exists(target):
        raise SystemExit("Could not unpack %s from %s" % (dll, name))
    return target


def load_runtime():
    os.makedirs(CACHE, exist_ok=True)
    paths = [fetch(*p) for p in PACKAGES]

    try:
        from clr_loader import get_coreclr
        from pythonnet import set_runtime
    except ImportError:
        raise SystemExit("pythonnet is required: pip install pythonnet")

    config = os.path.join(CACHE, "runtimeconfig_inspect.json")
    with io.open(config, "w", encoding="utf-8") as handle:
        handle.write(RUNTIME_CONFIG)

    set_runtime(get_coreclr(runtime_config=config))

    import clr
    for path in paths:
        clr.AddReference(path)


def assemblies(managed, pattern):
    """The game's own assemblies. Unity and system DLLs are skipped by default."""
    out = []
    for path in sorted(glob.glob(os.path.join(managed, "*.dll"))):
        name = os.path.basename(path)
        if pattern:
            if fnmatch.fnmatch(name, pattern):
                out.append(path)
        elif name.startswith(("Game", "Colossal")):
            out.append(path)
    return out


def make_decompiler(path):
    from ICSharpCode.Decompiler import DecompilerSettings
    from ICSharpCode.Decompiler.CSharp import CSharpDecompiler
    settings = DecompilerSettings()
    settings.ThrowOnAssemblyResolveErrors = False
    return CSharpDecompiler(path, settings)


def iter_types(path):
    """Yields (fullname, TypeDefinitionHandle) for every type in an assembly."""
    from ICSharpCode.Decompiler.Metadata import PEFile
    peFile = PEFile(path)
    reader = peFile.Metadata
    for handle in reader.TypeDefinitions:
        td = reader.GetTypeDefinition(handle)
        name = reader.GetString(td.Name)
        ns = reader.GetString(td.Namespace)
        full = (ns + "." + name) if ns else name
        yield full, handle, peFile


def cmd_find(args, managed):
    needle = args.pattern.lower()
    for path in assemblies(managed, args.assembly):
        for full, _handle, _pe in iter_types(path):
            if needle in full.lower():
                print("%-28s %s" % (os.path.basename(path), full))


def cmd_show(args, managed):
    from System import Reflection
    for path in assemblies(managed, args.assembly):
        decompiler = make_decompiler(path)
        try:
            code = decompiler.DecompileTypeAsString(
                _type_name(args.name))
        except Exception:
            continue
        if code and code.strip():
            print("// ---- %s : %s ----" % (os.path.basename(path), args.name))
            print(code)
            return
    print("Type not found: %s" % args.name, file=sys.stderr)


def _type_name(full):
    from ICSharpCode.Decompiler.TypeSystem import FullTypeName
    return FullTypeName(full)


def cmd_grep(args, managed):
    """Decompiles matching types and prints lines containing the pattern."""
    needle = args.pattern.lower()
    for path in assemblies(managed, args.assembly):
        decompiler = None
        for full, _handle, _pe in iter_types(path):
            if args.types and not fnmatch.fnmatch(full.split(".")[-1], args.types):
                continue
            if decompiler is None:
                decompiler = make_decompiler(path)
            try:
                code = decompiler.DecompileTypeAsString(_type_name(full))
            except Exception:
                continue
            if not code:
                continue
            lines = code.split("\n")
            hits = [(i, l) for i, l in enumerate(lines) if needle in l.lower()]
            if not hits:
                continue
            print("=== %s (%s) ===" % (full, os.path.basename(path)))
            for i, line in hits[: args.max_hits]:
                print("%5d  %s" % (i + 1, line.rstrip()))
            print()


def main():
    parser = argparse.ArgumentParser(description="Search and decompile the game's assemblies.")
    parser.add_argument("--game", default=DEFAULT_GAME)
    parser.add_argument("--assembly", default=None,
                        help="glob over DLL filenames, e.g. 'Game.dll'")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("find", help="list types whose full name contains a string")
    p.add_argument("pattern")

    p = sub.add_parser("show", help="decompile one type to C#")
    p.add_argument("name", help="full type name, e.g. Game.Rendering.MeshColorSystem")

    p = sub.add_parser("grep", help="decompile types and search the source")
    p.add_argument("pattern")
    p.add_argument("--types", default=None, help="glob over short type names")
    p.add_argument("--max-hits", type=int, default=40)

    args = parser.parse_args()

    managed = os.path.join(args.game, "Cities2_Data", "Managed")
    if not os.path.isdir(managed):
        raise SystemExit("Game assemblies not found at %s - pass --game" % managed)

    load_runtime()

    if args.command == "find":
        cmd_find(args, managed)
    elif args.command == "show":
        cmd_show(args, managed)
    elif args.command == "grep":
        cmd_grep(args, managed)


if __name__ == "__main__":
    main()
