# 01. Сборка кастомного Godot (double precision + C#)

Игре нужны **большие миры** (аналог Stormworks), поэтому используется Godot с `precision=double`
(«large world coordinates»). Официальных сборок с double precision **не существует** — движок нужно
собирать из исходников на каждом устройстве, где ведётся разработка.

Версия: **Godot 4.6.3-stable** (проверено также на 4.7.2 — поведение то же; раньше проект был зафиксирован
на 4.7.2, затем переключён на 4.6.3).
Целевая ОС в этом документе: **Windows 11** (MSVC).

> Плейсхолдер `<GODOT_SRC>` ниже — папка с исходниками движка на вашем устройстве.
> На основной машине разработки: `D:\Programs\Godot-4.6.3-double` (компилятор — VS Build Tools 2022,
> установлен туда же, в `D:\Programs\VSBuildTools`, чтобы не занимать системный диск C:).
> В репозиторий проекта исходники движка **не входят**.

## 1. Что нужно установить

| Что | Версия | Примечание |
|---|---|---|
| Git | любая | клонирование исходников |
| Visual Studio 2022 (Community/Build Tools) | 2019+ | компонент «Разработка классических приложений на C++», Windows 11 SDK |
| Python | 3.9+ | на Windows вызывать как `py -3.12`; команда `python` может указывать на заглушку Microsoft Store |
| SCons | 4.4+ | `py -3.12 -m pip install scons` (запуск: `py -3.12 -m SCons ...`, т.к. scons.exe не в PATH) |
| .NET SDK | 8.0+ | нужен и для сборки C#-части движка, и для самого проекта |

## 2. Исходники

```powershell
git clone --branch 4.6.3-stable --depth 1 https://github.com/godotengine/godot.git <GODOT_SRC>
cd <GODOT_SRC>
```

## 3. Сборка (полная последовательность)

Все команды — из корня `<GODOT_SRC>`. Первая полная сборка редактора занимает ~20–40 минут.

```powershell
# 1) Редактор (double + .NET)
py -3.12 -m SCons platform=windows target=editor precision=double module_mono_enabled=yes accesskit=no d3d12=no

# 2) C#-«клей» (bindings), генерируется самим только что собранным редактором
bin\godot.windows.editor.double.x86_64.mono.exe --headless --generate-mono-glue modules/mono/glue

# 3) Управляемые сборки (GodotSharp, GodotTools, Godot.NET.Sdk + nupkg-пакеты)
py -3.12 modules/mono/build_scripts/build_assemblies.py --godot-output-dir=./bin --precision=double

# 4) Шаблоны экспорта (нужны только для экспорта готовой игры)
py -3.12 -m SCons platform=windows target=template_debug   precision=double module_mono_enabled=yes accesskit=no d3d12=no
py -3.12 -m SCons platform=windows target=template_release precision=double module_mono_enabled=yes accesskit=no d3d12=no
```

