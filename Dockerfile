# Etapa 1: compilar y publicar la API con el SDK de .NET 9
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY TacnaGo.Api/TacnaGo.Api.csproj TacnaGo.Api/
RUN dotnet restore TacnaGo.Api/TacnaGo.Api.csproj
COPY TacnaGo.Api/ TacnaGo.Api/
RUN dotnet publish TacnaGo.Api/TacnaGo.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# Etapa 2: imagen final, solo con el runtime de ASP.NET Core
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Usuario sin privilegios que ya trae la imagen oficial de .NET
USER $APP_UID
ENTRYPOINT ["dotnet", "TacnaGo.Api.dll"]
