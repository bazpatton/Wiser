import json
import sys

import gas_receipt_ocr_lib as ocr_lib


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
        reader = easyocr.Reader(["en"], gpu=False, verbose=False)
    except Exception as ex:
        print(json.dumps({"error": f"easyocr reader init failed: {ex}"}))
        return 3

    result = ocr_lib.scan_with_reader(reader, image_path)
    print(ocr_lib.scan_result_to_json(result))
    if "error" in result:
        return 4
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
