FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props HVO.AgentControl.slnx ./
COPY src/HVO.AgentControl/HVO.AgentControl.csproj src/HVO.AgentControl/
COPY tests/HVO.AgentControl.Tests/HVO.AgentControl.Tests.csproj tests/HVO.AgentControl.Tests/
RUN dotnet restore HVO.AgentControl.slnx
COPY src/ src/
RUN dotnet publish src/HVO.AgentControl --configuration Release --no-restore --output /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11
ARG CONTROL_UID=1000
ARG CONTROL_GID=1000
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /data /run/secrets && chown -R ${CONTROL_UID}:${CONTROL_GID} /data /app && chmod 700 /data
USER ${CONTROL_UID}:${CONTROL_GID}
ENV ASPNETCORE_URLS=http://+:8080 Control__DataDirectory=/data Control__SecretsDirectory=/run/secrets
EXPOSE 8080
ENTRYPOINT ["dotnet", "HVO.AgentControl.dll"]
