FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY Mof.Http.csproj ./
RUN dotnet restore Mof.Http.csproj
COPY . .
RUN dotnet publish Mof.Http.csproj -c Release --no-restore -o /out /p:UseAppHost=false /p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
# MOF is a Windows x86/x64 program. Compose pins this image to linux/amd64.
USER root
RUN dpkg --add-architecture i386 \
    && apt-get update \
    && apt-get install -y --no-install-recommends wine wine64 wine32:i386 curl tini \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/ ./
COPY --chmod=755 docker/entrypoint.sh /usr/local/bin/mof-entrypoint
ENV URLS=http://0.0.0.0:8080 \
    AllowedHosts=* \
    MinistryOfFlat__BinaryDirectory=/data/MOFBinary \
    MinistryOfFlat__TempDirectory=/tmp/mof-requests \
    MinistryOfFlat__WineExecutable=/usr/bin/wine \
    WINEPREFIX=/home/app/.wine \
    WINEARCH=win64 \
    WINEDEBUG=-all \
    WINEDLLOVERRIDES=mscoree,mshtml=
RUN mkdir -p /data/MOFBinary /home/app/.wine \
    && chown -R app:app /data /home/app
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=5m --retries=3 \
    CMD curl --fail --silent http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["/usr/bin/tini", "-g", "--", "/usr/local/bin/mof-entrypoint"]
CMD ["dotnet", "Mof.Http.dll"]
