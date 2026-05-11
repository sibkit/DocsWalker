#!/usr/bin/env bash
# Запуск DocsWalker.Kernel.exe в фоне через PowerShell Start-Process.
# Используется и человеком, и автономным агентом (allow-rule в .claude/settings.local.json).
# Параметры зашиты: --config=kernel-config.json, RedirectStandardError=kernel.log, скрытое окно.
set -euo pipefail
exec powershell -NoProfile -Command "Start-Process -FilePath 'src\DocsWalker.Kernel\bin\Release\net10.0\win-x64\publish\DocsWalker.Kernel.exe' -ArgumentList '--config=kernel-config.json' -RedirectStandardError 'kernel.log' -RedirectStandardOutput 'kernel.stdout.log' -WindowStyle Hidden"
