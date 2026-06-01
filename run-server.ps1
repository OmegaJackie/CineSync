# Starts the CineSync sync server. Friends connect to ws://<your-ip>:5252/ws
Set-Location $PSScriptRoot
dotnet run --project CineSync.Server -c Release
