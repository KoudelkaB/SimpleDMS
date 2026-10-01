#!/usr/bin/env python3
import sys
import xml.etree.ElementTree as ET

path, version, date = sys.argv[1:]
tree = ET.parse(path)
root = tree.getroot()
releases = root.find("releases")
if releases is None:
    releases = ET.SubElement(root, "releases")
if not any(r.get("version") == version for r in releases):
    releases.insert(0, ET.Element("release", version=version, date=date))
ET.indent(tree, space="  ")
tree.write(path, encoding="UTF-8", xml_declaration=True)
