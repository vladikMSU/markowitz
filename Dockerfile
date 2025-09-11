# Базовый рантайм (тонкий)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
# Kestrel слушает внутри контейнера на 8080
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Сборка/публикация
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Markowitz.Web/Markowitz.Web.csproj", "Markowitz.Web/"]
RUN dotnet restore "Markowitz.Web/Markowitz.Web.csproj"
COPY . .
WORKDIR "/src/Markowitz.Web"
# UseAppHost=false — чтобы не тянуть нативный exe, образ будет меньше
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Финальный образ рантайма
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Markowitz.Web.dll"]
