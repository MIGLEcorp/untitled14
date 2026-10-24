# Движки Feather

Основные вкладки используют системный **Microsoft Edge WebView2 (Chromium)**.
Команда меню **«WebKit — экспериментальное окно»** открывает текущий адрес в
настоящем WebKit из официального `Microsoft.Playwright`, в отдельном процессе.
WebView2 не переименован в WebKit; движок основных вкладок не меняется.

## Что работает

- WebKit запускается с видимым окном и открывает HTTP/HTTPS-адрес текущей вкладки.
- При первом использовании приложение предлагает скачать WebKit с серверов Microsoft.
  Другие движки Playwright не устанавливаются.
- Данные WebKit отделены от основных вкладок: `%LOCALAPPDATA%\FeatherBrowser\WebKitPreview`.
  Никакие профили Chrome, Edge или Firefox этот модуль не открывает.
- Установленный движок находится в `%LOCALAPPDATA%\FeatherBrowser\Engines\WebKit`.
- Основной процесс не ссылается на Playwright. Его DLL, Node и процессы WebKit
  загружаются только после команды меню и завершаются после закрытия окна WebKit.
- Повторный запуск с уже открытым профилем сообщает о существующем окне.

## Ограничения

Это **экспериментальный просмотр**, а не полноценная замена движка вкладок.
Playwright предназначен для автоматизации и тестирования, использует собственные
патчи WebKit и не является Safari. В этом отдельном окне нет панели вкладок Feather,
его закладок, импорта, менеджера паролей и интеграции загрузок. Cookies двух движков
не синхронизируются. Поддержка мультимедиа зависит от Windows и сборки WebKit.

Текущий Playwright официально поддерживает Windows 11+ / Windows Server 2019+;
основной Feather и экспериментальный модуль могут иметь разные требования.
WebKit здесь не получает обновления вместе с системным WebView2. При обновлении
закреплённого пакета Playwright нужно повторно установить соответствующую версию
WebKit. Не используйте старые WebKit.NET, QtWebKit или Safari для Windows как замену.

Для полноценного встроенного WebKit на Windows потребуется отдельный нативный
адаптер и собственная сборка/доставка/обновления WinCairo. У проекта WebKit есть
поддерживаемая 64-битная Windows-сборка и MiniBrowser, но готовый официальный
WinForms-контрол, аналогичный WebView2, этим способом не предоставляется.

## Сборка и ручной запуск

`build.ps1` помещает отдельный модуль в `dist\Features\WebKit`. При запуске из
исходников пункт меню может собрать модуль отдельно при наличии .NET SDK 10.
Пакет закреплён в `Engine\WebKitPreview\Feather.WebKit.csproj`.

```powershell
dotnet publish .\Engine\WebKitPreview\Feather.WebKit.csproj -c Release -r win-x64 --self-contained false -o .\dist\Features\WebKit
.\dist\Features\WebKit\Feather.WebKit.exe --url https://example.com/
```

Предварительно установить только движок:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Engine\install-webkit.ps1
```

CLI модуля: `--url URL`, `--profile PATH`, `--browsers-path PATH`, `--install`.
`--install` завершает процесс после скачивания, не открывая окно браузера.
Пути в аргументах командной строки передавайте в кавычках, если они содержат пробелы.

## Проверка настоящего WebKit

Временные пути позволяют проверить движок без профиля пользователя:

```powershell
$webKitTestRoot = Join-Path $env:TEMP 'Feather-WebKit-test'
$webKitHelper = '.\dist\Features\WebKit\Feather.WebKit.exe'
Start-Process $webKitHelper -ArgumentList @('--install', '--browsers-path', ('"' + $webKitTestRoot + '\browsers"')) -WindowStyle Hidden -Wait
Start-Process $webKitHelper -ArgumentList @('--smoke-test', '--browsers-path', ('"' + $webKitTestRoot + '\browsers"'), '--result', ('"' + $webKitTestRoot + '\result.json"')) -WindowStyle Hidden -Wait
Get-Content (Join-Path $webKitTestRoot 'result.json')
```

Проверяются DOM, JavaScript, cookies, изоляция двух контекстов, сохранение cookies
после повторного открытия отдельного тестового профиля и событие закрытия контекста.
Сохраняются фактическая версия движка, User-Agent и PNG страницы. Сеть подменяется локальным
маршрутом Playwright; внешние сайты и существующие профили не используются.
Для проверки видимого окна добавьте `--headed`.

## Официальные источники

- [Порт WebKit для Windows, WinCairo и MiniBrowser](https://docs.webkit.org/Ports/WindowsPort.html)
- [Playwright: движки, происхождение WebKit и установка](https://playwright.dev/dotnet/docs/browsers)
- [Playwright: системные требования](https://playwright.dev/dotnet/docs/intro#system-requirements)
- [Microsoft.Playwright в NuGet](https://www.nuget.org/packages/Microsoft.Playwright)
- [API постоянного профиля WebKit](https://playwright.dev/dotnet/docs/api/class-browsertype#browser-type-launch-persistent-context)
