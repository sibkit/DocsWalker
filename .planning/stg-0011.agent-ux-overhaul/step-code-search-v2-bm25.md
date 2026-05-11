# stg-0011 — code-search-v2-bm25

## Цель

Заменить текущий substring-`search` на расширенный с BM25-ранжированием и фильтрами. Новые параметры: `query` (как был), `in` (`title`/`text`/`both` — default `both`), `type` (фильтр по типу узла), `tree` + `under` (искать в поддереве указанного узла в указанном дереве), `regex` (булев флаг — режим регулярного выражения вместо BM25), `limit` (default 20), `compact` (alias для `fields=id,type,title,score,snippet`). Поля результата: `id`, `type`, `title`, `score`, `snippet`. Boost по title-hit ×3.

## Файлы

Через `mcp__glider__find_code` определить точно:
- Kernel: `SearchHandler.cs`.
- Новые: `Bm25Scorer.cs`, `SnippetExtractor.cs`.

## Действия

1. Реализовать BM25-индексирование (term frequency, inverse document frequency, document length normalization).
2. Реализовать boost: term match в `title` × 3 к BM25-score.
3. Реализовать фильтры — `type`, `tree+under` (через индекс tree-scope потомков).
4. Реализовать `regex`-режим (без ранжирования, в порядке появления, тот же `limit`).
5. Реализовать `SnippetExtractor` — окно ±40 символов вокруг матча в `text`.
6. Реализовать `compact` — урезание полей.
7. Unit-тесты на ранжирование, фильтры, snippet.

## Риски

BM25 на 337 узлах — overengineering, но архитектурно корректно. На малых корпусах разница с упрощённым scoring неотличима, перформанс не страдает.
