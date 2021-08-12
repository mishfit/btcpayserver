#!/bin/bash
dotnet run --no-launch-profile --no-build -c Altcoins-Release -p "BTCPayServer/BTCPayServer.csproj" -- $@
