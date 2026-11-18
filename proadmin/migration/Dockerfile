FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY src/PilotMigration/PilotMigration.csproj src/PilotMigration/
RUN dotnet restore src/PilotMigration/PilotMigration.csproj
COPY src/PilotMigration/ src/PilotMigration/
RUN dotnet publish src/PilotMigration/PilotMigration.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Migration__DataDirectory=/data \
    TZ=Europe/Moscow
RUN mkdir -p /data && chown -R $APP_UID /data
COPY --from=build /app .
USER $APP_UID
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "PilotMigration.dll"]
