param(
    [string]$OutputDirectory,
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\publish\Amuse.Web' }
$publishPath = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $publishPath) {
    throw "Output directory already exists. Choose a new -OutputDirectory: $publishPath"
}
$stagePath = Join-Path $repoRoot ('artifacts\publish-stage-' + [Guid]::NewGuid().ToString('N'))
$framework = 'net10.0-windows10.0.17763.0'
$contained = $SelfContained.IsPresent.ToString().ToLowerInvariant()
$webProject = Join-Path $repoRoot 'Amuse.Web\Amuse.Web.csproj'
& dotnet publish $webProject -c Release -f $framework -r win-x64 --self-contained $contained -o $publishPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Web publish failed.' }
foreach ($name in @('HuggingFace','OnnxRuntime','StableDiffusionCpp')) {
    $projectPath = Join-Path $repoRoot "Amuse.Host.$name\Amuse.Host.$name.csproj"
    $hostStage = Join-Path $stagePath $name
    & dotnet publish $projectPath -c Release -f $framework -r win-x64 --self-contained $contained -o $hostStage --nologo
    if ($LASTEXITCODE -ne 0) { throw "Host publish failed: $name" }
    Get-ChildItem -LiteralPath $hostStage -Force | Copy-Item -Destination $publishPath -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repoRoot 'Amuse.Web\README.md') -Destination (Join-Path $publishPath 'README.md')
Write-Output "Published: $publishPath"
Write-Output "Start: & '$publishPath\Amuse.Web.exe' --port 8080"
