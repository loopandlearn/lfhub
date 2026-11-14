# For hosts that build from source (CapRover's captain-definition, plain `docker build`).
# The published images are built by the .NET SDK instead; see the workflow.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props lfhub.csproj ./
RUN dotnet restore lfhub.csproj
COPY . .
RUN dotnet publish lfhub.csproj -c Release --no-restore --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "lfhub.dll"]
