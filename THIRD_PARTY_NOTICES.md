# Third-party notices

The Can 'Em All Co-op mod itself (the plugin DLL and the source in this repository) is licensed under the MIT license,
see [LICENSE](LICENSE).

The **full** release zip (`CanEmAll-Coop-<version>-full.zip`) additionally contains the unmodified binary release of
**BepInEx 6.0.0-be.791 (Unity IL2CPP, win-x64)** and the components it ships with. They are listed below with their
licenses. The **plugin-only** zip contains none of them.

No file of the game "Can 'Em All" is included in any release package.

## Bundled components

Versions are the ones found in the shipped binaries.

| Component | Files in the zip | Version | License | Source |
|---|---|---|---|---|
| BepInEx | `BepInEx/core/BepInEx.Core.dll`, `BepInEx.Preloader.Core.dll`, `BepInEx.Unity.Common.dll`, `BepInEx.Unity.IL2CPP.dll` | 6.0.0-be.791 (commit `b782da4`) | **LGPL-2.1** | https://github.com/BepInEx/BepInEx |
| Unity Doorstop | `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` | 4.6.0 | **LGPL-2.1** | https://github.com/NeighTools/UnityDoorstop |
| Il2CppInterop | `Il2CppInterop.Common.dll`, `.Generator.dll`, `.HarmonySupport.dll`, `.Runtime.dll` | 1.5.3 (commit `dbda1cb`) | **LGPL-3.0** | https://github.com/BepInEx/Il2CppInterop |
| HarmonyX | `0Harmony.dll` | 2.10.2 | MIT | https://github.com/BepInEx/HarmonyX |
| MonoMod | `MonoMod.RuntimeDetour.dll`, `MonoMod.Utils.dll`, `MonoMod.Backports.dll`, `MonoMod.ILHelpers.dll` | 22.07.31.01 / 1.1.2 | MIT | https://github.com/MonoMod/MonoMod |
| Mono.Cecil | `Mono.Cecil.dll`, `.Mdb.dll`, `.Pdb.dll`, `.Rocks.dll` | 0.11.4 | MIT | https://github.com/jbevain/cecil |
| Cpp2IL | `Cpp2IL.Core.dll`, `LibCpp2IL.dll`, `StableNameDotNet.dll`, `WasmDisassembler.dll` | 2022.1.0-development.1452 | MIT | https://github.com/SamboyCoding/Cpp2IL |
| Disarm | `Disarm.dll` | 2022.1.0 | MIT | https://github.com/SamboyCoding/Disarm |
| AsmResolver | `AsmResolver.dll`, `.DotNet.dll`, `.PE.dll`, `.PE.File.dll` | 6.0.0-beta.5 | MIT | https://github.com/Washi1337/AsmResolver |
| AssetRipper.CIL, AssetRipper.Primitives | `AssetRipper.CIL.dll`, `AssetRipper.Primitives.dll` | 1.2.2 / 3.2.0 | MIT | https://github.com/AssetRipper/AssetRipper.CIL, https://github.com/AssetRipper/AssetRipper.Primitives |
| Iced | `Iced.dll` | 1.21.0 | MIT | https://github.com/icedland/iced |
| Gee.External.Capstone (managed wrapper of the Capstone disassembler) | `Gee.External.Capstone.dll` | 2.3.2 | MIT | https://github.com/9ee1/Capstone.NET |
| SemanticVersioning | `SemanticVersioning.dll` | 2.0.2 | MIT | https://github.com/adamreeve/semver.net |
| Dobby | `dobby.dll` | not stated in the binary | Apache-2.0 | https://github.com/jmpews/Dobby |
| .NET 6 runtime and base class libraries | `dotnet/` (`coreclr.dll`, `clrjit.dll`, `System.*.dll`, `Microsoft.*.dll`, `msquic.dll`, ...) | 6.0.x (file version 6.0.722) | MIT | https://github.com/dotnet/runtime |

Copyright notices as found in the shipped files (the MIT-licensed components keep their authors' notices):

- HarmonyX: Copyright BepInEx 2022. It is a fork of Harmony by Andreas Pardeike (MIT).
- MonoMod: Copyright 2022 0x0ade (RuntimeDetour, Utils); Copyright 2024 0x0ade, DaNike (Backports).
- Mono.Cecil: Copyright 2008 - 2018 Jb Evain.
- Cpp2IL, LibCpp2IL, StableNameDotNet, WasmDisassembler: Copyright Samboy063 2019-2023. Disarm: Copyright Samboy063 2022.
- AsmResolver: Copyright Washi 2016-2025.
- AssetRipper.CIL: Copyright (c) 2025 ds5678. AssetRipper.Primitives: Copyright (c) 2022-2025 ds5678.
- Iced: Copyright (C) 2018-present iced project and contributors.
- Gee.External.Capstone: Copyright (c) Ahmed Garhy. The native Capstone library it wraps is **not** part of the zip.
- SemanticVersioning: Copyright 2016 Adam Reeve.
- .NET runtime: Copyright (c) .NET Foundation and Contributors, and Microsoft Corporation. The .NET runtime in turn
  contains third-party code under its own notices, see
  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

## The LGPL components (BepInEx, Doorstop, Il2CppInterop)

These libraries are shipped **unmodified**, exactly as in the upstream binary releases, as separate files that are
loaded at run time. The mod's own DLL is a separate work that references them dynamically. You can replace any of these
files with your own build of the same component. The corresponding source code is available from the upstream
repositories linked in the table, at the commits and versions given there.

Full license texts:

- GNU Lesser General Public License 2.1 (BepInEx, Unity Doorstop): [licenses/LGPL-2.1.txt](licenses/LGPL-2.1.txt)
- GNU Lesser General Public License 3.0 (Il2CppInterop), which is written as additional permissions on top of the GNU
  GPL 3.0: [licenses/LGPL-3.0.txt](licenses/LGPL-3.0.txt) and [licenses/GPL-3.0.txt](licenses/GPL-3.0.txt)
- Apache License 2.0 (Dobby): [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)

## MIT license (HarmonyX, MonoMod, Mono.Cecil, Cpp2IL and related, Disarm, AsmResolver, AssetRipper.*, Iced, Gee.External.Capstone, SemanticVersioning, .NET)

Each of the MIT-licensed components above is covered by the following text, with the copyright holder named in the
list of notices above:

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Build-time only (not shipped)

The mod is compiled against the game's generated interop assemblies and the BepInEx core libraries on your own
machine. The test project uses xUnit (Apache-2.0) and Microsoft.NET.Test.Sdk (MIT) from NuGet. None of these end up in
a release package.

## Trademarks

"Can 'Em All" and its art belong to their respective owners. This project is an unofficial fan mod and is not
affiliated with or endorsed by the game's developer or publisher. Steam is a trademark of Valve Corporation.
