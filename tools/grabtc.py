#!/usr/bin/env python3
"""grabtc — read the burned-in timecode off a member's actual output.

The Gate 0 output-truth channel (docs/shakedown.md §1). Captures what the
device is really showing and OCRs the timecode the test asset burns into every
frame, so a member's *reported* position can be checked against its *actual*
one.

Kodi's own ``TakeScreenshot`` is deliberately not used: on the flatpak Kodi 22
desktop build it fails outright — ``CCaptureService::Fail ... capture request
failed (content 0, 0x0)`` — so the capture has to come from outside Kodi. That
also makes the channel uniform across platforms, since Android never had a
working Kodi-side path either.

Two capture backends:

* ``--x11-window <id>``  — ImageMagick ``import`` against the Kodi window.
  Capturing the window rather than the root keeps it fast (~200 ms vs ~2.6 s
  for a 4K root grab) and avoids capturing the rest of the desktop.
* ``--adb <serial>``     — ``adb exec-out screencap -p``.
* ``--adb-raw <serial>`` — same, without the on-device PNG encode, which
  more than halves the timed window (1131 ms -> 501 ms measured).

Prints the timecode in milliseconds, or nothing at all when the frame carries
no readable timecode — which is itself the Gate 0 finding for that device and
path, not an error to paper over.

    tools/grabtc.py --x11-window 0x9200002
    tools/grabtc.py --adb 192.168.1.150:42753 --keep /tmp/tab.png
"""

import argparse
import re
import subprocess
import sys
import tempfile
import os

# The asset burns "HH:MM:SS.mmm  f=N" into the top-left of the frame. Expressed
# as fractions of the captured image so one default works at any resolution or
# window size.
# Generous enough to cover an unletterboxed 16:9 window (L22: text at y 2-13%
# of the frame) and a letterboxed 16:10 panel (TAB 2560x1600 showing 16:9: the
# black bars push the same text down to y 6-15%).
CROP = (0.005, 0.02, 0.57, 0.14)  # x, y, w, h

TIMECODE = re.compile(r"(\d{1,2})\s*[:;]\s*(\d{2})\s*[:;]\s*(\d{2})\s*[.,]\s*(\d{1,3})")


def capture(args, path):
    if args.x11_window:
        env = dict(os.environ, DISPLAY=args.display)
        subprocess.run(
            ["import", "-window", args.x11_window, path],
            check=True, capture_output=True, env=env, timeout=20,
        )
    elif args.adb_raw:
        capture_adb_raw(args.adb_raw, path)
    else:
        raw = subprocess.run(
            ["adb", "-s", args.adb, "exec-out", "screencap", "-p"],
            check=True, capture_output=True, timeout=30,
        ).stdout
        if not raw:
            raise RuntimeError("screencap returned nothing")
        with open(path, "wb") as handle:
            handle.write(raw)


DEVICE_FILE = "/sdcard/grabtc.raw"


def adb_grab(serial):
    """The timed step, and *only* the timed step: sample the surface on the
    device. No PNG encode (measured 1131 ms with, 501 ms without) and no
    transfer, because both happen after the frame is taken and would otherwise
    be charged to the frame's instant — which is exactly the mistake that made
    a raw-capture run read a 2.7 s window and a bogus +985 ms bias."""
    subprocess.run(
        ["adb", "-s", serial, "shell", "screencap", DEVICE_FILE],
        check=True, capture_output=True, timeout=60,
    )


def capture_adb_raw(serial, path, grab=True):
    """Fetch the grabbed frame and convert it. Untimed."""
    if grab:
        adb_grab(serial)
    local_raw = path + ".raw"
    subprocess.run(
        ["adb", "-s", serial, "pull", DEVICE_FILE, local_raw],
        check=True, capture_output=True, timeout=120,
    )
    try:
        with open(local_raw, "rb") as handle:
            blob = handle.read()
        width = int.from_bytes(blob[0:4], "little")
        height = int.from_bytes(blob[4:8], "little")
        # The header is 12 bytes (w, h, format) on most builds and 16 on some;
        # pick whichever leaves exactly width*height*4 bytes of pixels.
        for header in (12, 16):
            if len(blob) - header == width * height * 4:
                break
        else:
            raise RuntimeError(
                "unexpected raw screencap layout: %dx%d, %d bytes"
                % (width, height, len(blob)))
        subprocess.run(
            ["magick", "-size", "%dx%d" % (width, height), "-depth", "8",
             "rgba:-", path],
            input=blob[header:], check=True, capture_output=True, timeout=60,
        )
    finally:
        try:
            os.unlink(local_raw)
        except OSError:
            pass


