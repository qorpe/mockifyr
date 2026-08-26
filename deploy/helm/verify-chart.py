#!/usr/bin/env python3
"""Renders the chart and asserts the security posture it promises (#241, #243).

A chart is configuration, so it cannot be unit-tested the way the engine is — but the claims it
makes ("runs unprivileged", "credentials never appear in args", "an Ingress requires admin auth")
are exactly the kind of thing that silently regresses in a template edit. This runs in CI next to
`helm lint`, so a values or template change that weakens a default fails the build.
"""
import pathlib
import re
import subprocess
import sys

CHART = "deploy/helm/mockifyr"


def render(*overrides: str) -> subprocess.CompletedProcess:
    args = ["helm", "template", "verify", CHART]
    for override in overrides:
        args += ["--set", override]
    return subprocess.run(args, capture_output=True, text=True, check=False)


def main() -> int:
    failures: list[str] = []

    def check(name: str, condition: bool) -> None:
        print(f"{'PASS' if condition else 'FAIL'}  {name}")
        if not condition:
            failures.append(name)

    default = render()
    if default.returncode != 0:
        print(default.stderr)
        return 1

    manifest = default.stdout

    # values.yaml defaults the image tag to .Chart.AppVersion, so an appVersion left behind is not a
    # cosmetic staleness — it is the chart installing an image nobody meant to install. It drifted from
    # 0.22.0 to a 1.x product precisely because nothing checked it.
    built = re.search(r"<Version>([^<]+)</Version>", pathlib.Path("Directory.Build.props").read_text())
    declared = re.search(r'appVersion:\s*"([^"]+)"', pathlib.Path(f"{CHART}/Chart.yaml").read_text())
    check(
        f"chart appVersion matches the built version ({built and built.group(1)})",
        built is not None and declared is not None and built.group(1) == declared.group(1))

    # Probes must use the endpoints that stay reachable without credentials (#242) — a probe cannot
    # authenticate, and a 401 liveness check restart-loops the pod.
    check("liveness probe uses /__admin/live", "/__admin/live" in manifest)
    check("readiness probe uses /__admin/ready", "/__admin/ready" in manifest)

    # Container hardening (#241): these are the lines an enterprise scanner looks for.
    check("pod refuses to run as root", "runAsNonRoot: true" in manifest)
    check("privilege escalation disabled", "allowPrivilegeEscalation: false" in manifest)
    check("all capabilities dropped", "drop:" in manifest and "ALL" in manifest)
    check("seccomp profile is RuntimeDefault", "RuntimeDefault" in manifest)

    # Safe defaults.
    check("admin auth is on by default", "MOCKIFYR_ADMIN_PASS" in manifest)
    check("admin credentials are injected from a Secret", "secretKeyRef" in manifest)
    check("request journal is bounded by default", "--journal-limit" in manifest)
    check("durable state is mounted at /work", "mountPath: /work" in manifest)

    # An Ingress without admin auth is refused outright rather than rendered.
    exposed = render("ingress.enabled=true", "adminAuth.enabled=false")
    check(
        "Ingress without admin auth is refused",
        exposed.returncode != 0 and "adminAuth.enabled" in exposed.stderr,
    )

    # Cryptography appears only when keys are configured, and only through a Secret.
    with_keys = render("cryptography.decryptKey=k1", "cryptography.signKey=k2").stdout
    check("crypto flags appear only when keys are set", "--decrypt-key" in with_keys and "--decrypt-key" not in manifest)
    check("crypto keys are injected from a Secret", "MOCKIFYR_DECRYPT_KEY" in with_keys)

    # Observability is opt-in, and a ServiceMonitor with nothing to scrape is refused (#246).
    plain = manifest
    check("metrics flag absent by default", "--metrics" not in plain)
    with_metrics = render("metrics.enabled=true", "metrics.serviceMonitor.enabled=true").stdout
    check("metrics flag appears when enabled", "--metrics" in with_metrics)
    check("ServiceMonitor scrapes the unauthenticated path", "/__admin/metrics" in with_metrics)
    orphan_monitor = render("metrics.serviceMonitor.enabled=true")
    check(
        "ServiceMonitor without metrics is refused",
        orphan_monitor.returncode != 0 and "metrics.enabled" in orphan_monitor.stderr,
    )

    # The audit trail is opt-in and always bounded when on (#247) — an unbounded trail in a pod's
    # memory is a leak, and the durable copy is meant to be the log line a SIEM keeps.
    check("audit flag absent by default", "--audit" not in manifest)
    with_audit = render("audit.enabled=true", "audit.limit=250").stdout
    check("audit flag appears when enabled", "--audit" in with_audit)
    check("audit trail carries its bound", '"250"' in with_audit)

    # Key files (#250): when mounted, no key may reach the container's arguments, and the mount must
    # be read-only — the whole point of the file source is that keys stay out of the process listing
    # and can be rotated underneath a running host.
    mounted = render("cryptography.decryptKey=k1", "cryptography.mountAsFiles=true").stdout
    check("mounted keys are passed as a file path", "--decrypt-key-file" in mounted)
    check("mounted keys are not passed as an argument", "--decrypt-key\n" not in mounted)
    check("mounted keys are not injected as an env var", "MOCKIFYR_DECRYPT_KEY" not in mounted)
    check("the key mount is read-only", "readOnly: true" in mounted)
    check("env-var mode is still the default", "MOCKIFYR_DECRYPT_KEY" in with_keys)

    # Voluntary disruption and network policy are opt-in (#397). The default render is the criterion
    # that matters most: an existing install must see no new object appear because the chart learned
    # to make one.
    check("no PodDisruptionBudget by default", "kind: PodDisruptionBudget" not in manifest)
    check("no NetworkPolicy by default", "kind: NetworkPolicy" not in manifest)

    # A PDB over one replica means a node drain can never evict the pod — a safeguard that hangs the
    # very operation it exists to make safe. Refused rather than rendered.
    single_replica_pdb = render("podDisruptionBudget.enabled=true")
    check(
        "PDB below two replicas is refused",
        single_replica_pdb.returncode != 0 and "replicaCount >= 2" in single_replica_pdb.stderr,
    )
    both_bounds = render(
        "podDisruptionBudget.enabled=true", "replicaCount=2", "podDisruptionBudget.maxUnavailable=1")
    check(
        "PDB with both bounds is refused",
        both_bounds.returncode != 0 and "not both" in both_bounds.stderr,
    )
    pdb = render("podDisruptionBudget.enabled=true", "replicaCount=2").stdout
    check("PDB renders above one replica", "kind: PodDisruptionBudget" in pdb)
    check("PDB selects this release's pods", "app.kubernetes.io/instance: verify" in pdb)

    # Egress is unrestricted unless asked for: this host calls webhooks, proxy targets, the
    # persistence backend, brokers, SMTP and an OIDC issuer, and a policy that pins egress without
    # listing them produces requests that hang rather than an error anybody can read.
    netpol = render("networkPolicy.enabled=true").stdout
    check("NetworkPolicy renders when enabled", "kind: NetworkPolicy" in netpol)
    check("egress is unrestricted by default", "- Egress" not in netpol)
    restricted = render("networkPolicy.enabled=true", "networkPolicy.restrictEgress=true").stdout
    check("egress is restricted when asked for", "- Egress" in restricted)
    check("DNS survives an egress restriction", "port: 53" in restricted)

    # Every optional resource renders when asked for.
    everything = render(
        "persistence.enabled=true", "ingress.enabled=true", "route.enabled=true",
        "sandboxAuth.enabled=true", "cryptography.decryptKey=k1",
        "replicaCount=2", "podDisruptionBudget.enabled=true", "networkPolicy.enabled=true",
    ).stdout
    for kind in ("Deployment", "Service", "Ingress", "Route", "PersistentVolumeClaim", "Secret",
                 "PodDisruptionBudget", "NetworkPolicy"):
        check(f"{kind} renders when enabled", f"kind: {kind}" in everything)

    print()
    print(f"{len(failures)} failed" if failures else "chart posture verified")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
