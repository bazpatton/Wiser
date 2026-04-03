import json
import re
import sys


def to_amount(value: str):
    normalized = value.replace(",", ".").strip()
    try:
        return round(float(normalized), 2)
    except ValueError:
        return None


def extract_fields(raw_text: str):
    upper_text = raw_text.upper()
    lines = [line.strip().upper() for line in raw_text.splitlines() if line.strip()]

    vol_credit = None
    amount_gbp = None
    date_ddmmyy = None

    vol_match = re.search(r"VOL\s*CREDIT\D{0,24}0*(\d{4,10})", upper_text)
    if vol_match:
        vol_credit = int(vol_match.group(1))
    else:
        # Fallback for layouts where CREDIT is on one line and value appears on the next line(s).
        for idx, line in enumerate(lines):
            if re.search(r"\bCREDIT\b", line):
                for candidate in lines[idx + 1: min(idx + 6, len(lines))]:
                    compact = candidate.replace(" ", "")
                    number_match = re.search(r"\b0*(\d{4,10})\b", compact)
                    if number_match:
                        vol_credit = int(number_match.group(1))
                        break
                if vol_credit is not None:
                    break

    amount_match = re.search(r"AMOUNT\D{0,10}(?:GBP|£)?\s*([0-9]+(?:[.,][0-9]{1,2})?)", upper_text)
    if amount_match:
        amount_gbp = to_amount(amount_match.group(1))
    else:
        # Fallback for OCR text like "AP 75,0" or "GBP 75.00" in separate lines.
        for line in lines:
            line_match = re.search(r"\b(?:AP|GBP|AMOUNT|£)\b\D{0,8}([0-9]{1,4}(?:[.,][0-9]{1,2})?)", line)
            if line_match:
                amount_gbp = to_amount(line_match.group(1))
                if amount_gbp is not None:
                    break

    if amount_gbp is None:
        generic_money = re.search(r"\b([0-9]{1,4}(?:[.,][0-9]{1,2}))\b", upper_text)
        if generic_money:
            amount_gbp = to_amount(generic_money.group(1))

    date_match = re.search(r"\b([0-3]\d/[0-1]\d/\d{2})\b", upper_text)
    if date_match:
        date_ddmmyy = date_match.group(1)

    return vol_credit, amount_gbp, date_ddmmyy


def main():
    if len(sys.argv) != 2:
        print(json.dumps({"error": "Usage: gas_receipt_ocr.py <image_path>"}))
        return 2

    image_path = sys.argv[1]
    try:
        import easyocr  # type: ignore
        import numpy as np  # type: ignore
        from PIL import Image, ImageOps  # type: ignore
    except Exception as ex:
        print(json.dumps({"error": f"easyocr import failed: {ex}"}))
        return 3

    try:
        reader = easyocr.Reader(["en"], gpu=False, verbose=False)
        # Load with Pillow first (handles more JPG variants + EXIF orientation),
        # then pass an RGB numpy array to EasyOCR.
        image = Image.open(image_path)
        image = ImageOps.exif_transpose(image).convert("RGB")
        image_np = np.array(image)
        if image_np.size == 0:
            raise RuntimeError("decoded image is empty")
        lines = reader.readtext(image_np, detail=1, paragraph=False)
    except Exception as ex:
        print(json.dumps({"error": f"easyocr read failed: {ex}"}))
        return 4

    texts = []
    confidence_values = []
    for row in lines:
        if len(row) >= 3:
            texts.append(str(row[1]))
            try:
                confidence_values.append(float(row[2]))
            except Exception:
                pass
        elif len(row) >= 2:
            texts.append(str(row[1]))

    raw_text = "\n".join(texts)
    vol_credit, amount_gbp, date_ddmmyy = extract_fields(raw_text)
    confidence = sum(confidence_values) / len(confidence_values) if confidence_values else None

    print(
        json.dumps(
            {
                "vol_credit": vol_credit,
                "amount_gbp": amount_gbp,
                "date_ddmmyy": date_ddmmyy,
                "confidence": confidence,
                "raw_text": raw_text,
            }
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
