FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY agents/windows/inventoryzing-agent/ ./
RUN dotnet restore src/Inventoryzing.Agent.Printer.Host/Inventoryzing.Agent.Printer.Host.csproj --locked-mode
RUN dotnet publish src/Inventoryzing.Agent.Printer.Host/Inventoryzing.Agent.Printer.Host.csproj \
    --configuration Release --output /out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out ./
RUN useradd --system --uid 10001 --create-home inventoryzing
USER inventoryzing
ENTRYPOINT ["dotnet", "Inventoryzing.Agent.Printer.Host.dll"]
