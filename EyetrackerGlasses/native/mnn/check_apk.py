#!/usr/bin/env python3
"""Check that a packaged APK has the MNN bridge for both application ABIs."""
import sys
from zipfile import ZipFile

if len(sys.argv) != 2:
    raise SystemExit("Usage: python check_apk.py <application.apk>")

with ZipFile(sys.argv[1]) as apk:
    for abi, machine in (("arm64-v8a", 183), ("x86_64", 62)):
        name = f"lib/{abi}/libeyetracker_mnn.so"
        with apk.open(name) as lib:
            header = lib.read(20)
        if header[:6] != b"\x7fELF\x02\x01" or int.from_bytes(header[18:20], "little") != machine:
            raise SystemExit(f"Wrong ELF architecture in {name}")
        print(f"PASS: {name}")
