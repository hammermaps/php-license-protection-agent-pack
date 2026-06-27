FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/LicenseServer/LicenseServer.csproj LicenseServer/
RUN dotnet restore LicenseServer/LicenseServer.csproj

COPY src/LicenseServer/ LicenseServer/
RUN dotnet publish LicenseServer/LicenseServer.csproj \
    -c Release -r linux-x64 --self-contained false \
    -o /app/publish

# ── runtime image ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Non-root user for production safety
RUN addgroup --system mmprotect && adduser --system --ingroup mmprotect mmprotect
USER mmprotect

# Configurable listen port — override at runtime: -e ASPNETCORE_HTTP_PORTS=9090
# Compose / docker run: set MMPROTECT_PORT which controls both host mapping and ASPNETCORE_HTTP_PORTS.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "MmProtect.LicenseServer.dll"]
