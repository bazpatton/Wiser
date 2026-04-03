"""
Persistent EasyOCR worker: loads models once, serves POST /scan (multipart file).
Run: python -m uvicorn ocr_worker:app --host 127.0.0.1 --port 8765
(cwd must be the directory containing this file and gas_receipt_ocr_lib.py)
"""
from contextlib import asynccontextmanager
import os
import tempfile
import uuid

from fastapi import FastAPI, File, HTTPException, UploadFile
from fastapi.responses import JSONResponse

import gas_receipt_ocr_lib as ocr_lib

_reader = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global _reader
    import easyocr  # type: ignore

    _reader = easyocr.Reader(["en"], gpu=False, verbose=False)
    yield
    _reader = None


app = FastAPI(title="Wiser gas receipt OCR", lifespan=lifespan)


@app.get("/health")
async def health():
    if _reader is None:
        return JSONResponse(
            status_code=503, content={"ok": False, "detail": "reader not ready"}
        )
    return {"ok": True, "detail": "ready"}


@app.post("/scan")
async def scan(file: UploadFile = File(...)):
    if _reader is None:
        raise HTTPException(status_code=503, detail="OCR reader not ready")

    suffix = os.path.splitext(file.filename or "")[1] or ".jpg"
    if suffix.lower() not in (".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"):
        suffix = ".jpg"

    data = await file.read()
    if not data:
        return JSONResponse(
            status_code=400,
            content={"error": "empty upload"},
        )

    tmp = os.path.join(
        tempfile.gettempdir(), f"wiser-ocr-{uuid.uuid4().hex}{suffix}"
    )
    try:
        with open(tmp, "wb") as f:
            f.write(data)
        result = ocr_lib.scan_with_reader(_reader, tmp)
        if "error" in result:
            return JSONResponse(status_code=422, content=result)
        return result
    finally:
        try:
            os.remove(tmp)
        except OSError:
            pass
