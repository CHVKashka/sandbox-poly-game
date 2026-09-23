# 02. Проект: структура, C#, запуск

## Быстрый старт: .bat-скрипты

В корне репозитория — готовые скрипты (двойной клик или из `cmd`/PowerShell), оборачивающие команды из этого
документа. Все сами вызывают `dotnet build` перед запуском/тестами, чтобы не ловить `Cannot instantiate C# script`
после чистки `.godot`/`obj`/`bin` (см. «Заметки по окружению» ниже). Движок по умолчанию ищут в
`D:\Programs\Godot-4.6.3-double` — другой путь задаётся переменной окружения `GODOT_SRC` перед запуском
(`set GODOT_SRC=D:\Godot\godot-src && run.bat`).

| Скрипт | Действие |
|---|---|
| `build.bat` | Только сборка C# (`dotnet build`) |
| `run.bat` | Сборка + запуск редактора построек (главная сцена), как обычная игра |
| `edit.bat` | Сборка + открыть проект в GUI-редакторе Godot (`-e`) |
| `test.bat` | Сборка + самотесты headless (см. [04-testing.md](04-testing.md)); код выхода 0 = всё прошло |
| `export.bat [аргументы]` | Обёртка над `Tools\export-windows.ps1` — собирает отдельный `.exe` игры (см. ниже) |

## Структура репозитория

```
sandbox-poly-game/
├─ project.godot            настройки проекта (главная сцена, окно 1600x900, MSAA 2x, метка Double Precision)
├─ sandbox-poly-game.csproj / .sln   C#-проект (Godot.NET.Sdk 4.6.3, net8.0, GodotFloat64=true)
├─ nuget.config             привязка пакетов Godot* к локальному источнику GodotDouble (см. ниже)
├─ build.bat / run.bat / edit.bat / test.bat / export.bat   быстрый старт (см. выше)
├─ blocks/                  data-driven описания блоков, по одному XML-файлу на блок (см. 03)
├─ Scenes/
│  └─ BuildEditor.tscn      главная сцена: один узел со скриптом BuildEditor (всё остальное строится кодом)
├─ Scripts/
│  ├─ Blocks/                компоненты блоков (BaseComponent, BuildingBlock) и загрузчик blocks/*.xml (BlockCatalog)
│  ├─ Core/                 логика без сцены: сетка клеток, чанки, меширование, каркас, рейкаст, постройка (Construction)
│  ├─ Editor/               узлы и UI редактора: BuildEditor, FlyCamera, VoxelWorld, EditorState, Ui/*
│  └─ Dev/                  инструменты разработчика: самотесты, скриншот-харнесс, демо-постройки
├─ Shaders/build_grid.gdshader   шейдер сетки на земле
├─ Tools/export-windows.ps1 экспорт игры в отдельный exe (см. ниже)
├─ Docs/                    эта документация
└─ .gitignore, .gitattributes
```

