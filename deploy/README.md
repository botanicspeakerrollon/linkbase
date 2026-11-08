# Deploying Linkbase

Examples to copy, for any Kubernetes or OpenShift cluster, with or without Argo CD. They all run the same image (see
`Dockerfile`), and differ only in how traffic reaches it. Every app setting is in
[operations](../docs/operations.md#configuration).

| Folder | What it is |
| --- | --- |
| `kustomize/base` | The Deployment, Service, ConfigMap and NetworkPolicy every deployment has |
| `kustomize/overlays/openshift` | Adds a Route, and uses the internal OpenShift registry's image |
| `kustomize/overlays/kubernetes` | Adds an Ingress, for AKS, EKS or any cluster with an ingress controller |
| `kustomize/overlays/dev` | Adds a throwaway Postgres and its Secret, for trying it in an empty cluster. Never for real data |
| `helm/linkbase` | The same as a Helm chart: Route or Ingress by values |
| `argocd` | An Argo CD `Application` for the OpenShift overlay, and one for the Helm chart |

## Before deploying

1. **An image** in a registry the cluster can pull from: built by `.gitlab-ci.yml` into the GitLab registry, or by hand
   with `docker build`. Set it in the overlay's `images` entry or the chart's `image` values.
2. **A Postgres database**, usually the platform's own, and a user who can create tables in it.
3. **A Secret named `linkbase-secrets`** in the namespace, with at least `ConnectionStrings__Linkbase`. See
   `kustomize/base/secret.example.yaml`. Create it by hand or with your secret store (Vault, Sealed Secrets, External
   Secrets); it's never in this repo. The Helm chart can make one from values instead, but then the secret is in your
   values.

## Deploying

```powershell
kubectl apply -k deploy/kustomize/overlays/openshift      # or oc apply -k, or overlays/kubernetes
helm install linkbase deploy/helm/linkbase --set existingSecret=linkbase-secrets --set route.enabled=true
kubectl apply -f deploy/argocd/linkbase-kustomize.yaml    # Argo CD, after changing its repoURL
```

## How it behaves

- **Migrations** run in each pod's `migrate` init container, from the same image with the same settings, before the app
  starts. EF Core holds a database lock while it migrates, so pods starting together take turns. No Helm hook or Argo CD
  hook is needed, and a failed migration stops the pod before it takes traffic.
- **Probes**: startup and readiness use `/health/ready` (every model's database reachable and migrated), liveness uses
  `/health/live` (the process is up).
- **Security**: non-root, no privilege escalation, every capability dropped, a read-only root filesystem with an
  `emptyDir` at `/tmp`, and no fixed user id, so OpenShift's restricted policy accepts it as it is.
- **Replicas**: one by default. Blazor Server keeps each browser on one pod, so more needs sticky sessions: an OpenShift
  Route has them by default, and the Kubernetes Ingress asks ingress-nginx for them.
- **TLS** ends at the Route or Ingress. The app trusts its `X-Forwarded-Proto` and doesn't redirect to HTTPS itself.
  It must be at the root of its host, not under a path.
- **Logs** are JSON on stdout. Set `OTEL_EXPORTER_OTLP_ENDPOINT` in the ConfigMap to send traces, metrics and logs to an
  OpenTelemetry collector as well.

## GitOps

On the default branch, `.gitlab-ci.yml`'s `deploy-tag` job can point an overlay at the image it just built and push that
commit, for Argo CD to roll out. It runs once `GITOPS_TOKEN` is set, and `GITOPS_OVERLAY` picks the overlay.

## Checked

Each overlay renders with `kubectl kustomize`, the chart passes `helm lint` and renders with a Route or an Ingress, and every
rendered object validates against the Kubernetes 1.30 schemas with kubeconform. They haven't been applied to a real
cluster yet.
