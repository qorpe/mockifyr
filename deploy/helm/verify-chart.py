#!/usr/bin/env python3
"""Renders the chart and asserts the security posture it promises (#241, #243).

A chart is configuration, so it cannot be unit-tested the way the engine is — but the claims it
makes ("runs unprivileged", "credentials never appear in args", "an Ingress requires admin auth")
are exactly the kind of thing that silently regresses in a template edit. This runs in CI next to
`helm lint`, so a values or template change that weakens a default fails the build.
"""
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

    # Every optional resource renders when asked for.
    everything = render(
        "persistence.enabled=true", "ingress.enabled=true", "route.enabled=true",
        "sandboxAuth.enabled=true", "cryptography.decryptKey=k1",
    ).stdout
    for kind in ("Deployment", "Service", "Ingress", "Route", "PersistentVolumeClaim", "Secret"):
        check(f"{kind} renders when enabled", f"kind: {kind}" in everything)

    print()
    print(f"{len(failures)} failed" if failures else "chart posture verified")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
