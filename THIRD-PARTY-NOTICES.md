# Сторонние уведомления

## Explorer Tab Utility

Адаптированные фрагменты из проекта [w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility), коммит [`fad736353210d36595c6e6caa77ac7ad9129b3ee`](https://github.com/w4po/ExplorerTabUtility/commit/fad736353210d36595c6e6caa77ac7ad9129b3ee), используются в Shell-интеграции, управлении представлением окна и низкоуровневой работе с нативными вкладками. Соответствие исходников:

| Исходник ExplorerOne | Источник в ExplorerTabUtility |
| --- | --- |
| [`ShellInterop.cs`](src/ExplorerOne/Shell/ShellInterop.cs) | [`IServiceProvider.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Interop/IServiceProvider.cs), [`IShellBrowser.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Interop/IShellBrowser.cs), [`IShellFolder.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Interop/IShellFolder.cs), [`ShellPathComparer.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Interop/ShellPathComparer.cs), [`WinApi.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/WinAPI/WinApi.cs) и команда `Ctrl+T` из [`ExplorerWatcher.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Hooks/ExplorerWatcher.cs) |
| [`ShellExplorerBackend.cs`](src/ExplorerOne/Shell/ShellExplorerBackend.cs) | Отдельные части `GetTabHandle`, `GetRecentlyCreatedWindow`, `GetLocation`, `GetSelectedItems`, `SelectItems`, `Navigate` и `RequestToOpenNewTab` из [`ExplorerWatcher.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Hooks/ExplorerWatcher.cs), а также вспомогательные проверки окон и вкладок из [`Helper.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Helpers/Helper.cs) |
| [`SourceWindowPresentation.cs`](src/ExplorerOne/SourceWindowPresentation.cs) | Адаптированные `HideWindow` и `UpdateWindowLayered` из [`Helper.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Helpers/Helper.cs); для layered-window операций также используется [`WinApi.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/WinAPI/WinApi.cs) |
| [`NativeTabReservations.cs`](src/ExplorerOne/Shell/NativeTabReservations.cs) | Низкоуровневый запрос нативной `Ctrl+T` из [`ExplorerWatcher.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Hooks/ExplorerWatcher.cs) и обнаружение окон класса `ShellTabWindowClass` по вспомогательным Win32-методам из [`Helper.cs`](https://github.com/w4po/ExplorerTabUtility/blob/fad736353210d36595c6e6caa77ac7ad9129b3ee/ExplorerTabUtility/Helpers/Helper.cs); состояние резервирования и точная привязка HWND написаны для ExplorerOne |

Очередь переноса, проверка полного состояния окон, учёт частичных переносов и финальная защита исходного окна написаны для ExplorerOne. Для `SourceWindowPresentation.cs` адаптированы только указанные методы; управление их жизненным циклом и восстановлением исходного состояния реализовано вокруг них для ExplorerOne. В `NativeTabReservations.cs` адаптированы только низкоуровневый запрос `Ctrl+T` и обнаружение класса вкладки; резервирование, отмена, передача и проверка точного HWND являются новой логикой ExplorerOne. Таблица указывает перечисленные адаптированные части; остальной код интеграции написан для этого проекта. Источник и зафиксированный коммит для перечисленных фрагментов указаны в этом уведомлении.

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

## MinHook 1.3.4

Нативный маршрут открытия использует [MinHook](https://github.com/TsudaKageyu/minhook/tree/v1.3.4), тег v1.3.4. Исходники включены в `src/ExplorerOne.RouterNative/vendor/minhook-1.3.4/`. Библиотека создаёт и отключает проверенный перехват точки открытия Проводника. Полный текст лицензии, включая уведомления Hacker Disassembler Engine:

```text
MinHook - The Minimalistic API Hooking Library for x64/x86
Copyright (C) 2009-2017 Tsuda Kageyu.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

 1. Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
 2. Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in the
    documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

================================================================================
Portions of this software are Copyright (c) 2008-2009, Vyacheslav Patkov.
================================================================================
Hacker Disassembler Engine 32 C
Copyright (c) 2008-2009, Vyacheslav Patkov.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

 1. Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
 2. Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in the
    documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE REGENTS OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

-------------------------------------------------------------------------------
Hacker Disassembler Engine 64 C
Copyright (c) 2008-2009, Vyacheslav Patkov.
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

 1. Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.
 2. Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in the
    documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE REGENTS OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

```
