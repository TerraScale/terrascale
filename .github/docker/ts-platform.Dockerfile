FROM mcr.microsoft.com/dotnet/runtime-deps:11.0.0-resolute-chiseled@sha256:24534e1f18024d3f8026061537fa21ba424f46cc0cdab4297c5528937b1ae960
ARG TARGETARCH
COPY ${TARGETARCH}/ /app
ENV ASPNETCORE_HTTP_PORTS=
WORKDIR /app
USER 1654
ENTRYPOINT ["/app/ts-platform"]
