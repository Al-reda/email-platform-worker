# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY shared/Shared.csproj                  shared/
COPY worker/Email.Worker.csproj            worker/
RUN dotnet restore worker/Email.Worker.csproj

COPY shared/          shared/
COPY worker/          worker/

RUN dotnet publish worker/Email.Worker.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Email.Worker.dll"]
