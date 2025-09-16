FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/Markowitz.Web/Markowitz.Web.csproj", "Markowitz.Web/"]
COPY ["src/Markowitz.Core/Markowitz.Core.csproj", "Markowitz.Core/"]
RUN dotnet restore "Markowitz.Web/Markowitz.Web.csproj"
COPY src/ .
WORKDIR "/src/Markowitz.Web"
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Markowitz.Web.dll"]
