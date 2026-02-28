FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5000

# Создаём директорию для данных
RUN mkdir -p /app/data

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["SFDRN.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Утилиты для диагностики и бэкапов
RUN apt-get update && apt-get install -y \
    curl \
    jq \
    dnsutils \
    sqlite3 \
    && rm -rf /var/lib/apt/lists/*

COPY docker-entrypoint.sh /
RUN sed -i 's/\r$//' /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# Volume для SQLite базы
VOLUME ["/app/data"]

ENTRYPOINT ["/docker-entrypoint.sh"]