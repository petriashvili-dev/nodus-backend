# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Nodus.Api/Nodus.Api.csproj Nodus.Api/
RUN dotnet restore Nodus.Api/Nodus.Api.csproj
COPY Nodus.Api/ Nodus.Api/
RUN dotnet publish Nodus.Api/Nodus.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
# tzdata: IANA zones for recurrence; libgssapi: Npgsql probes GSSAPI. ICU is already part of the image.
USER root
RUN apt-get update && apt-get install -y --no-install-recommends tzdata curl libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s CMD curl -fsS http://localhost:8080/health/live || exit 1
# `docker run <image> migrate` applies migrations and exits (run it as a release step before starting the API).
ENTRYPOINT ["dotnet", "Nodus.Api.dll"]
