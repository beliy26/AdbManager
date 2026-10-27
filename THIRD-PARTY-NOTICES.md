# Сторонние компоненты

Projectivy Launcher (https://github.com/spocky/miproja1) и LTvLauncher (https://github.com/leanbitlab-org/LtvLauncher,
GPL-3.0) в приложение не входят: по запросу пользователя программа скачивает APK выбранного лаунчера
с GitHub Releases автора и устанавливает на устройство.

## ЧистыйТВ — база пакетов и правила защиты

Файл `Assets/Data/chistytv-packages.json` и список защищённых пакетов в `Services/PackageAdvisor.cs`
взяты из проекта [ЧистыйТВ](https://github.com/by-sonic/chistytv)
(коммит `4290bfe7f7d889d8fb66aae10b67112842d0ffec`). Источники данных базы перечислены в
[docs/SOURCES.md](https://github.com/by-sonic/chistytv/blob/main/docs/SOURCES.md) проекта.

```
MIT License

Copyright (c) 2026 by-sonic
Portions (PROMPT.md approach and package research) derived from CleanYourTV,
Copyright (c) 2026 Gathin, https://github.com/Gathin23/cleanyourtv, MIT License.

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

## CleanYourTV — списки отключения, защита Home и входов ТВ, аудит приватности

Файл `Assets/Data/cleanyourtv-packages.json` собран скриптом `tools/import-cleanyourtv.py` из списков
`examples/*/debloat.txt` проекта [CleanYourTV](https://github.com/Gathin23/cleanyourtv)
(коммит `56cd3e81c062244ea313899d5af8d75294f2daae`). Оттуда же — правила защиты главного экрана и
входов HDMI/AV (`tools/disable.sh`), проверка после обновления прошивки (`tools/check.py`) и каталог
пакетов аудита приватности (`tools/privacy.py`), переписанные на C#.

```
MIT License

Copyright (c) 2026 Gathin

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
