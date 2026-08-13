# ============================================================
# Publish-SingleExe.ps1
# ============================================================
#
# This script asks the .NET SDK to create a Release build for Windows x64.
#
# --self-contained true
#     Bundles the .NET runtime so the destination machine does not need to
#     install .NET separately.
#
# PublishSingleFile=true
#     Packs the managed application into a single executable. The published
#     file is renamed to include the current <Version> from BCSTool.csproj.
#
# IncludeNativeLibrariesForSelfExtract=true
#     Allows native WPF/runtime components to be included in single-file
#     publishing.
#
# Run from PowerShell:
#
#   powershell.exe -ExecutionPolicy Bypass -File .\Publish-SingleExe.ps1
#
# ============================================================

$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "BCSTool.csproj"
$PublishDirectory = Join-Path $PSScriptRoot "publish"

$VersionNode = Select-Xml `
    -Path $Project `
    -XPath "/Project/PropertyGroup/Version"

if ($null -eq $VersionNode) {
    throw "Could not find <Version> in BCSTool.csproj."
}

$Version = $VersionNode.Node.InnerText.Trim()
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "BCSTool.csproj contains an empty <Version>."
}

$ReleaseExeName = "BCS Tool v$Version.exe"
$PublishedExe = Join-Path $PublishDirectory "BCS Tool.exe"
$ReleaseExe = Join-Path $PublishDirectory $ReleaseExeName

if (Test-Path $PublishDirectory) {
    Remove-Item $PublishDirectory -Recurse -Force
}

dotnet publish $Project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $PublishDirectory `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false

if (-not (Test-Path $PublishedExe)) {
    throw "Expected published executable was not produced: $PublishedExe"
}

Move-Item -Path $PublishedExe -Destination $ReleaseExe

Write-Host ""
Write-Host "Publish complete:"
Write-Host "  $PublishDirectory"
Write-Host "  $ReleaseExe"
Write-Host ""
Write-Host "BCS Tool settings are stored in:"
Write-Host "  HKEY_CURRENT_USER\Software\BCS Tool"
Write-Host ""
Write-Host "No settings.json file is required."
