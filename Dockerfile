FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY src/FC/FC.csproj src/FC/
RUN dotnet restore src/FC/FC.csproj
COPY src/FC/ src/FC/
RUN dotnet publish src/FC/FC.csproj -c Release -o /out --no-restore

# Versión alineada con el paquete NuGet Microsoft.Playwright 1.63.0.
FROM mcr.microsoft.com/playwright/dotnet:v1.63.0-noble
WORKDIR /app
COPY --from=build /out/ ./
RUN (id -u pwuser >/dev/null 2>&1 || useradd --create-home --shell /bin/bash pwuser) \
    && mkdir -p /data \
    && chown -R pwuser:pwuser /data /app
ENV HOME=/home/pwuser
USER pwuser
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV FC_DATA_DIR=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "FC.dll"]
