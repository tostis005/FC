FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY src/FC/FC.csproj src/FC/
RUN dotnet restore src/FC/FC.csproj
COPY src/FC/ src/FC/
RUN dotnet publish src/FC/FC.csproj -c Release -r linux-x64 --self-contained true -o /out

# El binario .NET 8 se publica autocontenido: no depende del runtime .NET 10 de Playwright.
# La versión de Chromium sigue alineada con Microsoft.Playwright 1.63.0.
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
# Modo gráfico real en un servidor X virtual, sin publicar puertos X11 ni VNC.
ENV FC_CHROMIUM_HEADLESS=false
EXPOSE 8080
# La imagen oficial de Playwright ya incluye Xvfb/xvfb-run.
ENTRYPOINT ["xvfb-run", "-a", "-s", "-screen 0 1440x900x24 -nolisten tcp", "/app/FC"]
