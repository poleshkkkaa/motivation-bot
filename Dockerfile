Використовуємо .NET SDK для побудови
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

Копіюємо csproj і відновлюємо залежності
COPY MotivationBot.csproj ./
RUN dotnet restore

Копіюємо всі файли
COPY . ./

Публікуємо реліз
RUN dotnet publish -c Release -o out

Релізний образ
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

Копіюємо з попереднього шару
COPY --from=build /app/out ./

Встановлюємо змінну середовища
ENV DOTNET_ENVIRONMENT=Production

Запускаємо бот
ENTRYPOINT ["dotnet", "MotivationBot.dll"]
