# InteropInspect

Prints types and members of the generated IL2CPP interop assemblies (`BepInEx\interop\*.dll`) and the BepInEx
core assemblies (`BepInEx\core\*.dll`) using `System.Reflection.MetadataLoadContext`. Nothing is executed or
loaded into the process, so it is safe to run while the game is running and it works for types that cannot be
loaded at runtime.

## Usage

```
dotnet run --project tools/InteropInspect -- <TypeNameRegex> [MemberRegex] [--types] [--game <dir>]
```

* `TypeNameRegex` - case-insensitive regex matched against the full type name (e.g. `^MoneyManager$`,
  `^UnityEngine\.GUI$`, `Il2CppInterop.Runtime.Injection.ClassInjector`). Nested types use `+`
  (`CanProcessor\+.*ProcessEnemies`).
* `MemberRegex` (optional) - case-insensitive regex matched against member names (fields, properties,
  ctors as `.ctor`, methods, enum values).
* `--types` - only list the matching types, no members.
* `--game <dir>` - game folder (default `D:\SteamLibrary\steamapps\common\Can 'Em All`, or env var `CANEMALL_DIR`).

Output: `[assembly] kind Full.TypeName : BaseType` followed by indented `field` / `property` / `ctor` / `method`
lines. IL2CPP game fields are exposed as properties in the interop assemblies; compiler-generated iterator
classes show up with their mangled names (`CanProcessor+<ProcessEnemies>d__39`).

## Examples

```
dotnet run --project tools/InteropInspect -- "^MoneyManager$"
dotnet run --project tools/InteropInspect -- "^MoneyManager$" "AddMoney|Money$"
dotnet run --project tools/InteropInspect -- "^UnityEngine\.GUI$" "^(Box|Button|TextField|Label)$"
dotnet run --project tools/InteropInspect -- "CanProcessor\+" --types
```

Tip: build once (`dotnet build tools/InteropInspect -c Release`) and run `tools/InteropInspect/bin/Release/net8.0/InteropInspect.exe`
directly for faster start-up.
