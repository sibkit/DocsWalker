# stg-0001 — sequence-counter

## Цель
Реализовать sequence-счётчик id в `docs/.docswalker/sequence.txt`: чтение, инкремент, запись. Интегрировать с механизмом атомарной записи из `write-api-basics`.

## Файлы
`src/DocsWalker.Core/Store/SequenceCounter.cs` — чтение/инкремент/запись; интеграция с атомарной записью

## Действия
1. При первой инициализации (файла нет) создать `sequence.txt` со значением `0`.
2. Метод `Next()`: прочитать текущее значение, увеличить на 1, записать обратно — атомарно (см. `write-api-basics`/`AtomicWriter`).
3. Использовать `Next()` исключительно из write-операций (`create-node`).
4. Запретить ручную правку (правило в Схеме). Программно DocsWalker монотонность не нарушает.

## Риски
- Гонка при параллельных запусках DocsWalker — операция должна быть file-lock-safe (FileShare.None при чтении-записи или OS-level lock через `FileStream` + monitor).
