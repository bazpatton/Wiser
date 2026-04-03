FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src
COPY Wiser.Monitor.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-pip libgl1 libglib2.0-0 \
    && pip3 install --no-cache-dir easyocr \
    && rm -rf /var/lib/apt/lists/*
ENV DATA_DIR=/data
ENV ASPNETCORE_URLS=http://+:8080
ENV OCR_PYTHON_PATH=python3
ENV EASYOCR_MODULE_PATH=/data/easyocr
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Wiser.Monitor.dll"]
