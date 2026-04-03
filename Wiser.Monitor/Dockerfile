FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src
COPY Wiser.Monitor.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv python3-pip libgl1 libglib2.0-0 \
    && python3 -m venv /opt/ocr-venv \
    && /opt/ocr-venv/bin/pip install --no-cache-dir --upgrade pip setuptools wheel \
    && (/opt/ocr-venv/bin/pip install --no-cache-dir --extra-index-url https://download.pytorch.org/whl/cpu torch==2.5.1+cpu torchvision==0.20.1+cpu \
        || /opt/ocr-venv/bin/pip install --no-cache-dir torch==2.5.1 torchvision==0.20.1) \
    && /opt/ocr-venv/bin/pip install --no-cache-dir easyocr==1.7.2 \
    && rm -rf /var/lib/apt/lists/*
ENV DATA_DIR=/data
ENV ASPNETCORE_URLS=http://+:8080
ENV OCR_PYTHON_PATH=/opt/ocr-venv/bin/python
ENV EASYOCR_MODULE_PATH=/data/easyocr
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Wiser.Monitor.dll"]
