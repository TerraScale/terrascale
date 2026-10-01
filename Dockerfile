# syntax=docker/dockerfile:1
FROM node:24-bookworm-slim AS assets
WORKDIR /src
COPY package.json package-lock.json .npmrc .browserslistrc ./
RUN --mount=type=cache,target=/root/.npm \
    npm ci --ignore-scripts --no-audit --no-fund --prefer-offline
COPY scripts/build-css.mjs scripts/build-js.mjs ./scripts/
COPY web/assets ./web/assets
# The JS build checks Datastar against the pinned release hashes.
COPY internal/ui/datastar_pins.go ./internal/ui/datastar_pins.go
# The stylesheet links the fonts by content hash.
COPY web/static/fonts ./web/static/fonts
# Release bundles ship brotli-precompressed siblings served in their place.
RUN npm run build:css -- --release && npm run build:js -- --release

FROM golang:1.27.1-bookworm AS build
WORKDIR /src
ENV CGO_ENABLED=0 \
    GOFLAGS='-mod=vendor -trimpath' \
    GOTOOLCHAIN=local
COPY go.mod go.sum ./
COPY vendor ./vendor
COPY cmd ./cmd
COPY internal ./internal
COPY web ./web
# Compile against the committed stylesheet and a throwaway script placeholder
# while the assets stage runs, so only the embedding package and the link step
# wait for the fresh bundles.
RUN --mount=type=cache,target=/root/.cache/go-build \
    --mount=type=tmpfs,target=/src/web/static/js \
    touch web/static/js/placeholder && go build ./...
COPY --from=assets /src/web/static/css ./web/static/css
COPY --from=assets /src/web/static/js ./web/static/js
# The deploy workflows pass the commit; health probes report it as the version.
ARG VERSION=dev
RUN --mount=type=cache,target=/root/.cache/go-build \
    go build -buildvcs=false \
      -ldflags="-s -w -X terrascale.tech/dashboard/internal/platform/buildinfo.version=${VERSION}" \
      -o /out/dashboard ./cmd/dashboard

FROM gcr.io/distroless/static-debian12:nonroot
WORKDIR /
ENV DASHBOARD_ADDR=:8080 \
    TS_DASHBOARD_PRODUCTION_DEMO=1
COPY --from=build --chown=nonroot:nonroot /out/dashboard /dashboard
EXPOSE 8080
ENTRYPOINT ["/dashboard"]
