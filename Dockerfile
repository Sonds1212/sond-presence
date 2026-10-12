# Imagem do servi?o do Sond Client (Render, Railway, qualquer host com Docker).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY SondPresence.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# A hospedagem passa a porta em $PORT; o Program.cs escuta nela.
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "SondPresence.dll"]
