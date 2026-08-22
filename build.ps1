<#
.SYNOPSIS
    Builds the Xbox Avatar CastleForge mod into a single loadable DLL.

.DESCRIPTION
    Produces artifacts/bin/XboxAvatar.dll, which is the whole mod: the avatar
    renderer, the multiplayer bridge, the Harmony patches, and - embedded as
    resources - Harmony itself plus the capture bridge and importer, which the
    mod unpacks into !Mods/XboxAvatar on first run.

    Nothing from the game is copied into this repository. The game and engine
    assemblies are read from the client you point -GameDirectory at, XNA comes
    from the machine's GAC, and the loader assemblies come from a CastleForge
    install or release.

.PARAMETER GameDirectory
    A Castle Miner Z install, for CastleMinerZ.exe and DNA.Common.dll.

.PARAMETER CastleForgeDirectory
    A folder holding ModLoader.dll and ModLoaderExtensions.dll - either an
    installed CastleForge game folder, a CastleForge Build\Release output, or an
    extracted core release.

.PARAMETER SampleAvatar
    An .ocavatar for the smoke tests. A fixture only; never shipped. Without
    one the avatar-dependent tests SKIP, which a release build should not
    accept - read the smoke-test lines.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDirectory,

    [Parameter(Mandatory = $true)]
    [string[]]$CastleForgeDirectory,

    [string]$SampleAvatar,

    # A folder holding a built AvatarBridge.dll and AvatarBridgeInjector.exe,
    # to embed for in-game avatar capture. Optional.
    [string]$BridgeDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameRoot = (Resolve-Path -LiteralPath $GameDirectory).Path
$forgeRoots = @($CastleForgeDirectory | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })

$gameExe = Join-Path $gameRoot 'CastleMinerZ.exe'
$commonDll = Join-Path $gameRoot 'DNA.Common.dll'
foreach ($required in @($gameExe, $commonDll)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Missing required user-supplied reference: $required"
    }
}

# An installed CastleForge has all of these in one folder. A working checkout
# splits them: Harmony sits in ReferenceAssemblies, while the loader assemblies
# only exist once something has built them. Accept several roots so either
# arrangement works, rather than making the caller stage a folder by hand.
function Find-ForgeAssembly {
    param([Parameter(Mandatory = $true)][string]$Name)

    foreach ($root in $forgeRoots) {
        $candidate = Get-ChildItem -LiteralPath $root -Filter $Name -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    throw "Could not find $Name under: $($forgeRoots -join '; '). " +
        'Point -CastleForgeDirectory at a CastleForge install, or at both a checkout and a core release.'
}

$modLoader = Find-ForgeAssembly 'ModLoader.dll'
$modLoaderExtensions = Find-ForgeAssembly 'ModLoaderExtensions.dll'

# Harmony is embedded into the mod, but never committed here: this repository
# holds no binaries at all, which is what lets the source-boundary audit be a
# blanket rule instead of a list of exceptions. Take it from CastleForge, which
# already ships the exact build its loader was tested against.
$harmony = Find-ForgeAssembly '0Harmony.dll'

function Find-XnaAssembly {
    param([Parameter(Mandatory = $true)][string]$Name)

    $assembly = Get-ChildItem -Path (
        Join-Path $env:WINDIR "Microsoft.NET\assembly\GAC_32\$Name\v4.0_*\$Name.dll"
    ) -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $assembly) {
        throw "Could not find $Name in the XNA Framework 4.0 GAC."
    }
    return $assembly.FullName
}

$xnaFramework = Find-XnaAssembly 'Microsoft.Xna.Framework'
$xnaGraphics = Find-XnaAssembly 'Microsoft.Xna.Framework.Graphics'
$xnaGame = Find-XnaAssembly 'Microsoft.Xna.Framework.Game'

