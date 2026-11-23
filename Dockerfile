FROM mcr.microsoft.com/dotnet/runtime-deps:11.0.0-resolute-chiseled@sha256:24534e1f18024d3f8026061537fa21ba424f46cc0cdab4297c5528937b1ae960 AS terrabase
ARG TARGETARCH
COPY ${TARGETARCH}/ /app
USER 0
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","mkdir","-p","/data"]
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","chown","1654:1654","/data"]
ENV ASPNETCORE_HTTP_PORTS=
WORKDIR /data
USER 1654
ENTRYPOINT ["/app/terrabase"]

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0.0-resolute-chiseled@sha256:24534e1f18024d3f8026061537fa21ba424f46cc0cdab4297c5528937b1ae960 AS ts-auth
ARG TARGETARCH
COPY ${TARGETARCH}/ /app
USER 0
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","mkdir","-p","/data"]
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","chown","1654:1654","/data"]
ENV ASPNETCORE_HTTP_PORTS=
USER 1654
ENTRYPOINT ["/app/ts-auth"]

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute-chiseled@sha256:fc0ee12f9ea79bde6cda88c7a34e5467fe383dbb62276be0ec13e5e9cc1654ac AS dashboard
WORKDIR /app
COPY . .
EXPOSE 8080
ENTRYPOINT ["dotnet", "TerraScale.Dashboard.dll"]
