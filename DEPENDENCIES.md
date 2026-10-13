# Build dependencies

- Harmony 2.4.2, official `Lib.Harmony` NuGet package, dependency-merged `lib/net472/0Harmony.dll`.
- NuGet URL: https://api.nuget.org/v3-flatcontainer/lib.harmony/2.4.2/lib.harmony.2.4.2.nupkg
- Package SHA256: `D64592E53090464559FCE48612C9CA7C8DC73113841376B7AA3455F46FC5D579`
- DLL SHA256: `7B9E756306FA3D7620E02A857C8927A6AB04973F9BD8A77D3866700A6DEAC55C`
- License: MIT, redistributed as `package/LICENSE.Harmony.txt`.
- Roslyn compiler: installed .NET SDK, selected from `dotnet --list-sdks`.
- .NET Framework reference API: Unity Editor `Data/UnityReferenceAssemblies/unity-4.8-api`, located from the game's generated `Assembly-CSharp.csproj`.
- Game API: the game's existing `Library/ScriptAssemblies/Assembly-CSharp.dll`.
- UnityEngine, Unity UI, TextMeshPro and Newtonsoft.Json: existing game/Editor assemblies located from `Assembly-CSharp.csproj`.

Game and Unity reference DLLs are used for compilation only and are not copied into the Mod package. Newtonsoft.Json is supplied by the host Unity project; the package does not ship another conflicting copy.

Official references: [Harmony 2](https://harmony.pardeike.net/v2/), [Harmony package](https://www.nuget.org/packages/Lib.Harmony/2.4.2), [DeepSeek API](https://api-docs.deepseek.com/).
