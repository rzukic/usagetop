# Build from the repository root, since the server shares the Windows app's core:
#   docker build -f my-usage-server/Dockerfile -t aiusage-server .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY my-usage-win/src/AiUsage.Core/ my-usage-win/src/AiUsage.Core/
COPY my-usage-server/src/AiUsage.Server/ my-usage-server/src/AiUsage.Server/
RUN dotnet publish my-usage-server/src/AiUsage.Server -c Release -o /app

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
