"""Writes an entry of the list of relays for everyone (public-relays.json) with its address not readable at a glance.

    python tools/relay_list.py hide <name> <https address> [word]    prints the entry to put into the list
    python tools/relay_list.py show <hidden>                         prints what a hidden field holds

The list is a public file. A relay's address written out in it is in front of everyone who opens or searches the
repository; in the "hidden" field it is not. This keeps it from a look and from a search, not from someone who reads
the program: the program has to know the address to connect, and what it does to read the field is done here too.
"""
import base64
import hashlib
import json
import sys

PAD = hashlib.sha256(b"remote-cli public relays").digest()


def mixed(data):
    return bytes(byte ^ PAD[at % len(PAD)] for at, byte in enumerate(data))


def hide(url, key=""):
    """The "hidden" field for a relay at `url` that asks for `key` (nothing when it asks for none)."""
    plain = json.dumps({"url": url.rstrip("/"), "key": key}, separators=(",", ":")).encode("utf-8")
    return base64.urlsafe_b64encode(mixed(plain)).decode("ascii").rstrip("=")


def show(hidden):
    return json.loads(mixed(base64.urlsafe_b64decode(hidden + "=" * (-len(hidden) % 4))).decode("utf-8"))


def main(args):
    if len(args) in (3, 4) and args[0] == "hide":
        print(json.dumps({"name": args[1], "hidden": hide(args[2], args[3] if len(args) == 4 else "")}, ensure_ascii=False))
    elif len(args) == 2 and args[0] == "show":
        print(json.dumps(show(args[1]), ensure_ascii=False))
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
