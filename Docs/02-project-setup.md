# 02. Проект: структура, C#, запуск

## Структура репозитория

```
SW_V2/
├─ project.godot            настройки проекта (главная сцена, окно 1600x900, MSAA 2x, метка Double Precision)
├─ SW_V2.csproj / .sln      C#-проект (Godot.NET.Sdk 4.7.2, net8.0, GodotFloat64=true)
├─ nuget.config             привязка пакетов Godot* к локальному источнику GodotDouble (см. ниже)
├─ Scenes/
│  └─ BuildEditor.tscn      главная сцена: один узел со скриптом BuildEditor (всё остальное строится кодом)
├─ Scripts/
│  ├─ Core/                 логика без сцены: сетка блоков, чанки, меширование, каркас, рейкаст, реестр блоков
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

`SW_V2.csproj`:
- `Sdk="Godot.NET.Sdk/4.7.2"` — версия должна совпадать с версией движка.
- `AssemblyName = SW_V2` — совпадает с `[dotnet] project/assembly_name` в `project.godot`.
- `GodotFloat64 = true` — определяет константу `GODOT_REAL_T_IS_DOUBLE`; редактор Godot передаёт это сам, свойство
  в csproj нужно, чтобы **сборка из командной строки** (`dotnet build`) была согласована с double-движком.
- `Nullable = enable`.

Вывод сборки: `.godot/mono/temp/bin/Debug/SW_V2.dll` — оттуда его подхватывает движок.

### NuGet и «двойные» пакеты (важно)

`GodotSharp`, `Godot.SourceGenerators`, `Godot.NET.Sdk` версии `4.7.2` есть на nuget.org, но **официальные — float**.
Проект обязан компилироваться против **своих** пакетов из `<GODOT_SRC>\bin\GodotSharp\Tools\nupkgs`
(собираются `build_assemblies.py --precision=double`, см. [01](01-engine-build.md)). Для этого:

1. На устройстве один раз: `dotnet nuget add source <GODOT_SRC>\bin\GodotSharp\Tools\nupkgs --name GodotDouble`.
2. `nuget.config` в корне репозитория (`packageSourceMapping`) заставляет брать `Godot*` только из `GodotDouble`.

Проверка (на этой машине выполнена): SHA256 `%USERPROFILE%\.nuget\packages\godotsharp\4.7.2\lib\net8.0\GodotSharp.dll`
= SHA256 `<GODOT_SRC>\bin\GodotSharp\Api\Release\GodotSharp.dll`.
Если после пересборки движка проект ведёт себя странно — удалить `%USERPROFILE%\.nuget\packages\godot*` и пересобрать.

## Сборка и запуск

Из корня репозитория (PowerShell). `<EDITOR>` = `<GODOT_SRC>\bin\godot.windows.editor.double.x86_64.mono.exe`
(`.console.exe` — то же самое, но печатает в консоль).

```powershell
# сборка C# любым из способов
dotnet build SW_V2.csproj                              # напрямую
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
powershell -ExecutionPolicy Bypass -File Tools\export-windows.ps1              # release
powershell -ExecutionPolicy Bypass -File Tools\export-windows.ps1 -Config debug
powershell -ExecutionPolicy Bypass -File Tools\export-windows.ps1 -GodotSrc D:\Godot\godot-src   # если движок не в C:\Godot\godot-src (или задать переменную GODOT_SRC)
```

Результат — `Builds\Windows\`:
- `SW_V2.exe` — игра (это **ваш double-шаблон** `template_release`/`template_debug`, а не официальный);
- `SW_V2.pck` — ресурсы; `data_SW_V2_windows_x86_64\` — .NET-рантайм и сборки игры.

Все три части переносятся только вместе. Первый экспорт долгий (публикация .NET, скачивание runtime-пакетов с nuget.org), повторные — быстрее.

Как это устроено: скрипт сам генерирует `export_presets.cfg` (в нём пути к шаблонам конкретной машины, поэтому файл и папка
`Builds/` в git не попадают) и вызывает `<EDITOR> --headless --export-release "Windows Desktop" ...`. Кастомные шаблоны обязательны:
официальные шаблоны Godot — float и с этой сборкой несовместимы. Скрипт написан в ASCII: Windows PowerShell 5.1 читает `.ps1` без BOM
в ANSI и ломает кириллицу; `export_presets.cfg` пишется **без BOM** — с BOM Godot не находит пресет.

Проверено: в экспортированной игре проходят все самотесты
(`Builds\Windows\SW_V2.exe --headless -- --selftest`, код выхода 0) и работает скриншот-харнесс.

## Заметки по окружению (Windows)

- Из PowerShell запускать надёжнее, чем из Git Bash (при диагностике GUI-режимы из Git Bash падали).
- Проект нужно открывать **только double-редактором**: официальный уберёт метку `Double Precision` и соберёт C# против float.
- MSBuild на русской локали печатает сообщения по-русски — это нормально, ключевые слова для поиска ошибок: `error`, `Ошибок:`.
