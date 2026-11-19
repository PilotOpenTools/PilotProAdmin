FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY src/ProAdmin/PilotProAdmin.csproj src/ProAdmin/
RUN dotnet restore src/ProAdmin/PilotProAdmin.csproj
COPY src/ProAdmin/ src/ProAdmin/
COPY transfer/ transfer/
RUN dotnet publish src/ProAdmin/PilotProAdmin.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    App__DataDirectory=/data \
    TZ=Europe/Moscow
RUN mkdir -p /data && chown -R $APP_UID /data
COPY --from=build /app .
USER $APP_UID
VOLUME /data
EXPOSE 8080
# В образе нет curl: проверка - запрос /health через bash (/dev/tcp).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "PilotProAdmin.dll"]
