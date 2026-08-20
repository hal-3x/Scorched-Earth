"""
Build Scorched Earth without installing a .NET SDK.

Cities: Skylines II mods are plain .NET Framework class libraries, so the only thing a
build really needs is a C# compiler and the game's own assemblies to reference. This
script fetches the Roslyn compiler as a NuGet package, hosts it on the .NET runtime the
game's launcher already installs, and compiles straight against
Cities2_Data/Managed - including its mscorlib, so the output targets the same framework
the game runs on.

Use this if you do not have (or do not want) the .NET SDK. If you do have the SDK,
`dotnet build ScorchedEarth.csproj` does the same job and is the better-trodden path.

    python tools/build.py                 # build
    python tools/build.py --deploy        # build and copy into the game's local mods folder
    python tools/build.py --game "D:\\...\\Cities Skylines II"

Requires: Python 3.8+, and `pip install pythonnet` on first run.
"""

import argparse
import glob
import io
import os
import shutil
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
CACHE = os.path.join(HERE, ".buildcache")

DEFAULT_GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Cities Skylines II"

# Roslyn 4.4 is the last line that runs cleanly on .NET 6 without dragging in a newer
# System.Collections.Immutable than the runtime provides.
ROSLYN = [
    ("microsoft.codeanalysis.common", "4.4.0", "lib/netcoreapp3.1/Microsoft.CodeAnalysis.dll"),
    ("microsoft.codeanalysis.csharp", "4.4.0", "lib/netcoreapp3.1/Microsoft.CodeAnalysis.CSharp.dll"),
]

RUNTIME_CONFIG = (
    '{"runtimeOptions":{"tfm":"net6.0",'
    '"framework":{"name":"Microsoft.NETCore.App","version":"6.0.0"}}}'
)


def fetch_roslyn():
    """Downloads and unpacks the compiler on first run. Returns the assembly paths."""
    os.makedirs(CACHE, exist_ok=True)
    paths = []

    for name, version, dll in ROSLYN:
        target = os.path.join(CACHE, name, dll.replace("/", os.sep))
        if not os.path.exists(target):
            url = "https://www.nuget.org/api/v2/package/%s/%s" % (name, version)
            print("Fetching %s %s ..." % (name, version))
            with urllib.request.urlopen(url) as response:
                data = response.read()
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                archive.extractall(os.path.join(CACHE, name))

        if not os.path.exists(target):
            raise SystemExit("Could not unpack %s from %s" % (dll, name))

        paths.append(target)

    return paths


def load_compiler(roslyn_paths):
    """Starts the .NET runtime and loads Roslyn into this Python process."""
    try:
        from clr_loader import get_coreclr
        from pythonnet import set_runtime
    except ImportError:
        raise SystemExit("pythonnet is required: pip install pythonnet")

    config = os.path.join(CACHE, "runtimeconfig.json")
    with io.open(config, "w", encoding="utf-8") as handle:
        handle.write(RUNTIME_CONFIG)

    set_runtime(get_coreclr(runtime_config=config))

    import clr
    for path in roslyn_paths:
        clr.AddReference(path)


def compile_mod(managed_dir, sources, out_dll, out_pdb):
    from Microsoft.CodeAnalysis import MetadataReference, OptimizationLevel, OutputKind, SyntaxTree
    from Microsoft.CodeAnalysis.CSharp import (
        CSharpCompilation, CSharpCompilationOptions, CSharpParseOptions,
        CSharpSyntaxTree, LanguageVersion,
    )
    from Microsoft.CodeAnalysis.Text import SourceText
    from System.Collections.Generic import List
    from System.IO import File, FileAccess, FileMode, FileStream
    from System.Text import Encoding

    parse_options = CSharpParseOptions(LanguageVersion.CSharp9)

    trees = List[SyntaxTree]()
    for source in sources:
        # The encoding has to be carried on the source text itself, otherwise Roslyn
        # refuses to emit a PDB for it.
        text = SourceText.From(File.ReadAllText(source), Encoding.UTF8)
        trees.Add(CSharpSyntaxTree.ParseText(text, parse_options, source))

    # Reference every assembly the game ships, mscorlib included. That is what makes the
    # output a .NET Framework assembly without needing reference assemblies installed.
    references = List[MetadataReference]()
    for dll in sorted(glob.glob(os.path.join(managed_dir, "*.dll"))):
        try:
            references.Add(MetadataReference.CreateFromFile(dll))
        except Exception:
            pass  # Native or otherwise unreadable - not a managed reference.

    options = CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
    options = options.WithAllowUnsafe(True).WithOptimizationLevel(OptimizationLevel.Release)

    compilation = CSharpCompilation.Create("ScorchedEarth", trees, references, options)

    dll_stream = FileStream(out_dll, FileMode.Create, FileAccess.Write)
    pdb_stream = FileStream(out_pdb, FileMode.Create, FileAccess.Write)
    try:
        result = compilation.Emit(dll_stream, pdb_stream)
    finally:
        dll_stream.Dispose()
        pdb_stream.Dispose()

    errors, warnings = [], []
    for diagnostic in result.Diagnostics:
        severity = diagnostic.Severity.ToString()
        if severity == "Error":
            errors.append(diagnostic.ToString())
        elif severity == "Warning":
            warnings.append(diagnostic.ToString())

    return bool(result.Success), errors, warnings


def main():
    parser = argparse.ArgumentParser(description="Build Scorched Earth without a .NET SDK.")
    parser.add_argument("--game", default=DEFAULT_GAME, help="Cities: Skylines II install directory")
    parser.add_argument("--deploy", action="store_true", help="copy the result into the local mods folder")
    args = parser.parse_args()

    managed = os.path.join(args.game, "Cities2_Data", "Managed")
    if not os.path.isdir(managed):
        raise SystemExit("Game assemblies not found at %s - pass --game" % managed)

    sources = sorted(glob.glob(os.path.join(PROJECT, "src", "**", "*.cs"), recursive=True))
    sources += sorted(glob.glob(os.path.join(PROJECT, "Properties", "*.cs")))
    if not sources:
        raise SystemExit("No sources found under %s" % PROJECT)

    out_dir = os.path.join(PROJECT, "bin")
    os.makedirs(out_dir, exist_ok=True)
    out_dll = os.path.join(out_dir, "ScorchedEarth.dll")
    out_pdb = os.path.join(out_dir, "ScorchedEarth.pdb")

    load_compiler(fetch_roslyn())

    print("Compiling %d source file(s) ..." % len(sources))
    ok, errors, warnings = compile_mod(managed, sources, out_dll, out_pdb)

    for warning in warnings:
        print("warning:", warning)

    if not ok:
        for error in errors:
            print("error:", error)
        raise SystemExit("Build failed with %d error(s)." % len(errors))

    print("Built %s" % out_dll)

    if args.deploy:
        local_mods = os.path.join(
            os.environ["LOCALAPPDATA"] + "Low",
            "Colossal Order", "Cities Skylines II", ".cache", "Mods", "local", "ScorchedEarth")
        os.makedirs(local_mods, exist_ok=True)
        shutil.copy2(out_dll, local_mods)
        if os.path.exists(out_pdb):
            shutil.copy2(out_pdb, local_mods)
        print("Deployed to %s" % local_mods)


if __name__ == "__main__":
    main()
