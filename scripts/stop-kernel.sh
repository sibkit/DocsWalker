#!/usr/bin/env bash
# Остановка всех DocsWalker.Kernel.exe процессов. Идемпотентно
# (молча проходит, если их нет). Альтернатива: послать
# `POST /<graph>` с `{"jsonrpc":"2.0","id":1,"method":"shutdown"}`.
exec taskkill //F //IM DocsWalker.Kernel.exe
