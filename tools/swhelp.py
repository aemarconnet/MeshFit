#!/usr/bin/env python3
"""Prints the text of SOLIDWORKS API help pages (needs network access to help.solidworks.com).

usage: python3 tools/swhelp.py IModelDoc2~SketchAddConstraints IRefPlane~CornerPoints ISurface~PlaneParams
       python3 tools/swhelp.py --enum swSelectType_e

Member pages are named Interface~Member as in the help URLs. The script reads the page's embedded
__NEXT_DATA__ JSON and prints the parameter / remarks text.
"""
import html, json, re, sys, urllib.request

BASE = "https://help.solidworks.com/2019/English/api/{lib}/SOLIDWORKS.Interop.{lib}~SOLIDWORKS.Interop.{lib}.{name}.html"


def page(lib, name):
    url = BASE.format(lib=lib, name=name)
    raw = urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"}), timeout=30).read().decode("utf8", "replace")
    m = re.search(r'__NEXT_DATA__" type="application/json">(.*?)</script>', raw, re.S)
    text = json.loads(m.group(1))["props"]["pageProps"]["helpContentData"]["helpText"]
    text = html.unescape(re.sub(r"<[^>]+>", " ", text))
    return re.sub(r"\s+", " ", text)


def main(argv):
    if not argv:
        print(__doc__)
        return 1
    if argv[0] == "--enum":
        for name in argv[1:]:
            print("=== ", name, "\n", page("swconst", name)[:6000])
        return 0
    for name in argv:
        s = page("sldworks", name)
        i = s.find("Parameters") if "Parameters" in s else s.find("Property Value")
        j = s.find("See Also")
        print("=== ", name, "\n", s[max(i, 0):j if j > 0 else max(i, 0) + 3000][:3000])
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