Результат в `<GODOT_SRC>\bin\`:
- `godot.windows.editor.double.x86_64.mono.exe` (+ `.console.exe`) — редактор/запуск проекта;
- `godot.windows.template_{debug,release}.double.x86_64.mono.exe` — шаблоны экспорта;
- `GodotSharp\Api\...`, `GodotSharp\Tools\...` — C#-сборки движка, `GodotSharp\Tools\nupkgs` — NuGet-пакеты.

Проверка: баннер при запуске должен содержать `v4.6.3.stable.mono.double.custom_build`.

### Про флаги `accesskit=no d3d12=no`
Без них SCons печатает `ERROR: ... requires dependencies to be installed`, **но завершается с кодом 0 и
не собирает ничего** (легко не заметить). Эти драйверы (скринридеры, Direct3D 12) игре не нужны.
Если они всё же нужны — поставить зависимости скриптами `misc\scripts\install_accesskit.py` и
`misc\scripts\install_d3d12_sdk_windows.py`.

## 4. ⚠️ КРИТИЧЕСКАЯ ЛОВУШКА: `--precision=double` у `build_assemblies.py`

У `build_assemblies.py` есть собственный флаг `--precision`, **по умолчанию `single`**. Если его не
передать, C#-биндинги (`GodotSharp.dll`) собираются под **float**-структуры (`Vector2/3`, `Transform`, ...),
а нативный движок — под **double**. Несовпадение раскладки структур между нативным и управляемым кодом
приводит к **стопроцентному краху GUI-редактора** (access violation `0xC0000005`) в момент загрузки
C#-плагина редактора (GodotTools): в логе последним остаётся `EditorTheme: Generating new styles.`,
дальше процесс просто умирает. Headless-режим при этом может работать нормально, что сбивает с толку.

Что при диагностике **не** оказалось причиной (не тратьте время): рендерер (Vulkan/OpenGL), компилятор
(MSVC/MinGW), версия Godot (4.6/4.7), версия .NET SDK, AccessKit, целостность GodotTools.dll.
Признак верной сборки в логе msbuild: `/p:GodotFloat64=true`.

## 5. Локальный NuGet-источник (один раз на устройство)

`Godot.NET.Sdk`, `GodotSharp` и `Godot.SourceGenerators` версии `4.6.3` существуют и на nuget.org
(официальные, **float**). Проект обязан использовать **свои** пакеты, поэтому:

```powershell
dotnet nuget add source <GODOT_SRC>\bin\GodotSharp\Tools\nupkgs --name GodotDouble
```

В репозитории лежит `nuget.config` с `packageSourceMapping`: пакеты `Godot*` берутся **только** из источника
`GodotDouble`. Если источник не зарегистрирован — restore упадёт с понятной ошибкой (это защита от тихой
подмены на float-пакеты).

Проверка, что подтянулись верные пакеты: SHA256 файла
`%USERPROFILE%\.nuget\packages\godotsharp\4.6.3\lib\net8.0\GodotSharp.dll` должен совпадать с
`<GODOT_SRC>\bin\GodotSharp\Api\Release\GodotSharp.dll`. Если ранее на машине собирались проекты
официальным Godot 4.6.3, в кэше `%USERPROFILE%\.nuget\packages\godot*` может лежать float-версия —
удалите эти папки, чтобы restore взял локальные пакеты.

## 6. Запуск и правила работы

- Проект открывать **только** double-редактором (`godot.windows.editor.double.x86_64.mono.exe`).
  Официальный редактор уберёт из `project.godot` метку `Double Precision` и соберёт C# против float-API.
- Запуск GUI-редактора из скриптов — через PowerShell (`Start-Process`); из Git Bash при диагностике
  наблюдались случайные падения при запуске GUI-режимов.
- После **любой** пересборки движка (`editor`) повторять шаги 2–3 (глю + `build_assemblies.py --precision=double`),
  затем очистить кэш `%USERPROFILE%\.nuget\packages\godot*` и пересобрать проект, иначе NuGet
  оставит в кэше пакеты от прошлой сборки.

## 7. Что уже было проверено (для истории)

- Собирается и MSVC 2022, и MinGW-w64 (GCC 16.2) — результат одинаковый; MSVC достаточно.
- Godot 4.6.3 и 4.7.2 ведут себя идентично; проект **переключён на 4.6.3** (был зафиксирован на 4.7.2).
- Чисто GDScript-редактор с double precision (без `module_mono_enabled`) всегда работал — проблема была
  исключительно в C#-части (см. п. 4).

## 8. Вторая машина разработки (для истории)

Собрано по этой инструкции целиком, включая шаблоны экспорта, на машине с Windows 11 без предустановленных
инструментов сборки. Что ставилось (везде на диск `D:`, чтобы не занимать системный `C:`, там было мало места):
`winget install Microsoft.DotNet.SDK.8`; `winget install Microsoft.VisualStudio.2022.BuildTools --override
"--installPath D:\Programs\VSBuildTools --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended
--add Microsoft.VisualStudio.Component.Windows11SDK.22621"`; SCons — `py -3.10 -m pip install scons`
(на этой машине `py -3.12` не было, использован уже стоявший Python 3.10). Исходники клонированы в
`D:\Programs\Godot-4.6.3-double`. Все 4 шага (editor, glue, build_assemblies --precision=double,
template_debug, template_release) прошли без ошибок с первого раза; баннер `--headless --version`
подтвердил `4.6.3.stable.mono.double.custom_build`. NuGet-источник `GodotDouble` зарегистрирован,
проект пересобран (`dotnet build`), SHA256 `GodotSharp.dll` совпал, самотесты — 55 passed, 0 failed.
