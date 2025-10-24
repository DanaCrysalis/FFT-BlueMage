# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/BlueMage/*" -Force -Recurse
dotnet publish "./BlueMage.csproj" -c Release -o "$env:RELOADEDIIMODS/BlueMage" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location