# Roslyn, not the v4.0.30319 compiler shipped with the framework: CastleForge
# mods are C# 7.3, and its shared sources use string interpolation and
# "using static", neither of which the old compiler understands.
function Find-Csc {
    $vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vsWhere) {
        $installation = & $vsWhere -latest -products * -property installationPath |
            Select-Object -Last 1
        if ($installation) {
            $candidate = Join-Path $installation 'MSBuild\Current\Bin\Roslyn\csc.exe'
            if (Test-Path -LiteralPath $candidate) { return $candidate }
        }
    }
    $fallback = Get-ChildItem -Path (
        Join-Path ${env:ProgramFiles(x86)} 'MSBuild\*\Bin\Roslyn\csc.exe'
    ) -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($fallback) { return $fallback.FullName }

    throw 'The Roslyn C# compiler was not found. Install Visual Studio or the Build Tools.'
}
$frameworkCsc = Find-Csc

$artifacts = Join-Path $repoRoot 'artifacts'
$bin = Join-Path $artifacts 'bin'
if (Test-Path -LiteralPath $artifacts) {
    $resolved = (Resolve-Path -LiteralPath $artifacts).Path
    if (-not $resolved.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an artifacts path outside the repository: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $bin | Out-Null

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Command
    )
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE." }
}

$modSource = Join-Path $repoRoot 'src\XboxAvatar'
$staging = Join-Path $artifacts 'embed'
New-Item -ItemType Directory -Force -Path $staging | Out-Null

# The importer is built here rather than committed, for the same reason as
# Harmony: nothing binary lives in this repository. It is embedded into the mod
# and unpacked into !Mods/XboxAvatar on first run, so the whole product is still
# the single DLL a CastleForge user drops in.
$importerOut = Join-Path $staging 'Import Xbox Avatar.exe'
$importerArguments = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/langversion:7.3',
    '/define:XBOX_AVATAR_BRAND',
    "/out:$importerOut",
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    (Join-Path $repoRoot 'tools\AvatarImporter.cs')
)
Invoke-Native 'Importer compilation' { & $frameworkCsc $importerArguments }

# Everything the mod carries inside itself. Harmony is resolved from memory by
# EmbeddedResolver; Tools is written to !Mods/XboxAvatar on first run.
$resources = @(
    "/resource:$harmony,XboxAvatar.Embedded.0Harmony.dll",
    "/resource:$importerOut,XboxAvatar.Tools.Import Xbox Avatar.exe"
)

# The native capture bridge is optional here. It needs the Windows SDK's
# C++/WinRT toolchain to build, and only avatar *capture* uses it: a player who
# already has an avatar.ocavatar never touches it. Embed it when a build is
# supplied, and say so plainly when it is not.
if ($BridgeDirectory) {
    $bridgeRoot = (Resolve-Path -LiteralPath $BridgeDirectory).Path
    foreach ($name in @('AvatarBridge.dll', 'AvatarBridgeInjector.exe')) {
        $bridgeFile = Join-Path $bridgeRoot $name
        if (-not (Test-Path -LiteralPath $bridgeFile)) {
            throw "-BridgeDirectory does not contain $name."
        }
        $resources += "/resource:$bridgeFile,XboxAvatar.Natives.$name"
    }
    Write-Host '  capture bridge: embedded.' -ForegroundColor Cyan
} else {
    Write-Host '  capture bridge: not supplied, so in-game capture is unavailable in this build.' -ForegroundColor Yellow
    Write-Host '                  Pass -BridgeDirectory to embed it. Importing an existing' -ForegroundColor Yellow
    Write-Host '                  avatar.ocavatar still works.' -ForegroundColor Yellow
}

$sources = Get-ChildItem -LiteralPath $modSource -Recurse -File -Filter *.cs |
    Select-Object -ExpandProperty FullName

