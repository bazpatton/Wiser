FROM python:3.12-slim-bookworm

WORKDIR /app
ENV PYTHONUNBUFFERED=1
ENV DATA_DIR=/data

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY wiser_monitor ./wiser_monitor
COPY static ./static

EXPOSE 8080
CMD ["uvicorn", "wiser_monitor.main:app", "--host", "0.0.0.0", "--port", "8080"]
