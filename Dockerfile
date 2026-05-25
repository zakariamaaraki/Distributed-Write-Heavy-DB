FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY LsmWriteDb.csproj ./
RUN dotnet restore ./LsmWriteDb.csproj

COPY . ./
RUN dotnet publish ./LsmWriteDb.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish ./
EXPOSE 8080
EXPOSE 6543

ENTRYPOINT ["dotnet", "LsmWriteDb.dll"]
