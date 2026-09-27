# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

# NOTE: no "Remove-Item $env:RELOADEDIIMODS/<mod>/*" here on purpose, so files added to the deployed mod folder by
# hand survive a rebuild. The mod's data (FFTIVC/) is part of the project and is copied over on publish.
dotnet publish "./BlueMage.csproj" -c Release -o "$env:RELOADEDIIMODS/fftivc.jobs.bluemage" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location
