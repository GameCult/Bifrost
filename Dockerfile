FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Bifrost.Web/Bifrost.Web.csproj src/Bifrost.Web/
RUN dotnet restore src/Bifrost.Web/Bifrost.Web.csproj

COPY . .
RUN dotnet publish src/Bifrost.Web/Bifrost.Web.csproj \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:5080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 5080
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Bifrost.Web.dll"]
