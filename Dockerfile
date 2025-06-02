# Базовий образ .NET для запуску
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Базовий образ SDK для збірки
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копіюємо всі файли в контейнер
COPY . .

# Публікуємо проєкт
RUN dotnet publish MotivationBot.csproj -c Release -o /app/publish

# Остаточний образ
FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MotivationBot.dll"]
