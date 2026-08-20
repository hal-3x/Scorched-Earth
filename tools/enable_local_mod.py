"""
Put the local ScorchedEarth entry back into the active Cities: Skylines II playset.

The playset is the game's source of truth for which mods exist and are enabled. A
backend-synced playset only knows about Paradox Mods entries, so a re-sync can drop a
`local` entry - and once it is gone the mod stops appearing in the in-game mod list and in
Skyve, even though the DLL is still sitting in the local mods folder.

This adds the entry back. It refuses to run while the game is open, because the game holds a
lock on the config and would overwrite anything written underneath it. A timestamped backup
is taken before any change.

    python tools/enable_local_mod.py            # add the entry
    python tools/enable_local_mod.py --status   # just report what is there
"""

import argparse
import datetime
import io
import json
import os
import shutil
import subprocess
import sys

MOD_ID = "ScorchedEarth"

CONFIG = os.path.join(
    os.environ.get("LOCALAPPDATA", "") + "Low",
    "Colossal Order", "Cities Skylines II", ".cache", "Mods", "playset_config.json")

LOCK = os.path.join(os.path.dirname(CONFIG), "playset_config_lock.json")
LOCAL_MODS = os.path.join(os.path.dirname(CONFIG), "local")


def game_is_running():
    """True if Cities2.exe is up. Editing the config under a running game is pointless."""
    try:
        out = subprocess.check_output(["tasklist"], text=True, errors="ignore")
    except Exception:
        return False  # Cannot tell; the lock check below still applies.
    return "Cities2.exe" in out


def load_config():
    if not os.path.isfile(CONFIG):
        raise SystemExit("Playset config not found at %s" % CONFIG)
    with io.open(CONFIG, encoding="utf-8") as handle:
        return json.load(handle)


def describe(config):
    active = config.get("activePlaysetId")
    for playset in config.get("playsets", []):
        mods = playset.get("mods", [])
        sources = {}
        for mod in mods:
            sources[mod.get("source")] = sources.get(mod.get("source"), 0) + 1

        marker = " (active)" if playset.get("id") == active else ""
        name = playset.get("presentation", {}).get("name", "?")
        print("%-28s%s  %d mods  %s" % (name, marker, len(mods), sources))

        for mod in mods:
            if mod.get("source") != "pdx_mods":
                print("    non-pdx entry:", mod)


def main():
    parser = argparse.ArgumentParser(description="Re-enable the local ScorchedEarth mod entry.")
    parser.add_argument("--status", action="store_true", help="report only, change nothing")
    args = parser.parse_args()

    config = load_config()

    if args.status:
        describe(config)
        deployed = os.path.join(LOCAL_MODS, MOD_ID, MOD_ID + ".dll")
        print("\nDLL present:", os.path.isfile(deployed), "->", deployed)
        return

    if game_is_running():
        raise SystemExit(
            "Cities: Skylines II is running. Close it first - the game holds a lock on the\n"
            "playset config and will overwrite anything written while it is open.")

    deployed = os.path.join(LOCAL_MODS, MOD_ID, MOD_ID + ".dll")
    if not os.path.isfile(deployed):
        raise SystemExit("No mod DLL at %s - build and deploy it first." % deployed)

    active = config.get("activePlaysetId")
    target = None
    for playset in config.get("playsets", []):
        if playset.get("id") == active:
            target = playset
            break

    if target is None:
        raise SystemExit("Could not find the active playset (%s) in the config." % active)

    mods = target.setdefault("mods", [])

    for mod in mods:
        if mod.get("source") == "local" and mod.get("sourceId") == MOD_ID:
            if mod.get("isEnabled"):
                print("Already present and enabled in playset '%s'; nothing to do."
                      % target.get("presentation", {}).get("name", "?"))
                return
            mod["isEnabled"] = True
            break
    else:
        mods.append({"source": "local", "sourceId": MOD_ID, "isEnabled": True})

    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    backup = CONFIG + "." + stamp + ".bak"
    shutil.copy2(CONFIG, backup)

    with io.open(CONFIG, "w", encoding="utf-8") as handle:
        handle.write(json.dumps(config, ensure_ascii=False))

    print("Backed up  : %s" % backup)
    print("Enabled    : %s (local) in playset '%s'"
          % (MOD_ID, target.get("presentation", {}).get("name", "?")))
    print("Start the game and check Logs/ScorchedEarth.log for 'Scorched Earth ... loading'.")


if __name__ == "__main__":
    main()
