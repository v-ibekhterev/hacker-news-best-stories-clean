FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/HackerNews.Api/HackerNews.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-base
WORKDIR /app
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HackerNews.Api.dll"]

# Optional target for environments where the host, but not Docker, can restore NuGet.
FROM runtime-base AS host-published
COPY .artifacts/publish/ .

FROM runtime-base AS final
COPY --from=build /app .
