FROM mcr.microsoft.com/dotnet/aspnet:10.0.9-noble-chiseled AS base
USER app
EXPOSE 8080
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src
COPY ["DragaliaAPI/Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["nuget.config", "."]
COPY ["GuestAuth/GuestAuth.csproj", "GuestAuth/"]
COPY ["Aspire/Dawnshard.ServiceDefaults/Dawnshard.ServiceDefaults.csproj", "Aspire/Dawnshard.ServiceDefaults/"]
COPY ["Shared/DragaliaAPI.Photon.Shared/DragaliaAPI.Photon.Shared.csproj", "Shared/DragaliaAPI.Photon.Shared/"]
COPY ["DragaliaAPI/DragaliaAPI/DragaliaAPI.csproj", "DragaliaAPI/DragaliaAPI/"]
COPY ["DragaliaAPI/DragaliaAPI.Database/DragaliaAPI.Database.csproj", "DragaliaAPI/DragaliaAPI.Database/"]
COPY ["DragaliaAPI/DragaliaAPI.Shared/DragaliaAPI.Shared.csproj", "DragaliaAPI/DragaliaAPI.Shared/"]
COPY ["DragaliaAPI/DragaliaAPI.Shared.SourceGenerator/DragaliaAPI.Shared.SourceGenerator.csproj", "DragaliaAPI/DragaliaAPI.Shared.SourceGenerator/"]
COPY ["DragaliaAPI/DragaliaAPI.MasterAssetConverter/DragaliaAPI.MasterAssetConverter.csproj", "DragaliaAPI/DragaliaAPI.MasterAssetConverter/"]
COPY ["DragaliaAPI/DragaliaAPI.MissionDesigner/DragaliaAPI.MissionDesigner.csproj", "DragaliaAPI/DragaliaAPI.MissionDesigner/"]
RUN --mount=type=secret,id=proxy_ca \
    if [ -f /run/secrets/proxy_ca ]; then export SSL_CERT_FILE=/run/secrets/proxy_ca; fi; \
    dotnet restore "DragaliaAPI/DragaliaAPI/DragaliaAPI.csproj" && dotnet restore "GuestAuth/GuestAuth.csproj"
COPY [".editorconfig", ".editorconfig"]
COPY ["DragaliaAPI/", "DragaliaAPI/"]
COPY ["Aspire/", "Aspire/"]
COPY ["Shared/", "Shared/"]
COPY ["GuestAuth/", "GuestAuth/"]
RUN dotnet publish GuestAuth/GuestAuth.csproj -c Release -o /app/guestauth/ /p:UseAppHost=false
WORKDIR "/src/DragaliaAPI/DragaliaAPI"
RUN dotnet publish "DragaliaAPI.csproj" -c Release -o /app/publish/ /p:UseAppHost=false

FROM caddy:2.11.7-alpine@sha256:eb4d35b7452d1ab6171fbdbe8d9275b2301dad6dad92115d91e19fb9b24d510f AS caddy
RUN setcap -r /usr/bin/caddy

FROM base AS final
LABEL org.dawnshard.guest-auth="1"
COPY --from=build /app/publish/ .
COPY --from=build /app/guestauth/ /guestauth/
COPY --from=caddy /usr/bin/caddy /usr/bin/caddy
COPY deploy/tencent/guest-auth/CADDY_LICENSE /licenses/Caddy-LICENSE
ENTRYPOINT ["dotnet", "DragaliaAPI.dll"]

