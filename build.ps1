[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Project3'),
    [string]$DeployPath,
    [switch]$NoDeploy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$talkRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
if (-not $DeployPath) { $DeployPath = Join-Path $ProjectPath 'mods\FreeWorldTalk' }
$DeployPath = [IO.Path]::GetFullPath($DeployPath)

function Assert-TalkFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file missing: $Path" }
}
function Resolve-TalkReference([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $ProjectPath $Path))
}

$projectFile = Join-Path $ProjectPath 'Assembly-CSharp.csproj'
$gameAssembly = Join-Path $ProjectPath 'Library\ScriptAssemblies\Assembly-CSharp.dll'
Assert-TalkFile $projectFile
Assert-TalkFile $gameAssembly
[xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
$hintPaths = @($projectXml.SelectNodes("//*[local-name()='Reference']/*[local-name()='HintPath']") | ForEach-Object { Resolve-TalkReference $_.InnerText })
$mscorlib = @($hintPaths | Where-Object { [IO.Path]::GetFileName($_) -eq 'mscorlib.dll' }) | Select-Object -First 1
if (-not $mscorlib) { throw 'Unity .NET Framework reference assemblies were not found in Assembly-CSharp.csproj. Regenerate project files in Unity.' }
$apiRoot = Split-Path -Parent $mscorlib
Assert-TalkFile $mscorlib

$harmonyVersion = '2.4.2'
$harmonyPackageHash = 'D64592E53090464559FCE48612C9CA7C8DC73113841376B7AA3455F46FC5D579'
$harmonyDllHash = '7B9E756306FA3D7620E02A857C8927A6AB04973F9BD8A77D3866700A6DEAC55C'
$vendorRoot = Join-Path $talkRoot 'vendor'
$harmonyRoot = Join-Path $vendorRoot "Harmony-$harmonyVersion"
$harmonyPackage = Join-Path $vendorRoot "Lib.Harmony.$harmonyVersion.nupkg"
$harmonyDll = Join-Path $harmonyRoot '0Harmony.dll'
New-Item -ItemType Directory -Force -Path $vendorRoot, $harmonyRoot | Out-Null
if (-not (Test-Path -LiteralPath $harmonyPackage -PathType Leaf)) {
    Write-Host "Downloading official Lib.Harmony $harmonyVersion..."
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/lib.harmony/$harmonyVersion/lib.harmony.$harmonyVersion.nupkg" -OutFile $harmonyPackage
}
if ((Get-FileHash -LiteralPath $harmonyPackage -Algorithm SHA256).Hash -ne $harmonyPackageHash) { throw 'Harmony NuGet SHA256 does not match the pinned dependency.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($harmonyPackage)
try {
    foreach ($entryName in @('lib/net472/0Harmony.dll', 'LICENSE')) {
        $entry = $zip.GetEntry($entryName)
        if ($null -eq $entry) { throw "Harmony package is missing $entryName" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $harmonyRoot ([IO.Path]::GetFileName($entryName))), $true)
    }
} finally { $zip.Dispose() }
if ((Get-FileHash -LiteralPath $harmonyDll -Algorithm SHA256).Hash -ne $harmonyDllHash) { throw 'Harmony DLL SHA256 does not match the pinned net472 assembly.' }

$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$sdkLines = @(& $dotnet --list-sdks)
if ($LASTEXITCODE -ne 0) { throw 'dotnet --list-sdks failed.' }
$sdkLine = $sdkLines | Select-Object -Last 1
if (-not $sdkLine -or $sdkLine -notmatch '^([^ ]+) \[(.+)\]$') { throw 'A .NET SDK with Roslyn is required; dotnet runtime alone is insufficient.' }
$compiler = Join-Path (Join-Path $Matches[2] $Matches[1]) 'Roslyn\bincore\csc.dll'
Assert-TalkFile $compiler

$references = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $apiRoot -Filter '*.dll' -File) { [void]$references.Add($file.FullName) }
$facades = Join-Path $apiRoot 'Facades'
if (Test-Path -LiteralPath $facades -PathType Container) {
    foreach ($file in Get-ChildItem -LiteralPath $facades -Filter '*.dll' -File) { [void]$references.Add($file.FullName) }
}
[void]$references.Add($gameAssembly)
[void]$references.Add($harmonyDll)
foreach ($path in $hintPaths) {
    $name = [IO.Path]::GetFileName($path)
    if (($name -match '^UnityEngine(\..+)?\.dll$' -and $name -notmatch 'Editor') -or $name -in @('Unity.TextMeshPro.dll', 'Newtonsoft.Json.dll', 'Assembly-CSharp-firstpass.dll')) {
        Assert-TalkFile $path
        [void]$references.Add($path)
    }
}
foreach ($required in @('UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.InputLegacyModule.dll', 'UnityEngine.UI.dll', 'Unity.TextMeshPro.dll', 'Newtonsoft.Json.dll')) {
    if (-not @($references | Where-Object { [IO.Path]::GetFileName($_) -eq $required }).Count) { throw "Required reference is absent: $required" }
}

