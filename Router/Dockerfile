FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Router.csproj .
RUN dotnet restore Router.csproj
COPY Program.cs ReadConsistencyPolicy.cs ./
RUN dotnet publish Router.csproj -c Release -o /app/publish --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Router.dll"]
