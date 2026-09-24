FROM node:24-bookworm-slim AS css
WORKDIR /src
COPY package.json package-lock.json .browserslistrc ./
RUN PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm ci --ignore-scripts --no-audit --no-fund
COPY scripts/build-css.mjs ./scripts/build-css.mjs
COPY web/assets ./web/assets
RUN npm run build:css

FROM golang:1.27.1-bookworm AS build
WORKDIR /src
ENV CGO_ENABLED=0 \
    GOFLAGS=-mod=vendor \
    GOTOOLCHAIN=local
COPY go.mod go.sum ./
COPY vendor ./vendor
COPY cmd ./cmd
COPY internal ./internal
COPY web ./web
COPY --from=css /src/web/static/css/terrascale.css ./web/static/css/terrascale.css
RUN go build -trimpath -buildvcs=false -ldflags='-s -w' -o /out/dashboard ./cmd/dashboard

FROM gcr.io/distroless/static-debian12:nonroot
WORKDIR /
ENV DASHBOARD_ADDR=:8080 \
    TS_DASHBOARD_PRODUCTION_DEMO=1
COPY --from=build --chown=nonroot:nonroot /out/dashboard /dashboard
EXPOSE 8080
ENTRYPOINT ["/dashboard"]