$sources = @(Get-ChildItem -LiteralPath (Join-Path $talkRoot 'src') -Recurse -Filter '*.cs' -File | Sort-Object FullName)
if ($sources.Count -eq 0) { throw 'No C# sources were found in src.' }
$artifactRoot = Join-Path $talkRoot 'artifacts'
$packageRoot = Join-Path $talkRoot 'package'
$assemblyRoot = Join-Path $packageRoot 'v_common\Assemblies'
New-Item -ItemType Directory -Force -Path $artifactRoot, $assemblyRoot | Out-Null
$outputDll = Join-Path $artifactRoot 'FreeWorldTalk.dll'
$responseFile = Join-Path $artifactRoot 'compile.rsp'
$compileArgs = @('/nostdlib+', '/target:library', '/langversion:latest', '/deterministic+', '/optimize+', '/debug:portable', '/utf8output', '/warn:4', ('/out:"' + $outputDll + '"'))
$compileArgs += @($references | Sort-Object | ForEach-Object { '/reference:"' + $_ + '"' })
$compileArgs += @($sources | ForEach-Object { '"' + $_.FullName + '"' })
[IO.File]::WriteAllLines($responseFile, [string[]]$compileArgs, (New-Object Text.UTF8Encoding($false)))
Write-Host "Building $($sources.Count) source files against $gameAssembly"
& $dotnet $compiler /noconfig "@$responseFile"
if ($LASTEXITCODE -ne 0) { throw "C# compilation failed (exit $LASTEXITCODE); the installed Mod was not touched." }
Assert-TalkFile $outputDll
Copy-Item -LiteralPath $outputDll -Destination (Join-Path $assemblyRoot 'FreeWorldTalk.dll') -Force
Copy-Item -LiteralPath $harmonyDll -Destination (Join-Path $assemblyRoot '0Harmony.dll') -Force
Copy-Item -LiteralPath (Join-Path $harmonyRoot 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE.Harmony.txt') -Force
$packageReadme = [IO.File]::ReadAllText((Join-Path $talkRoot 'README.md')).Replace('](package/', '](')
[IO.File]::WriteAllText((Join-Path $packageRoot 'README.md'), $packageReadme, (New-Object Text.UTF8Encoding($false)))
Copy-Item -LiteralPath (Join-Path $talkRoot 'DEPENDENCIES.md') -Destination (Join-Path $packageRoot 'DEPENDENCIES.md') -Force
$documentationRoot = Join-Path $talkRoot 'docs\images'
if (-not (Test-Path -LiteralPath $documentationRoot -PathType Container)) { throw 'README SVG sources are missing: docs/images.' }
$documentationFiles = @(Get-ChildItem -LiteralPath $documentationRoot -Filter '*.svg' -File | Sort-Object Name | ForEach-Object {
    $relative = 'docs\images\' + $_.Name
    $destination = Join-Path $packageRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    $relative
})
if ($documentationFiles.Count -eq 0) { throw 'README requires at least one local SVG in docs/images.' }
Write-Host "Built Mod package: $packageRoot"

# Explicit distribution allowlist: never include personal data, even if it is copied into package accidentally.
$zipFiles = @('Manifest\manifest.xml', 'Manifest\icon.png', 'Manifest\preview.png', 'Prompts\prompts.json', 'Prompts\migration-v1.json', 'Prompts\role-generator.json', 'README.md', 'DEPENDENCIES.md', 'LICENSE.Harmony.txt', 'v_common\Assemblies\0Harmony.dll', 'v_common\Assemblies\FreeWorldTalk.dll')
$zipFiles += $documentationFiles
$zipPath = Join-Path $artifactRoot 'FreeWorldTalk.zip'
$zipTemp = Join-Path $artifactRoot ('FreeWorldTalk.' + [Guid]::NewGuid().ToString('N') + '.zip')
$zipOutput = [IO.Compression.ZipFile]::Open($zipTemp, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($relative in $zipFiles) {
        $source = Join-Path $packageRoot $relative
        Assert-TalkFile $source
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zipOutput, $source, ('FreeWorldTalk/' + $relative.Replace('\', '/')), [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zipOutput.Dispose() }
if (Test-Path -LiteralPath $zipPath -PathType Leaf) { [IO.File]::Replace($zipTemp, $zipPath, [System.Management.Automation.Language.NullString]::Value) }
else { [IO.File]::Move($zipTemp, $zipPath) }
Write-Host "Distribution ZIP: $zipPath"

if (-not $NoDeploy) {
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $ProjectPath 'mods')) + [IO.Path]::DirectorySeparatorChar
    if (-not $DeployPath.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "DeployPath must be a child of $allowedRoot" }
    if ([IO.Path]::GetFileName($DeployPath.TrimEnd('\', '/')) -ne 'FreeWorldTalk') { throw 'DeployPath must name the FreeWorldTalk Mod folder.' }
    New-Item -ItemType Directory -Force -Path $DeployPath | Out-Null
    foreach ($relativePath in $zipFiles) {
        $source = Join-Path $packageRoot $relativePath
        $destination = Join-Path $DeployPath $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        # This JSON belongs to the player after first installation. Updates preserve it and all unknown files/backups.
        if ($relativePath -in @('Prompts\prompts.json', 'Prompts\role-generator.json') -and (Test-Path -LiteralPath $destination -PathType Leaf)) {
            Write-Host "Preserved player prompt configuration: $destination"
            continue
        }
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    Write-Host "Installed Mod: $DeployPath"
    Write-Host 'Enable FreeWorldTalk in the game Mod manager, then restart Play Mode. The build does not edit the game activation list.'
}