Файлы `*.cs.uid` и `*.gdshader.uid` создаёт редактор Godot — их **нужно коммитить** (стабильные ссылки на ресурсы).
Папка `.godot/` (кэш импорта, сборка C#) в git не попадает и пересоздаётся на каждом устройстве.

## C#-проект

`sandbox-poly-game.csproj`:
- `Sdk="Godot.NET.Sdk/4.6.3"` — версия должна совпадать с версией движка.
- `AssemblyName = sandbox-poly-game` — совпадает с `[dotnet] project/assembly_name` в `project.godot`.
- `GodotFloat64 = true` — определяет константу `GODOT_REAL_T_IS_DOUBLE`; редактор Godot передаёт это сам, свойство
  в csproj нужно, чтобы **сборка из командной строки** (`dotnet build`) была согласована с double-движком.
- `Nullable = enable`.

Вывод сборки: `.godot/mono/temp/bin/Debug/sandbox-poly-game.dll` — оттуда его подхватывает движок.

### NuGet и «двойные» пакеты (важно)

`GodotSharp`, `Godot.SourceGenerators`, `Godot.NET.Sdk` версии `4.6.3` есть на nuget.org, но **официальные — float**.
Проект обязан компилироваться против **своих** пакетов из `<GODOT_SRC>\bin\GodotSharp\Tools\nupkgs`
(собираются `build_assemblies.py --precision=double`, см. [01](01-engine-build.md)). Для этого:

1. На устройстве один раз: `dotnet nuget add source <GODOT_SRC>\bin\GodotSharp\Tools\nupkgs --name GodotDouble`.
2. `nuget.config` в корне репозитория (`packageSourceMapping`) заставляет брать `Godot*` только из `GodotDouble`.

Проверка (на этой машине выполнена): SHA256 `%USERPROFILE%\.nuget\packages\godotsharp\4.6.3\lib\net8.0\GodotSharp.dll`
= SHA256 `<GODOT_SRC>\bin\GodotSharp\Api\Release\GodotSharp.dll`.
Если после пересборки движка проект ведёт себя странно — удалить `%USERPROFILE%\.nuget\packages\godot*` и пересобрать.

## Сборка и запуск

Проще всего — `build.bat`/`run.bat`/`edit.bat`/`test.bat` из корня репозитория (см. «Быстрый старт» выше). Ниже —
то же самое вручную, из корня репозитория (PowerShell). `<EDITOR>` = `<GODOT_SRC>\bin\godot.windows.editor.double.x86_64.mono.exe`
(`.console.exe` — то же самое, но печатает в консоль).

```powershell
# сборка C# любым из способов
dotnet build sandbox-poly-game.csproj                              # напрямую
<EDITOR> --headless --path . --editor --build-solutions --quit   # тем же путём, что кнопка Build в редакторе

# запуск игры/редактора построек (главная сцена)
<EDITOR> --path .

# открыть проект в редакторе Godot
<EDITOR> --path . -e
```

В самом редакторе Godot: F5 — запуск, Alt+B — сборка C#.

## Отдельный exe игры (экспорт)

Готовая сборка, которую можно открыть двойным кликом (без редактора Godot и без установленного .NET):

```powershell
export.bat                       # release (обёртка над Tools\export-windows.ps1, см. «Быстрый старт» выше)
export.bat -Config debug
export.bat -GodotSrc D:\Godot\godot-src   # если движок не в D:\Programs\Godot-4.6.3-double (или задать переменную GODOT_SRC)

# то же самое напрямую, без обёртки:
powershell -ExecutionPolicy Bypass -File Tools\export-windows.ps1              # release
powershell -ExecutionPolicy Bypass -File Tools\export-windows.ps1 -Config debug
powershell -ExecutionPolicy Bypass -File Tools\export-windows.ps1 -GodotSrc D:\Godot\godot-src
```

Результат — `Builds\Windows\`:
- `sandbox-poly-game.exe` — игра (это **ваш double-шаблон** `template_release`/`template_debug`, а не официальный);
- `sandbox-poly-game.pck` — ресурсы; `data_sandbox-poly-game_windows_x86_64\` — .NET-рантайм и сборки игры.

Все три части переносятся только вместе. Первый экспорт долгий (публикация .NET, скачивание runtime-пакетов с nuget.org), повторные — быстрее.

Как это устроено: скрипт сам генерирует `export_presets.cfg` (в нём пути к шаблонам конкретной машины, поэтому файл и папка
`Builds/` в git не попадают) и вызывает `<EDITOR> --headless --export-release "Windows Desktop" ...`. Кастомные шаблоны обязательны:
официальные шаблоны Godot — float и с этой сборкой несовместимы. Скрипт написан в ASCII: Windows PowerShell 5.1 читает `.ps1` без BOM
в ANSI и ломает кириллицу; `export_presets.cfg` пишется **без BOM** — с BOM Godot не находит пресет.

Проверено: в экспортированной игре проходят все самотесты
(`Builds\Windows\sandbox-poly-game.exe --headless -- --selftest`, код выхода 0) и работает скриншот-харнесс.

## Заметки по окружению (Windows)

- Из PowerShell запускать надёжнее, чем из Git Bash (при диагностике GUI-режимы из Git Bash падали).
- Проект нужно открывать **только double-редактором**: официальный уберёт метку `Double Precision` и соберёт C# против float.
- MSBuild на русской локали печатает сообщения по-русски — это нормально, ключевые слова для поиска ошибок: `error`, `Ошибок:`.
- Если перед коммитом (или вручную) удалить `.godot/`, `obj/`, `bin/` — это ожидаемо (они в `.gitignore`), но
  **редактор не соберёт C# сам при обычном запуске** (`<EDITOR> --path .`): он попытается сразу загрузить
  `BuildEditor.cs` как класс и упадёт с ошибкой `Cannot instantiate C# script ... class definition could not be
  found`. Перед первым запуском после такой чистки — `dotnet build sandbox-poly-game.csproj` (или
  `<EDITOR> --headless --path . --editor --build-solutions --quit`), см. «Сборка и запуск» выше.
  `run.bat`/`edit.bat`/`test.bat` делают это автоматически перед каждым запуском — с ними эта ловушка не грозит.
