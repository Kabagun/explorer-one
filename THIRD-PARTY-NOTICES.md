# Сторонние уведомления

## Explorer Tab Utility

Адаптированные фрагменты из проекта [w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility), коммит [`fad736353210d36595c6e6caa77ac7ad9129b3ee`](https://github.com/w4po/ExplorerTabUtility/commit/fad736353210d36595c6e6caa77ac7ad9129b3ee), используются и в `src/ExplorerOne/Shell/`, и в управлении представлением окна. Соответствие исходников:

| Исходник ExplorerOne | Источник в ExplorerTabUtility |
| --- | --- |
| [`ShellInterop.cs`](src/ExplorerOne/Shell/ShellInterop.cs) | [`IServiceProvider.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Interop/IServiceProvider.cs), [`IShellBrowser.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Interop/IShellBrowser.cs), [`IShellFolder.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Interop/IShellFolder.cs), [`ShellPathComparer.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Interop/ShellPathComparer.cs), [`WinApi.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/WinAPI/WinApi.cs) и команда `Ctrl+T` из [`ExplorerWatcher.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Hooks/ExplorerWatcher.cs) |
| [`ShellExplorerBackend.cs`](src/ExplorerOne/Shell/ShellExplorerBackend.cs) | Отдельные части `GetTabHandle`, `GetRecentlyCreatedWindow`, `GetLocation`, `GetSelectedItems`, `SelectItems`, `Navigate` и `RequestToOpenNewTab` из [`ExplorerWatcher.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Hooks/ExplorerWatcher.cs), а также вспомогательные проверки окон и вкладок из [`Helper.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Helpers/Helper.cs) |
| [`SourceWindowPresentation.cs`](src/ExplorerOne/SourceWindowPresentation.cs) | Адаптированные `HideWindow` и `UpdateWindowLayered` из [`Helper.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Helpers/Helper.cs); для layered-window операций также используется [`WinApi.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/WinAPI/WinApi.cs) |

Очередь переноса, проверка полного состояния окон, учёт частичных переносов и финальная защита исходного окна написаны для ExplorerOne. Для `SourceWindowPresentation.cs` из ETU взяты только указанные методы; управление их жизненным циклом, восстановление исходного состояния, alpha-подавление и резервное сворачивание реализованы вокруг них для ExplorerOne. Таблица указывает только перечисленные адаптированные части; окружающий код интеграции и остальные компоненты написаны для этого проекта. Комментарии в адаптированных исходниках отмечают источник и зафиксированный коммит.

**Атрибуция проекта:** Copyright © 2025 w4po.

**Лицензия:** MIT. Текст ниже сохранён из [LICENSE в указанном коммите](https://raw.githubusercontent.com/w4po/ExplorerTabUtility/fad736353210d36595c6e6caa77ac7ad9129b3ee/LICENSE), включая исходную строку-заполнитель авторского права.

```text
MIT License

Copyright (c) [year] [fullname]

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Изображения, логотипы и значки из ExplorerTabUtility не копировались. Сторонние NuGet-пакеты не используются.
