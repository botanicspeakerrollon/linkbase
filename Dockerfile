# The Linkbase image. It builds with Docker, Podman, Buildah or Kaniko, and runs anywhere a container runs, including
# OpenShift's restricted policy: any user id, a read-only root filesystem with /tmp mounted, and port 8080.
#
#   docker build -t linkbase .
#   docker run -p 8080:8080 -e ConnectionStrings__Linkbase="Host=...;Database=...;Username=...;Password=..." linkbase
#
# Migrations are a separate step from the same image, before the app starts:
#   docker run --rm -e ConnectionStrings__Linkbase=... linkbase model migrate
#
# A bank's own registry mirror, or Red Hat's UBI .NET images, can replace the base images:
#   --build-arg SDK_IMAGE=registry.example.com/dotnet/sdk:10.0 --build-arg RUNTIME_IMAGE=registry.example.com/dotnet/aspnet:10.0

ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM ${SDK_IMAGE} AS build
WORKDIR /src

# Restore first, so changing code doesn't download packages again.
COPY global.json Directory.Build.props ./
COPY src/Linkbase/Linkbase.csproj src/Linkbase/
RUN dotnet restore src/Linkbase/Linkbase.csproj

COPY src/ src/
RUN dotnet publish src/Linkbase/Linkbase.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app

# Npgsql offers Kerberos (GSSAPI) when it connects, which many enterprise Postgres servers use, and needs its library to
# do that or to decline quietly. Debian-based images have apt, and Red Hat's UBI images microdnf.
USER root
RUN if command -v apt-get > /dev/null; then \
        apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*; \
    elif command -v microdnf > /dev/null; then \
        microdnf install -y krb5-libs && microdnf clean all; \
    fi

# Owned by root and readable by everyone, so whatever user id the platform picks can run it, and none can change it.
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    HttpsRedirection=false \
    DOTNET_NOLOGO=true
EXPOSE 8080

# The Microsoft images' non-root user. OpenShift replaces it with its own, which works just as well.
ARG RUNTIME_USER=1654
USER ${RUNTIME_USER}

ENTRYPOINT ["dotnet", "Linkbase.dll"]
