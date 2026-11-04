# Script Merger Remastered

Script and resource merging for The Witcher 3 Remastered.

## Source

- `src/WitcherScriptMerger`: Windows interface.
- `src/WitcherScriptMerger.Engine`: scanning, merge planning, inventory and recovery workflows.
- `src/WSCompiler.Core`: shared WitcherScript compiler and decompiler.
- `src/WSCompiler.CompilerCore`: compiler-only library built from the shared sources.
- `src/WSCompiler.Cli`: standalone WSCompiler command-line tool.

See [BUILDING.txt](BUILDING.txt) for build and publish commands and the
[compiler README](src/WSCompiler.Cli/README.md) for CLI commands and examples.

Annotation Inspector is supplied with the runtime release as a separate tool.
Its source is not included here.

Licensing and attribution are in [COPYING.txt](COPYING.txt) and [Licenses](Licenses).
