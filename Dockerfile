# Вказуємо базовий образ із .NET SDK
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Додатково встановлюємо бібліотеку curl (опціонально)
RUN apt-get update && apt-get install -y curl

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копіюємо .csproj і відновлюємо залежності
COPY ["MotivationBot.csproj", "./"]
RUN dotnet restore "MotivationBot.csproj"

# Копіюємо всі інші файли
COPY . .

# Публікуємо з релізною конфігурацією
RUN dotnet publish "MotivationBot.csproj" -c Release -o /app/publish

# Final stage
FROM base AS final
WORKDIR /app

# Копіюємо результати публікації
COPY --from=build /app/publish .

# Встановлюємо змінні середовища (опціонально, токен краще передавати через середовище на хостингу)
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Вказуємо команду запуску
ENTRYPOINT ["dotnet", "MotivationBot.dll"]