$modOut = Join-Path $bin 'XboxAvatar.dll'
$modArguments = @(
    '/nologo', '/target:library', '/optimize+', '/platform:x86', '/langversion:7.3',
    '/define:CASTLEFORGE_BRAND;CMZ_MODERN_MODEL_ENTITY',
    "/out:$modOut",
    "/reference:$commonDll",
    "/reference:$gameExe",
    "/reference:$xnaFramework",
    "/reference:$xnaGraphics",
    "/reference:$xnaGame",
    "/reference:$harmony",
    "/reference:$modLoader",
    "/reference:$modLoaderExtensions"
) + $resources + $sources

Write-Host 'Building the Xbox Avatar mod.' -ForegroundColor Cyan
Invoke-Native 'Mod compilation' { & $frameworkCsc $modArguments }

# The smoke tests load the mod assembly by reflection, exactly as the game
# does, and assert on the avatar format, the protocol and the hand geometry.
$testProjects = @(
    @{ Name = 'AvatarProtocolSmoke';  Source = 'tests\Protocol\AvatarProtocolSmoke.cs';        References = @() },
    @{ Name = 'AvatarMessageIdSmoke'; Source = 'tests\Protocol\AvatarMessageIdSmoke.cs';       References = @() },
    @{ Name = 'AvatarAttachmentSmoke';Source = 'tests\Attachment\AvatarAttachmentSmoke.cs';    References = @($commonDll, $xnaFramework) },
    @{ Name = 'FirstPersonHandSmoke'; Source = 'tests\FirstPerson\FirstPersonHandSmoke.cs';    References = @($commonDll, $xnaFramework) }
)
foreach ($test in $testProjects) {
    $testOut = Join-Path $bin ($test.Name + '.exe')
    $testArguments = @('/nologo', '/target:exe', '/optimize+', '/platform:x86', '/langversion:7.3', "/out:$testOut")
    foreach ($reference in $test.References) { $testArguments += "/reference:$reference" }
    $testArguments += (Join-Path $repoRoot $test.Source)
    Invoke-Native "$($test.Name) compilation" { & $frameworkCsc $testArguments }
}

# The mod now references the loader and Harmony, so anything that loads it by
# reflection needs those alongside - in the game they sit in the game folder and
# come out of the mod's own resources. Put them beside the tests so a test
# failure is about the mod, not about an assembly it could not find.
foreach ($dependency in @($modLoader, $modLoaderExtensions, $harmony)) {
    Copy-Item -LiteralPath $dependency -Destination $bin -Force
}

$testAvatar = if ($SampleAvatar) { $SampleAvatar } else { Join-Path $gameRoot '!Mods\XboxAvatar\avatar.ocavatar' }

Write-Host ''
Write-Host 'Smoke tests:' -ForegroundColor Green
Write-Host ("  avatar: {0}" -f $testAvatar)
$smokeTests = @(
    @{ Name = 'AvatarProtocolSmoke';   Arguments = @($modOut, $testAvatar, $gameRoot); Needs = $testAvatar },
    @{ Name = 'AvatarMessageIdSmoke';  Arguments = @($gameExe, $commonDll, $modOut);   Needs = $null },
    @{ Name = 'AvatarAttachmentSmoke'; Arguments = @($modOut, $testAvatar, $gameRoot); Needs = $testAvatar },
    @{ Name = 'FirstPersonHandSmoke';  Arguments = @($modOut, $testAvatar, $gameRoot); Needs = $testAvatar }
)
foreach ($smokeTest in $smokeTests) {
    $smokeExe = Join-Path $bin ($smokeTest.Name + '.exe')
    if ($smokeTest.Needs -and -not (Test-Path -LiteralPath $smokeTest.Needs)) {
        Write-Host ("  SKIP {0}: no {1}" -f $smokeTest.Name, $smokeTest.Needs) -ForegroundColor Yellow
        continue
    }
    $smokeArguments = $smokeTest.Arguments
    Invoke-Native $smokeTest.Name { & $smokeExe @smokeArguments }
}

Write-Host ''
Write-Host 'Build completed:' -ForegroundColor Green
Get-ChildItem -LiteralPath $bin -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    Write-Host ("  {0}  {1}" -f $hash, $_.Name)
}
