"""Проверка строковых ресурсов (src/AdbManager/Strings/<язык>/Resources.resw).

- у всех языков одинаковый набор ключей;
- ключи из Res.Get/Res.Format в коде и x:Uid в XAML есть в ресурсах, неиспользуемых ключей нет;
- в переводах совпадают подстановки {0}, {1:...};
- в коде и XAML не осталось русских литералов (строки с «loc-ignore» пропускаются).

Запуск: python tools/check-resources.py (код возврата 1 — есть ошибки).
"""
import re
import sys
from pathlib import Path
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parent.parent / 'src' / 'AdbManager'
CYR = re.compile('[А-Яа-яЁё]')


def load(path):
    return {d.get('name'): (d.findtext('value') or '') for d in ElementTree.parse(path).getroot().iter('data')}


def main():
    errors = []
    langs = {p.parent.name: load(p) for p in sorted((ROOT / 'Strings').glob('*/Resources.resw'))}
    names = list(langs)
    base = langs[names[0]]
    for lang in names[1:]:
        for k in base.keys() - langs[lang].keys():
            errors.append(f'{lang}: нет ключа {k}')
        for k in langs[lang].keys() - base.keys():
            errors.append(f'{names[0]}: нет ключа {k}')
    for k in base:
        holes = {lang: sorted(re.findall(r'\{\d+[^}]*\}', res.get(k, ''))) for lang, res in langs.items()}
        if len({tuple(v) for v in holes.values()}) > 1:
            errors.append(f'{k}: разные подстановки {holes}')

    used = set()
    uids = set()
    for f in ROOT.rglob('*.cs'):
        if 'obj' in f.parts or 'bin' in f.parts:
            continue
        text = f.read_text(encoding='utf-8')
        used |= set(re.findall(r'Res\.(?:Get|Format)\("([^"]+)"', text))
        for n, line in enumerate(text.splitlines(), 1):
            code = line.split('//')[0]
            if re.search(r'"[^"]*' + CYR.pattern, code) and 'loc-ignore' not in line and not line.strip().startswith('///'):
                errors.append(f'{f.relative_to(ROOT)}:{n}: русский литерал в коде')
    for f in ROOT.glob('*.xaml'):
        text = f.read_text(encoding='utf-8')
        uids |= set(re.findall(r'x:Uid="([^"]+)"', text))
        for n, line in enumerate(text.splitlines(), 1):
            if re.search(r'(Text|Content|Label|ToolTip|Header|Title|Message|PlaceholderText)="[^"{]*' + CYR.pattern, line) \
                    and not re.search(r'Text="(Русский)"', line):
                errors.append(f'{f.relative_to(ROOT)}:{n}: русский литерал в XAML (нужен x:Uid)')

    for k in sorted(used - base.keys()):
        errors.append(f'в коде используется отсутствующий ключ {k}')
    for uid in sorted(uids):
        if not any(k.startswith(uid + '.') for k in base):
            errors.append(f'x:Uid="{uid}" без строк в ресурсах')
    for k in sorted(base):
        if '.' in k:
            if k.split('.')[0] not in uids:
                errors.append(f'{k}: нет элемента с таким x:Uid')
        elif k not in used and k != 'LanguageTag':
            errors.append(f'{k}: не используется')

    for e in errors:
        print(e)
    print(f'ключей: {len(base)}, языков: {len(langs)}, ошибок: {len(errors)}')
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
