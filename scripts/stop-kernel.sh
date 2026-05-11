#!/usr/bin/env bash
# Остановка всех DocsWalker.Kernel.exe процессов. Идемпотентно (молча проходит, если их нет).
# Используется агентом перед re-publish (Kernel.exe lock'ается во время работы).
exec taskkill //F //IM DocsWalker.Kernel.exe
