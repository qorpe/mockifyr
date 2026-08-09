# The demo runner + every tool the steps need — so a Docker-only machine can run the
# whole demo without installing .NET, node, jq or grpcurl on the host.
FROM python:3.12-alpine
ARG TARGETARCH
RUN apk add --no-cache bash curl jq nodejs npm coreutils grep \
 && case "$TARGETARCH" in amd64) A=x86_64 ;; arm64) A=arm64 ;; *) A=x86_64 ;; esac \
 && curl -sL "https://github.com/fullstorydev/grpcurl/releases/download/v1.9.1/grpcurl_1.9.1_linux_${A}.tar.gz" \
    | tar -xz -C /usr/local/bin grpcurl \
 && npm install --prefix /deps ws@8 \
 && rm -rf /root/.npm
ENV NODE_PATH=/deps/node_modules
WORKDIR /repo
CMD ["python3", "demo/runner.py"]
