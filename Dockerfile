# syntax=docker/dockerfile:1.7
#
# Email Worker — Worker Service, no HTTP. Uses dotnet/runtime:8.0 (slimmer
# than aspnet:8.0 since we don't need Kestrel or the web pipeline).

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Shared/Shared.csproj                  src/Shared/
COPY src/Email.Worker/Email.Worker.csproj      src/Email.Worker/
RUN dotnet restore src/Email.Worker/Email.Worker.csproj

COPY src/Shared/         src/Shared/
COPY src/Email.Worker/   src/Email.Worker/

RUN dotnet publish src/Email.Worker/Email.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID

# No EXPOSE — worker is outbound-only (SQS consumer, SES sender).
# No curl needed — no healthcheck endpoint to hit. docker-compose uses
# service_started for dependencies that depend on the worker.

ENTRYPOINT ["dotnet", "EmailPlatform.Email.Worker.dll"]
