FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Lola.Api/Lola.Api.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN useradd -u 10001 -m lola && mkdir -p /app/data && chown lola:lola /app/data
USER lola
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8200
EXPOSE 8200
ENTRYPOINT ["dotnet", "Lola.Api.dll"]
