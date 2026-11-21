FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source
COPY global.json ./
COPY src/Tbox.Core/ src/Tbox.Core/
COPY src/Tbox.Cloud/ src/Tbox.Cloud/
RUN dotnet publish src/Tbox.Cloud/Tbox.Cloud.csproj -c Release -o /out /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir /cloud-keys && chown app:app /cloud-keys
USER app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080 Cloud__KeyDirectory=/cloud-keys
ENTRYPOINT ["dotnet", "Tbox.Cloud.dll"]
