# 1C: Log Exporter (OneSLogExporter)

Инструмент и фоновая служба Windows на платформе **.NET 10 (C# 14)** для разбора, фильтрации и непрерывного экспорта логов платформы **1С:Предприятие 8.3 / 8.2**:
* **Технологический журнал (ТЖ)**: текстовые файлы `*.log` в каталогах процессов (`rphost_*`, `rmngr_*`, `ragent_*`).
* **Журнал регистрации (ЖР)**: текстовый формат (`*.lgp`, `1Cv8.lgf`, `*.lgx`) и формат SQLite (`1Cv8.lgd`).

Проект решает две основные задачи:
1. **Оперативное расследование инцидентов (GUI)** — быстрый поиск тяжелых запросов к СУБД, взаимных блокировок (`TLOCK`, `TDEADLOCK`), долгих серверных вызовов и критических ошибок в журналах объемом в десятки и сотни гигабайт без зависания интерфейса и вылетов по памяти.
2. **Фоновый экспорт в хранилища 24/7 (Windows Service)** — отказоустойчивая доставка событий в **ClickHouse** (HTTP API / `JSONEachRow`), **Elasticsearch / OpenSearch** и локальные кольцевые **JSON-дампы** (NDJSON).

---

## Архитектура и изоляция от блокировок 1С

Главная проблема при работе со штатными журналами 1С сторонними утилитами — возникновение файловых блокировок, из-за которых рабочие процессы `rphost` не могут сделать запись в лог или аварийно завершаются. 

В OneSLogExporter сбор и отправка разделены на два независимых этапа:

```
[Журналы 1С (*.lgp / *.lgd / *.log)]
       │
       │  ЭТАП 1: Инкрементальное чтение (неблокирующий FileShare.ReadWrite / SQLite PRAGMA query_only=ON)
       ▼
[Быстрый парсинг и сохранение в локальные NDJSON файлы]
       │
       │  (Файлы 1С закрываются мгновенно, дескрипторы освобождены)
       ▼
[Локальный каталог JSON-дампов] (data_tglog_*.json / data_evlog_*.json)
       │
       │  ЭТАП 2: Сетевая транспортировка (JsonLogTransporter)
       ▼
[ClickHouse / Elasticsearch]
```

1. **Этап 1 (Сбор данных)**:
   - Воркер службы считывает только новые события, появившиеся с момента предыдущего опроса (для `.lgp` и `.log` ведется трекинг байтового смещения, для `.lgd` — максимальный `rowID` в `state.json`).
   - Распарсенные события немедленно сбрасываются в локальные JSON-файлы.
   - Дескриптор файла 1С сразу же закрывается.
2. **Этап 2 (Транспортировка)**:
   - Модуль `JsonLogTransporter` берет накопленные локальные `.json` файлы и отгружает их пачками в целевую СУБД (ClickHouse или Elastic).
   - Если сервер аналитики временно недоступен или отвечает с задержкой, это никак не влияет на 1С: логи продолжают копиться в локальных файлах дампа с настроенной ротацией по объему.

---

## Возможности GUI-клиента

* **Работа с тяжелыми файлами (100+ ГБ)**:
  - **Бинарный поиск даты ($O(\log N)$)**: при выборке за конкретный интервал времени в больших `.lgp` утилита не читает файл с первой строки, а за доли секунды находит точное байтовое смещение нужного дня.
  - **Дисковый сессионный кэш**: распарсенные строки сохраняются в локальную временную SQLite базу (`%TEMP%\OneSLogExporter\session_*.db`) в режиме WAL. Оперативная память не перегружается, интерфейс сохраняет плавность (WPF DataGrid с виртуализацией).
* **Гибкая система фильтрации**:
  - Отбор по значениям колонок в один клик: кнопки `[ ДА ]` (включить) и `[ НЕ ]` (исключить).
  - Полнотекстовый поиск с префиксами (`user:`, `event:`, `app:`, `meta:`, `comment:`) и отрицанием (`!`, `-`).
  - Интерактивный выбор периода (пресеты «Сегодня», «Вчера», «3 дня», «7 дней», «30 дней» либо произвольный интервал).
  - Сохранение и загрузка профилей фильтрации в JSON.
* **Экспорт результатов**:
  - Выгрузка отфильтрованных строк в Excel (`.xlsx`) с автоматическим форматированием ширины колонок.
  - Сохранение в формат JSON Lines (NDJSON).
  - Прямая отправка выделенных записей в ClickHouse или Elasticsearch.

---

## Структура репозитория

```text
OneSLogExporter/
├── OneSLogExporter.sln                 # Классический файл решения Visual Studio / Rider
├── OneSLogExporter.slnx                # Решение нового формата .slnx (.NET 10)
├── build.ps1                           # Скрипт сборки, тестирования и публикации
├── README.md                           # Документация
├── scripts/
│   ├── INSTALL_SERVICE.ps1             # Установка службы Windows (автоопределение путей)
│   ├── UPDATE_SERVICE.ps1              # Обновление исполняемых файлов службы
│   ├── UNINSTALL_SERVICE.ps1           # Остановка и удаление службы
│   ├── elasticsearch-template.json     # Шаблон индексов для Elasticsearch
│   └── logcfg.sample.xml               # Пример настройки logcfg.xml для техжурнала 1С
├── src/
│   ├── OneSLogExporter.Core/           # Движок парсеров (.lgp, .lgd, .log), дисковый кэш, транспортеры
│   ├── OneSLogExporter.Gui/            # Настольный клиент (WPF, XAML)
│   └── OneSLogExporter.Service/        # Фоновая служба Windows
└── tests/
    └── OneSLogExporter.Tests/          # Модульные тесты парсеров, ротации и экспорта
```

---

## Сборка проекта

Для сборки необходим **.NET 10.0 SDK** (или новее).

### Автоматическая сборка (рекомендуется)
Запустите скрипт в корне репозитория:
```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```
Скрипт выполнит:
1. Остановку запущенных процессов приложения и службы.
2. Восстановление зависимостей NuGet и компиляцию в конфигурации `Release`.
3. Запуск автоматических тестов (`dotnet test`).
4. Публикацию релизных пакетов в каталог `publish/`:
   - `publish\Gui\` — настольное приложение `OneSLogExporter.Gui.exe`
   - `publish\Service\` — служба `OneSLogExporter.Service.exe` и конфигурационные файлы.

### Ручная сборка через .NET CLI
```powershell
dotnet build OneSLogExporter.sln -c Release
dotnet test OneSLogExporter.sln -c Release --no-build
```

---

## Установка и запуск службы Windows

Служба регистрируется в системе с именем `OneSLogExporter` и отображаемым именем **«1C: Log Exporter»**.

### 1. Настройка параметров
Откройте файл `publish\Service\appsettings.json` и укажите необходимые каталоги и подключения:

```jsonc
{
  "Exporter": {
    "PollingIntervalSeconds": 60,       // Интервал опроса файлов (в секундах)
    "StateFilePath": "state.json",       // Файл сохранения позиций чтения

    // Обязательный каталог для сохранения распарсенных JSON перед отправкой (NDJSON)
    "FileDump": {
      "TechLogEnabled": true,          // Локальный дамп ТЖ
      "EventLogEnabled": true,         // Локальный дамп ЖР
      "TechLogDirectoryPath": "C:/1C_Export/json_dump/techlog",
      "EventLogDirectoryPath": "C:/1C_Export/json_dump/eventlog",
      "RetainedFileCountLimit": 30,     // Хранить до 30 файлов в каталоге
      "MaxTotalSizeMb": 500            // Не более 500 МБ на каталог
    },

    // Мониторинг Журнала Регистрации (.lgp / 1Cv8.lgf / SQLite 1Cv8.lgd)
    "EventLog": {
      "Enabled": true,
      "DirectoryPath": "C:/Logs/1C/reg_1541", // Каталог кластера (reg_1541) или логов конкретной базы
      "DatabaseName": "demo_db",             // Имя базы в кластере 1С (автопоиск GUID и словаря 1Cv8.lgf)
      "IndexId": "prod",
      "LoadArchive": false                   // false = начинать только с новых live-событий
    },

    // Мониторинг Технологического Журнала (.log)
    "TechLog": {
      "Enabled": true,
      "DirectoryPath": "C:/Logs/1C/techlog",
      "IndexId": "prod",
      "MaxAgeHours": 24
    },

    // Экспорт в ClickHouse (HTTP API / FORMAT JSONEachRow)
    "ClickHouse": {
      "TechLogEnabled": false,         // Экспорт ТЖ в ClickHouse
      "EventLogEnabled": true,         // Экспорт ЖР в ClickHouse
      "ServerUrl": "http://localhost:8123",
      "Database": "default",
      "User": "default",
      "Password": "",
      "EventLogTable": "eventlog",
      "TechLogTable": "techlog",
      "BulkBatchSize": 5000,
      "TimeoutSeconds": 60
    },

    // Экспорт в Elasticsearch / OpenSearch
    "Elastic": {
      "TechLogEnabled": false,         // Экспорт ТЖ в Elastic
      "EventLogEnabled": false,        // Экспорт ЖР в Elastic
      "ServerUrl": "http://localhost:9200",
      "Username": "",
      "Password": "",
      "BulkBatchSize": 1000
    }
  }
}
```

> **Важно по путям**: в JSON используйте прямые слеши `/` (например, `"C:/Logs/1C/reg_1541"`) либо двойные обратные слеши `\\\\`.

### 2. Регистрация службы
Запустите PowerShell от имени Администратора:
```powershell
powershell -ExecutionPolicy Bypass -File .\publish\Service\INSTALL_SERVICE.ps1
```
*(Скрипт можно запускать как из папки `publish\Service\`, так и из корня через `.\scripts\INSTALL_SERVICE.ps1` — путь к исполняемому файлу определится автоматически).*

Служба будет настроена с автоматическим запуском при старте системы и перезапуском при сбоях.

### 3. Управление службой
```powershell
# Запуск
Start-Service OneSLogExporter

# Остановка
Stop-Service OneSLogExporter

# Проверка статуса
Get-Service OneSLogExporter

# Обновление бинарных файлов (после пересборки)
powershell -ExecutionPolicy Bypass -File .\scripts\UPDATE_SERVICE.ps1

# Полное удаление службы
powershell -ExecutionPolicy Bypass -File .\scripts\UNINSTALL_SERVICE.ps1
```

Логи работы самой службы и возможные сбои записываются в каталог `logs/` рядом со службой (`logs/ones_exporter_errors_*.log`).

---

## Лицензия

Проект распространяется под лицензией [MIT](LICENSE).