def ocr(path, crop, keep=None):
    """Crop to the timecode band, make it black-on-white, and pad it.

    The padding is load-bearing: tesseract returns an empty string on a clean,
    correctly-thresholded crop that runs to the image edge, and starts reading
    it perfectly once there is a white margin around the glyphs.
    """
    size = subprocess.run(
        ["identify", "-format", "%w %h", path],
        check=True, capture_output=True, text=True, timeout=20,
    ).stdout.split()
    width, height = int(size[0]), int(size[1])
    fx, fy, fw, fh = crop
    geometry = "%dx%d+%d+%d" % (
        max(1, int(width * fw)), max(1, int(height * fh)),
        int(width * fx), int(height * fy),
    )

    prepared = keep or tempfile.mktemp(suffix=".png")

    # The timecode is pure white text in a semi-transparent box over the
    # asset's colour bars, so its contrast changes frame to frame and with the
    # bar underneath it. Greyscale cannot separate them: on the Tab the bars
    # are bright (red/green/yellow all convert to a light grey) and every
    # threshold blew the whole crop out to white.
    #
    # min(R,G,B) does separate them, because it is high only where all three
    # channels are high — white text survives, every saturated bar collapses to
    # near zero. That reads on both devices and, as a by-product, gets the
    # frame counter right where greyscale misread it. The greyscale ladder is
    # kept behind it for any frame where the box is over something neutral.
    # A Kodi dialog dims the video behind it, which drops white text from
    # ~100% to ~45% and made every threshold above that miss — so the ladder
    # has to reach below a dimmed white before it gives up.
    variants = (
        ["-separate", "-evaluate-sequence", "min", "-threshold", "70%"],
        ["-separate", "-evaluate-sequence", "min", "-threshold", "50%"],
        ["-separate", "-evaluate-sequence", "min", "-threshold", "35%"],
        ["-separate", "-evaluate-sequence", "min", "-normalize",
         "-threshold", "60%"],
        ["-colorspace", "gray"],
        ["-colorspace", "gray", "-threshold", "55%"],
        ["-colorspace", "gray", "-contrast-stretch", "5%x5%", "-threshold", "55%"],
    )

    try:
        for variant in variants:
            subprocess.run(
                ["magick", path, "-crop", geometry, "+repage"] + variant +
                ["-negate", "-resize", "250%",
                 "-bordercolor", "white", "-border", "30", prepared],
                check=True, capture_output=True, timeout=30,
            )
            for psm in ("7", "6", "11"):
                text = subprocess.run(
                    ["tesseract", prepared, "-", "--psm", psm],
                    capture_output=True, text=True, timeout=30,
                ).stdout
                match = TIMECODE.search(text)
                if match:
                    h, m, s, frac = match.groups()
                    if int(m) > 59 or int(s) > 59 or int(h) > 12:
                        # A misread, not a timecode. The hours bound matters:
                        # one sample OCRed as 90:00:00 and, being arithmetically
                        # valid, sailed through into the statistics.
                        continue
                    return (int(h) * 3600000 + int(m) * 60000 + int(s) * 1000
                            + int(frac.ljust(3, "0")))
        return None
    finally:
        if not keep:
            try:
                os.unlink(prepared)
            except OSError:
                pass


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--x11-window", help="X window id, e.g. 0x9200002")
    source.add_argument("--adb", help="adb serial (on-device PNG encode)")
    source.add_argument("--adb-raw", metavar="SERIAL",
                        help="adb serial, raw grab — 2.3x tighter capture window")
    source.add_argument("--adb-grab", metavar="SERIAL",
                        help="sample the device surface and exit. THE timed step "
                             "for an adb member; pair with --adb-collect")
    source.add_argument("--adb-collect", metavar="SERIAL",
                        help="fetch and convert the frame --adb-grab took; untimed")
    source.add_argument("--from-file", help="OCR an already-captured frame")
    parser.add_argument(
        "--capture-only", metavar="PATH",
        help="capture a frame to PATH and exit without OCR. Callers timing a "
             "capture must use this: OCR is far slower and more variable than "
             "the grab, so timing them together buries the frame's instant in "
             "the OCR ladder's runtime.",
    )
    parser.add_argument("--display", default=":0")
    parser.add_argument("--crop", help="x,y,w,h as fractions of the frame")
    parser.add_argument("--keep", help="keep the prepared crop here (for tuning)")
    parser.add_argument("--raw", help="keep the raw capture here")
    args = parser.parse_args()

    crop = CROP
    if args.crop:
        crop = tuple(float(v) for v in args.crop.split(","))

    if args.adb_grab:
        try:
            adb_grab(args.adb_grab)
        except (subprocess.SubprocessError, OSError) as error:
            print("grab failed: %s" % error, file=sys.stderr)
            return 2
        return 0

    if args.adb_collect:
        path = args.capture_only or args.raw or tempfile.mktemp(suffix=".png")
        try:
            capture_adb_raw(args.adb_collect, path, grab=False)
        except (subprocess.SubprocessError, OSError, RuntimeError) as error:
            print("collect failed: %s" % error, file=sys.stderr)
            return 2
        if args.capture_only:
            return 0
    elif args.from_file:
        path = args.from_file
    else:
        path = args.capture_only or args.raw or tempfile.mktemp(suffix=".png")
        try:
            capture(args, path)
        except (subprocess.SubprocessError, OSError, RuntimeError) as error:
            print("capture failed: %s" % error, file=sys.stderr)
            return 2
        if args.capture_only:
            return 0

    try:
        value = ocr(path, crop, args.keep)
    finally:
        if not args.raw and not args.from_file:
            try:
                os.unlink(path)
            except OSError:
                pass

    if value is None:
        print("no timecode in frame", file=sys.stderr)
        return 1
    print(value)
    return 0


if __name__ == "__main__":
    sys.exit(main())
