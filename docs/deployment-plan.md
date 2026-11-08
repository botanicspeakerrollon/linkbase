# Deployment plan

Linkbase should run anywhere a container runs, without changes: a bank's on-prem OpenShift with Argo CD and GitLab, plain
Kubernetes, Azure, or one server with Docker. The way to get there is a twelve-factor app: one image, configured by
environment variables, logging to stdout, with health probes and no outbound internet needed. Platform files then only
describe how to run that image.

## The app

| Need | How |
| --- | --- |
| Logs | stdout only. JSON lines outside Development (`Logging__Console__FormatterName=json`), with trace ids, and a line per request: method, path, status and time, never bodies or personal data |
| Configuration | Environment variables only, listed in [operations](operations.md#configuration). Secrets come from the platform's Secrets |
| Health | `/health/live` says the process is up. `/health/ready` says every model's database is reachable and migrated. They map onto liveness, readiness and startup probes |
| Behind a proxy | `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` honours `X-Forwarded-Proto` and `-For` from a Route or ingress that terminates TLS, and `HttpsRedirection=false` leaves TLS to it. The app runs at the root of its host: serving it under a path such as `/linkbase` needs every link made relative first |
| Restarts and replicas | Data Protection keys, which sign cookies and anti-forgery tokens, are kept in the database, so restarts don't sign anyone out. Blazor Server holds a live connection to one pod, so more than one replica needs sticky sessions: OpenShift Routes have them by default, and an ingress needs an annotation. One replica is the default |
| No internet | The graph libraries come with the app. Web import is the only thing that calls out, to Anthropic, and only when used with a key. Corporate proxies and CA bundles work through the standard `HTTPS_PROXY` and `SSL_CERT_DIR` |
| Disk | Only `/tmp` is written, so the root filesystem can be read-only with an `emptyDir` at `/tmp` |
| Telemetry | OpenTelemetry traces and metrics over OTLP, for Dynatrace, Elastic, Grafana or anything else that speaks it, when `OTEL_EXPORTER_OTLP_ENDPOINT` is set |

## The image

A multi-stage `Dockerfile` that Docker, Podman, Buildah and Kaniko all build. It runs as a non-root user and works under
OpenShift's random user id: files are readable by group 0, nothing is written outside `/tmp`, and it listens on 8080. The
base images are build arguments, so a bank can use its own registry mirror or Red Hat's UBI .NET images. Migrations run
from the same image as a separate step (`model migrate`), never inside the app process.

## Deploying

`deploy/` holds both styles, as examples to copy. See [deploy/README.md](../deploy/README.md).

- **Kustomize**: a base with the Deployment, Service, ConfigMap, NetworkPolicy and probes, and overlays for OpenShift (a
  Route), Kubernetes (an Ingress) and a throwaway dev Postgres.
- **Helm**: a chart with the same pieces, chosen by values.
- **Argo CD**: an `Application` for each.

Migrations run in each pod's init container, before the app starts. That needs no Helm or Argo CD hook, whose pre-install
and PreSync phases would run before the ConfigMap and Secret exist on a first install. EF Core's migration lock keeps pods
starting together from clashing. Real environments use the platform's own Postgres, given to the app as a Secret.

## Pipelines

- **GitHub Actions** (`.github/workflows/ci.yml`) builds and tests, including the Postgres and browser tests through
  Testcontainers, then builds the image, scans it and keeps an SBOM.
- **GitLab CI** (`.gitlab-ci.yml`) needs no Docker daemon. Tests use a `services: postgres` container through
  `LINKBASE_TEST_CONNECTION`, and the browser tests only with `RUN_BROWSER_TESTS=true`, as they
  download Chromium. Buildah builds the image and pushes it to the project's registry, and Trivy scans it and keeps an
  SBOM. Every image it uses can point at a mirror through CI/CD variables. It hasn't run on a real GitLab yet.
- Both report known vulnerabilities without failing, since most come from the base image. Making them block is one
  setting each.

## State

All four steps are done: the app (logs, health, proxies, Data Protection keys, OpenTelemetry), the image built and
scanned in GitHub's CI, tests against an existing Postgres with the GitLab pipeline, and `deploy/`. The GitHub workflow
runs on every push. The GitLab pipeline and the manifests are checked offline but haven't run on a real GitLab or
cluster.

Still to do: serving under a path prefix, which needs every link made relative, and encrypting the Data Protection keys
with a certificate if a platform asks for it.
