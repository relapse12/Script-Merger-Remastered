# WSCompiler
Requires the .NET 8 runtime.

## Commands
WSCompiler.exe compile --scripts <folder> [--vanilla <folder>] [--out <folder|file.rsblob>]
WSCompiler.exe check --scripts <folder> [--vanilla <folder>]
WSCompiler.exe config --vanilla <folder> # Saves the path in `WSCompiler.settings.json` beside the executable

  --scripts <folder>          Scripts to compile or check
  --vanilla <folder>          Clean content\content0\scripts; can omit after setting default with config
  --out <folder|file.rsblob>  Output location; accepts full path with file name or a folder. Defaults to precompiled.rsblob
  --reference <path>          Dependency folder, .ws, or .rsblob; can repeat for multiple
  --rtti <file>               Override native types with .xml, .txt, or .txt.gz
  --overwrite                 Replace an existing output after successful compilation.
  --json                      Print one JSON result and diagnostics, without progress text.

## Examples
```powershell
# Save the vanilla scripts folder:
.\WSCompiler.exe config --vanilla "C:\Games\The Witcher 3\content\content0\scripts" # Use your own directory

# Choose an output directory; writes modYourMod\content\precompiled.rsblob:
.\WSCompiler.exe compile --scripts "modYourMod\content\scripts" --out "modYourMod\content"

# Check scripts without writing a blob:
.\WSCompiler.exe check --scripts "modYourMod\content\scripts"

# Use a dependency's loose scripts or compiled blob as declarations only:
.\WSCompiler.exe compile --scripts "modYourMod\content\scripts" --out "modYourMod\content" --reference "modDependency\content\scripts"
.\WSCompiler.exe compile --scripts "modYourMod\content\scripts" --out "modYourMod\content" --reference "modDependency\content\precompiled.rsblob"

# Use multiple references, including a single script file:
.\WSCompiler.exe compile --scripts "modYourMod\content\scripts" --out "modYourMod\content" --reference "modDependency\content\scripts\local\shared.ws" --reference "modOtherDependency\content\precompiled.rsblob"

# Override native declarations (also accepts an XML dump or rtti.txt.gz):
.\WSCompiler.exe compile --scripts "modYourMod\content\scripts" --out "modYourMod\content" --rtti "rtti.txt"

# Replace an existing blob:
.\WSCompiler.exe compile --scripts "modYourMod\content\scripts" --out "modYourMod\content" --overwrite

# Print results and diagnostics as JSON:
.\WSCompiler.exe compile --scripts "modYourMod\content\scripts" --out "modYourMod\content" --json
.\WSCompiler.exe check --scripts "modYourMod\content\scripts" --json

# Show command options:
.\WSCompiler.exe --help
.\WSCompiler.exe compile --help
.\WSCompiler.exe check --help
.\WSCompiler.exe config --help

# Show the compiler version:
.\WSCompiler.exe --version
```

JSON includes `success`, `output`, `bytes`, `seconds` and `diagnostics`. Each diagnostic has
`severity`, `code`, `file`, `line` and `message`. Check results have `output: null`.

Exit codes: `0` success; `1` compilation/input failure; `2` command/output error; `130` cancelled.
