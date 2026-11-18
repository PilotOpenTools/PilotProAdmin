FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY src/ReportMetrics.Collector/ReportMetrics.Collector.csproj src/ReportMetrics.Collector/
RUN dotnet restore src/ReportMetrics.Collector/ReportMetrics.Collector.csproj
COPY src/ReportMetrics.Collector/ src/ReportMetrics.Collector/
RUN dotnet publish src/ReportMetrics.Collector/ReportMetrics.Collector.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    TZ=Europe/Moscow
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ReportMetrics.Collector.dll"]
