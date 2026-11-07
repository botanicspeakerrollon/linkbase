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
base images are build arguments, so a bank can use its own registry mirror or Red Hat's UBI .NET images. Migrations run as
a separate job from the same image (`model migrate`), never at start-up: an Argo CD PreSync hook, a Helm hook, or by hand.

## Deploying

`deploy/` holds both styles, as examples to copy:

- **Kustomize**: a base with the Deployment, Service, ConfigMap, a Secret with no values, the migration Job, a
  NetworkPolicy and probes, and overlays for OpenShift (a Route), Kubernetes (an Ingress) and a throwaway dev Postgres.
- **Helm**: a chart with the same pieces, chosen by values.
- **Argo CD**: an `Application` for each.

Real environments use the platform's own Postgres, given to the app as a Secret.

## Pipelines

- **GitHub Actions** builds, tests, and builds the image.
- **GitLab CI** does the same, pushes the image to the GitLab registry, and can update an overlay's image tag for Argo CD
  to roll out. On-prem runners often can't run Docker-in-Docker, so the tests can use an existing Postgres given by
  `LINKBASE_TEST_CONNECTION`, such as a GitLab `services: postgres`, instead of Testcontainers.
- Both produce an SBOM and scan the image with Trivy.

## Order

1. The app: logs, health, proxies and path prefix, Data Protection keys, OpenTelemetry.
2. The image, built in CI.
3. Tests against an existing Postgres, and the GitLab pipeline.
4. `deploy/`: Kustomize, Helm and Argo CD.
