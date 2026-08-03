$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
if (-not $distRoot.StartsWith($projectRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Invalid release directory.'
}

if (Test-Path -LiteralPath $distRoot) {
    Remove-Item -LiteralPath $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $distRoot | Out-Null

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $projectRoot
try {
    dotnet restore Picall.csproj --force
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    dotnet publish Picall.csproj -c Release --no-restore --no-self-contained -o (Join-Path $distRoot 'Picall')
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $runtimeRoot = [System.IO.Path]::GetFullPath((Join-Path $distRoot 'Picall\runtimes'))
    if (Test-Path -LiteralPath $runtimeRoot) {
        Get-ChildItem -LiteralPath $runtimeRoot -Directory | Where-Object { $_.Name -ne 'win-x64' } | ForEach-Object {
            $runtimeToRemove = [System.IO.Path]::GetFullPath($_.FullName)
            if (-not $runtimeToRemove.StartsWith($runtimeRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw 'Invalid runtime cleanup path.'
            }
            Remove-Item -LiteralPath $runtimeToRemove -Recurse -Force
        }
    }

    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.txt') -Destination (Join-Path $distRoot 'Picall\README.txt')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $distRoot 'Picall\THIRD-PARTY-NOTICES.txt')
    Compress-Archive -Path (Join-Path $distRoot 'Picall\*') -DestinationPath (Join-Path $distRoot 'Picall-payload.zip') -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $distRoot 'Picall') -DestinationPath (Join-Path $distRoot 'Picall-portable.zip') -CompressionLevel Optimal

    $compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
    $references = @(
        'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll',
        'System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll', 'Microsoft.CSharp.dll'
    ) | ForEach-Object { '/reference:' + $_ }
    $arguments = @(
        '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
        ('/out:' + (Join-Path $distRoot 'Picall-Setup.exe')),
        ('/win32icon:' + (Join-Path $projectRoot 'Assets\picall.ico')),
        ('/win32manifest:' + (Join-Path $projectRoot 'Installer\installer.manifest')),
        ('/resource:' + (Join-Path $distRoot 'Picall-payload.zip') + ',Picall.Payload.zip')
    ) + $references + (Join-Path $projectRoot 'Installer\Program.cs')
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    Remove-Item -LiteralPath (Join-Path $distRoot 'Picall-payload.zip') -Force

    Get-ChildItem $distRoot | Select-Object Name, Length, LastWriteTime
}
finally {
    Pop-Location
}
