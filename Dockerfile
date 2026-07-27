FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore layers first so dependency changes alone invalidate the cache.
COPY Directory.Packages.props ./
COPY src/Sological.Sms.Core/Sological.Sms.Core.csproj src/Sological.Sms.Core/
COPY src/Sological.Sms.Service/Sological.Sms.Service.csproj src/Sological.Sms.Service/
RUN dotnet restore src/Sological.Sms.Service/Sological.Sms.Service.csproj

COPY src/ src/
RUN dotnet publish src/Sological.Sms.Service/Sological.Sms.Service.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sological.Sms.Service.dll"]
