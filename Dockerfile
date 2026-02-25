# Multi-stage build for LLimit LLM cost gateway.
# Build stage uses .NET SDK, runtime uses minimal ASP.NET image.
# SQLite database is stored in /data (mount a volume in production).
# See: https://learn.microsoft.com/en-us/dotnet/core/docker/build-container
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/LLimit/LLimit.csproj ./
RUN dotnet restore
COPY src/LLimit/ ./
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV LLIMIT_DB_PATH=/data/llimit.db

EXPOSE 8080
VOLUME /data

ENTRYPOINT ["dotnet", "LLimit.dll"]
