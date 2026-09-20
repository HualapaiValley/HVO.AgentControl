#!/usr/bin/python3
import os
import sys
import time

try:
    with open(".fake-dotnet-mode", encoding="ascii") as stream:
        mode = stream.read().strip()
except OSError:
    mode = "pass"
if mode == "fail":
    print("fail")
    sys.exit(3)
if mode == "timeout":
    time.sleep(5)
if mode == "large":
    print("x" * 70000)
else:
    print("pass")
