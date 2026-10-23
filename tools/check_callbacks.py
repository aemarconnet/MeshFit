#!/usr/bin/env python3
"""Checks that every ribbon and right-click menu command has its SOLIDWORKS callback methods.

SOLIDWORKS finds callbacks by name on the add-in object: OnMenu<Cmd> plus OnRibbonEnable<Cmd> (ribbon)
or OnMenuEnable<Cmd> (context menu). A missing method makes a button silently dead.

usage: python3 tools/check_callbacks.py      (from the repository root; exit code 1 when something is missing)
"""
import glob, re, sys

src_files = glob.glob("src/MeshFit.SolidWorks/SwAddin*.cs")
src = "".join(open(f, encoding="utf-8").read() for f in src_files)
defined = set(re.findall(r"public (?:void|int) (On\w+)\(", src))

ribbon = re.search(r"RibbonCommands =\s*\{(.*?)\n        \};", src, re.S)
ribbon_cmds = re.findall(r"\(Cmd\.(\w+),", ribbon.group(1)) if ribbon else []
menus = open("src/MeshFit.SolidWorks/SwAddin.Menus.cs", encoding="utf-8").read()
block = menus[menus.index("MenuItems ="):menus.index("};", menus.index("MenuItems ="))]
menu_cmds = re.findall(r"Cmd\.(\w+)", block)

missing = [n for c in ribbon_cmds for n in ("OnMenu" + c, "OnRibbonEnable" + c) if n not in defined]
missing += [n for c in menu_cmds for n in ("OnMenu" + c, "OnMenuEnable" + c) if n not in defined]
print(f"ribbon commands: {len(ribbon_cmds)}, menu items: {len(menu_cmds)}")
if missing:
    print("MISSING callbacks:", ", ".join(sorted(set(missing))))
    sys.exit(1)
print("all callbacks present")
