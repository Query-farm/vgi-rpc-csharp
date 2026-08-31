FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish conformance/QueryFarm.VgiRpc.Tailnet/QueryFarm.VgiRpc.Tailnet.csproj \
    --configuration Release --output /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "QueryFarm.VgiRpc.Tailnet.dll"]
