"""Read-only XLSX structure report. No record titles, names, or Drive IDs are emitted."""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import posixpath
import re
import xml.etree.ElementTree as ET
import zipfile

NS = {
    "s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
    "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
}
DEFAULT_SOURCE = Path(__file__).resolve().parents[1] / "data/reference/catalog.xlsx"
PENDING_ROW_COLOR = "FFFFC000"
OTHER_NAME_HIGHLIGHT = "FFFABF8F"


def inspect(source):
    raw = source.read_bytes()
    result = {"source_sha256": hashlib.sha256(raw).hexdigest(), "source_bytes": len(raw), "sheets": []}
    with zipfile.ZipFile(source) as archive:
        shared = []
        if "xl/sharedStrings.xml" in archive.namelist():
            shared = [
                "".join(node.text or "" for node in si.findall(".//s:t", NS))
                for si in ET.fromstring(archive.read("xl/sharedStrings.xml"))
            ]

        def value(cell):
            if cell is None:
                return None
            if cell.get("t") == "inlineStr":
                return "".join(t.text or "" for t in cell.findall(".//s:t", NS))
            stored = cell.find("s:v", NS)
            if stored is None or stored.text is None:
                return None
            return shared[int(stored.text)] if cell.get("t") == "s" else stored.text

        styles = ET.fromstring(archive.read("xl/styles.xml"))
        fill_colors = []
        for fill in styles.find("s:fills", NS):
            pattern = fill.find("s:patternFill", NS)
            color = pattern.find("s:fgColor", NS) if pattern is not None else None
            fill_colors.append(
                color.get("rgb")
                if color is not None and pattern.get("patternType") == "solid"
                else None
            )
        style_fills = [int(style.get("fillId", "0")) for style in styles.find("s:cellXfs", NS)]

        def fill_color(cell):
            return None if cell is None else fill_colors[style_fills[int(cell.get("s", "0"))]]

        relationships = {
            rel.get("Id"): rel.get("Target")
            for rel in ET.fromstring(archive.read("xl/_rels/workbook.xml.rels"))
        }
        book = ET.fromstring(archive.read("xl/workbook.xml"))
        for sheet in book.find("s:sheets", NS):
            target = relationships[sheet.get("{" + NS["r"] + "}id")]
            path = target.lstrip("/") if target.startswith("/") else posixpath.normpath("xl/" + target)
            root = ET.fromstring(archive.read(path))
            rows, codes, record_values = [], [], []
            pending_count = other_name_highlights = 0
            for row in root.findall("s:sheetData/s:row", NS):
                cells = {re.sub(r"\d", "", c.get("r")): c for c in row}
                values = {col: value(cell) for col, cell in cells.items()}
                rows.append(values)
                parts = [values.get(col) for col in "ABCDEF"]
                if not all(part is not None and re.fullmatch(r"\d", part) for part in parts):
                    continue
                codes.append("".join(parts))
                record_values.append(values)
                pending_count += all(
                    fill_color(cells.get(col)) == PENDING_ROW_COLOR
                    for col in "ABCDEFGHIJKLMNOPQ"
                )
                other_name_highlights += fill_color(cells.get("G")) == OTHER_NAME_HIGHLIGHT
            counts = Counter(codes)
            dimension = root.find("s:dimension", NS)
            summary = {
                "sheet": sheet.get("name"),
                "dimension": dimension.get("ref") if dimension is not None else None,
                "nonempty_rows": sum(any(v is not None for v in r.values()) for r in rows),
                "six_digit_record_rows": len(codes),
                "unique_codes": len(counts),
                "duplicate_code_groups": sum(count > 1 for count in counts.values()),
                "category_counts": dict(sorted(Counter(code[:2] for code in codes).items())),
                "hyperlink_count": len(root.findall("s:hyperlinks/s:hyperlink", NS)),
                "formula_count": len(root.findall(".//s:f", NS)),
            }
            if codes:
                # This source has its headers in the first row. Record values are never emitted.
                summary.update({
                    "headers": {col: text for col, text in rows[0].items() if text is not None},
                    "electronic_form_counts": dict(Counter(r.get("K") or "blank" for r in record_values)),
                    "populated_columns": {col: sum(bool(r.get(col)) for r in record_values) for col in "LMNPQ"},
                    "pending_documents": pending_count,
                    "pending_rule": "Whole record A:Q uses solid FFFFC000; confirmed by user.",
                    "other_name_only_orange_highlights": other_name_highlights,
                    "conditional_format_range_count": len(root.findall("s:conditionalFormatting", NS)),
                })
            result["sheets"].append(summary)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", nargs="?", type=Path, default=DEFAULT_SOURCE)
    args = parser.parse_args()
    print(json.dumps(inspect(args.source), ensure_ascii=False, indent=2))
