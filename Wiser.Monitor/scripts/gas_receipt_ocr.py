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

    vol_credit = None
    amount_gbp = None
    date_ddmmyy = None

    vol_match = re.search(r"VOL\s*CREDIT\D{0,12}(\d{1,9})", upper_text)
    if vol_match:
        vol_credit = int(vol_match.group(1))

    amount_match = re.search(r"AMOUNT\D{0,10}(?:GBP|£)?\s*([0-9]+(?:[.,][0-9]{1,2})?)", upper_text)
    if amount_match:
        amount_gbp = to_amount(amount_match.group(1))

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
    except Exception as ex:
        print(json.dumps({"error": f"easyocr import failed: {ex}"}))
        return 3

    try:
        reader = easyocr.Reader(["en"], gpu=False)
        lines = reader.readtext(image_path, detail=1, paragraph=False)
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
