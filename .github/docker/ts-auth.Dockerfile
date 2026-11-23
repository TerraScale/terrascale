FROM mcr.microsoft.com/dotnet/runtime-deps:11.0.0-resolute-chiseled@sha256:24534e1f18024d3f8026061537fa21ba424f46cc0cdab4297c5528937b1ae960
ARG TARGETARCH
COPY ${TARGETARCH}/ /app
USER 0
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","mkdir","-p","/data"]
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","chown","1654:1654","/data"]
ENV ASPNETCORE_HTTP_PORTS=
USER 1654
ENTRYPOINT ["/app/ts-auth"]
