param([string]$OutputDirectory = 'TEMP/folder-smoke')
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Run the interactive folder fixture only on a disposable GitHub Windows runner.'
}

# The WPF/Forms fixture and Avalonia reference graph can build Tbox.Core with
# different global properties into the same obj folder. A cold parallel build
# races on GeneratedMSBuildEditorConfig; use one node and one Windows property.
$project = 'tests/Tbox.FolderSmoke/Tbox.FolderSmoke.csproj'
dotnet build $project -c Release -p:TboxWindows=true --disable-build-servers -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project $project -c Release -p:TboxWindows=true --no-build --no-restore -- $OutputDirectory
exit $LASTEXITCODE
