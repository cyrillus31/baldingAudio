param(
    [string]$ProjectName = 'baldingAudio',
    [string]$ProjectRoot = 'X:\Projects',
    [ValidateSet('demo','demo-flood','selftest')]
    [string]$Mode = 'demo-flood'
)

$ErrorActionPreference = 'Stop'

$repo = Join-Path $ProjectRoot $ProjectName
if (!(Test-Path $repo)) {
    throw "Repo not found: $repo"
}

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found. Install the .NET SDK on Windows (dotnet --list-sdks should work)."
}

$csproj = Join-Path $repo 'src\BaldingAudio.App\BaldingAudio.App.csproj'

Write-Host "Publishing Windows x64 into $repo\artifacts\win-x64 ..."
& dotnet publish $csproj -c Release -r win-x64 --self-contained true -o "$repo\artifacts\win-x64"

$exe = Join-Path $repo 'artifacts\win-x64\baldingAudio.exe'
if (!(Test-Path $exe)) {
    throw "Build succeeded but exe not found: $exe"
}

$args = switch ($Mode) {
    'demo'       { @('--demo') }
    'demo-flood' { @('--demo-flood') }
    'selftest'   { @('--selftest') }
}

Write-Host "Running: $exe $($args -join ' ')"
& $exe @args
