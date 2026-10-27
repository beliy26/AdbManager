"""Собирает src/AdbManager/Assets/Data/cleanyourtv-packages.json из списков отключения проекта CleanYourTV.

    git clone --depth 1 https://github.com/Gathin23/cleanyourtv <папка>
    python -I tools/import-cleanyourtv.py <папка> src/AdbManager/Assets/Data/cleanyourtv-packages.json

Списки examples/*/debloat.txt (MIT, см. THIRD-PARTY-NOTICES.md): по пакету на строку, «# комментарий»
после имени — что это. Для каждого пакета берётся самый частый комментарий и число моделей ТВ, где он
отключён. Формат результата — как у chistytv-packages.json.
"""
import collections
import json
import pathlib
import re
import subprocess
import sys

PACKAGE_RE = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")

# Реклама, телеметрия, демо и рекомендации — «Можно отключить»; остальное (видеосервисы, браузеры,
# игры и т.п.) — «По желанию»: их кто-то отключил на своём ТВ, но ими можно пользоваться.
DISABLE_RE = re.compile(
    r"\b(ads?|advert\w*|promo\w*|analytics?|telemetry|track\w*|statistic\w*|stats|logg?\w*|logs?|report\w*|"
    r"feedback|demo\w*|recommend\w*|sponsor\w*|acr|survey|usage|crash\w*|marketing|banner|sticker|"
    r"bloat\w*|upsell|nag\w*|spy\w*|data collection|diagnostic\w*|bugreport\w*|personali[sz]\w*|"
    r"inscape|alphonso|samba)\b",
    re.IGNORECASE,
)

# Главный экран и мастер настройки отключаются в CleanYourTV только после установки своего лаунчера;
# без него ТВ остаётся без Home. Их в базу не берём (ещё и защищены в программе).
SKIP = {
    "com.google.android.tvlauncher",
    "com.google.android.apps.tv.launcherx",
    "com.google.android.leanbacklauncher",
    "com.google.android.tungsten.setupwraith",
}


def main():
    src = pathlib.Path(sys.argv[1])
    out = pathlib.Path(sys.argv[2])
    comments = collections.defaultdict(collections.Counter)
    models = collections.defaultdict(set)

    for lst in sorted(src.glob("examples/*/debloat.txt")):
        text = lst.read_text(encoding="utf-8-sig", errors="replace")
        for raw in text.splitlines():
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            name, _, comment = line.partition("#")
            name = name.split()[0] if name.split() else ""
            if not PACKAGE_RE.match(name) or name in SKIP:
                continue
            models[name].add(lst.parent.name)
            comment = comment.strip()
            if comment:
                comments[name][comment] += 1

    packages = []
    for name in sorted(models):
        title = comments[name].most_common(1)[0][0] if comments[name] else ""
        count = len(models[name])
        action = "disable" if DISABLE_RE.search(title) or DISABLE_RE.search(name.replace(".", " ")) else "ask"
        packages.append({
            "id": name,
            "platform": "any",
            "action": action,
            "title": title or name,
            # Пояснение «отключён на N моделях ТВ» программа строит из ресурсов на языке интерфейса.
            "models": count,
        })

    commit = subprocess.run(["git", "-C", str(src), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    data = {
        "source": "https://github.com/Gathin23/cleanyourtv (MIT), examples/*/debloat.txt",
        "commit": commit,
        "platforms": [],
        "packages": packages,
    }
    out.write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    disable = sum(p["action"] == "disable" for p in packages)
    print(f"{len(packages)} packages ({disable} disable, {len(packages) - disable} ask), commit {commit}")


if __name__ == "__main__":
    main()
