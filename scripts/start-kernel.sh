#!/usr/bin/env bash
# Запуск DocsWalker.Kernel.exe V2 в фоне через PowerShell Start-Process.
# Параметры зашиты: --config=kernel-config.json, stderr → kernel.log,
# stdout → kernel.stdout.log, окно скрыто.
#
# Прежде чем запускать, опубликуйте kernel в Release/win-x64:
#   dotnet publish src/DocsWalker.Kernel -c Release -r win-x64
set -euo pipefail
exec powershell -NoProfile -Command "Start-Process -FilePath 'src\DocsWalker.Kernel\bin\Release\net10.0\win-x64\publish\DocsWalker.Kernel.exe' -ArgumentList '--config=kernel-config.json' -RedirectStandardError 'kernel.log' -RedirectStandardOutput 'kernel.stdout.log' -WindowStyle Hidden"
