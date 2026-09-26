# syntax=docker/dockerfile:1
FROM node:24-bookworm-slim AS css
WORKDIR /src
COPY package.json package-lock.json .browserslistrc ./
RUN --mount=type=cache,target=/root/.npm \
    npm ci --ignore-scripts --no-audit --no-fund --prefer-offline
COPY scripts/build-css.mjs ./scripts/build-css.mjs
COPY web/assets ./web/assets
RUN npm run build:css

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
# Compile against the committed stylesheet while the css stage runs, so only
# the embedding package and the link step wait for the fresh bundle.
RUN --mount=type=cache,target=/root/.cache/go-build go build ./...
COPY --from=css /src/web/static/css/terrascale.css ./web/static/css/terrascale.css
RUN --mount=type=cache,target=/root/.cache/go-build \
    go build -buildvcs=false -ldflags='-s -w' -o /out/dashboard ./cmd/dashboard

FROM gcr.io/distroless/static-debian12:nonroot
WORKDIR /
ENV DASHBOARD_ADDR=:8080 \
    TS_DASHBOARD_PRODUCTION_DEMO=1
COPY --from=build --chown=nonroot:nonroot /out/dashboard /dashboard
EXPOSE 8080
ENTRYPOINT ["/dashboard"]
