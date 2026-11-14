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
# Restores again, now the source is here: the first restore saw no .razor files and left out the Blazor script (blazor.web.js),
# so every page rendered and none was interactive. Packages are cached by then, so it costs seconds.
RUN dotnet publish src/Linkbase/Linkbase.csproj -c Release -o /app -p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app

# Security fixes released since the base image was built, which the image scan would otherwise report. Then the library
# Npgsql needs to offer Kerberos (GSSAPI) when it connects, as many enterprise Postgres servers use, or to decline it
# quietly. Ubuntu and Debian images have apt, and Red Hat's UBI images microdnf.
USER root
RUN if command -v apt-get > /dev/null; then \
        apt-get update && apt-get upgrade -y && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
        && rm -rf /var/lib/apt/lists/*; \
    elif command -v microdnf > /dev/null; then \
        microdnf upgrade -y && microdnf install -y krb5-libs && microdnf clean all; \
    fi

# Owned by root and readable by everyone, so whatever user id the platform picks can run it, and none can change it.
COPY --from=build /app ./

# The example models, which `model setup` loads (see docker-compose.demo.yml). The .dockerignore leaves out the others.
COPY models/ ./models/

# The requirements, which the demo shows in the app (Docs:Pages). The .dockerignore leaves out every other document, and a
# deployment that doesn't set Docs:Pages never reads this one.
COPY docs/ ./docs/

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    HttpsRedirection=false \
    DOTNET_NOLOGO=true
EXPOSE 8080

# The Microsoft images' non-root user. OpenShift replaces it with its own, which works just as well.
ARG RUNTIME_USER=1654
USER ${RUNTIME_USER}

ENTRYPOINT ["dotnet", "Linkbase.dll"]
