# Build from the repository root, since the server shares the Windows app's core:
#   docker build -f my-usage-server/Dockerfile -t aiusage-server .
#
# The build stage runs on the machine's own architecture and publishes for the
# image's, so one command builds for both without compiling under emulation:
#   docker buildx build --platform linux/amd64,linux/arm64 -f my-usage-server/Dockerfile .
# BuildKit fills in both arguments. A builder without it leaves them as they
# are here, which makes it build on and for its own machine, as before.
ARG BUILDPLATFORM=linux
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
# The release's version, when release-mac.sh builds it; the project's otherwise.
ARG VERSION
WORKDIR /src
COPY my-usage-win/src/AiUsage.Core/ my-usage-win/src/AiUsage.Core/
COPY my-usage-server/src/AiUsage.Server/ my-usage-server/src/AiUsage.Server/
RUN dotnet publish my-usage-server/src/AiUsage.Server -c Release ${TARGETARCH:+-a $TARGETARCH} \
    ${VERSION:+-p:Version=$VERSION} -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir /data && chown app:app /data
# Tokens, history and the API token live here; mount a volume on it.
ENV AIUSAGE_DATA=/data \
    AIUSAGE_URLS=http://0.0.0.0:8377
VOLUME /data
EXPOSE 8377
USER app
ENTRYPOINT ["dotnet", "aiusage-server.dll"]
