#!/bin/bash
set -e

dotnet tool restore
# clean is required to ensure all code style errors are (re-)raised by compiler
dotnet clean ./typebeat.Desktop.slnf --verbosity=q
dotnet build -c Debug -warnaserror ./typebeat.Desktop.slnf -p:EnforceCodeStyleInBuild=true
dotnet CodeFileSanity
dotnet jb inspectcode "typebeat.Desktop.slnf" --no-build --format=Text --stdout --caches-home="inspectcode" --verbosity=WARN
