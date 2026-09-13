FROM node:26-bookworm-slim AS opencode
ARG OPENCODE_VERSION=1.18.30
RUN npm install --global opencode-ai@${OPENCODE_VERSION}

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/HVO.AgentControl/HVO.AgentControl.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends python3 tmux tini ca-certificates git \
    && rm -rf /var/lib/apt/lists/*
COPY --from=opencode /usr/local/bin/node /usr/local/bin/node
COPY --from=opencode /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/opencode-ai/bin/opencode.exe /usr/local/bin/opencode \
    && mkdir -p /data && chown 1000:1000 /data
WORKDIR /app
COPY --from=build /app/ ./
USER 1000:1000
ENV ASPNETCORE_HTTP_PORTS=8080 HOME=/data/home LANG=C.UTF-8 TERM=xterm-256color
EXPOSE 8080
ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "HVO.AgentControl.dll"]
