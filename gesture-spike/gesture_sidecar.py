"""
BorderDock gesture SIDECAR (headless).

No preview window, no keys — just reads the camera, detects gestures, and prints
one line per stable gesture to stdout for the C# app to read:

    GESTURE:OPEN_PALM
    GESTURE:FIST
    GESTURE:SWIPE_LEFT
    GESTURE:SWIPE_RIGHT

Tuned defaults come from the interactive tuner (hold 8f, swipe 0.18, smooth 0.5).
BorderDock launches this with the venv python and reads stdout. Exits cleanly on
camera failure (prints ERROR:... first).
"""

import os
import sys
import time
import cv2
import mediapipe as mp
from mediapipe.tasks.python import vision, BaseOptions

MODEL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hand_landmarker.task")

WRIST = 0
TIPS = {"thumb": 4, "index": 8, "middle": 12, "ring": 16, "pinky": 20}
PIPS = {"thumb": 2, "index": 6, "middle": 10, "ring": 14, "pinky": 18}

HELD_FRAMES = 8        # tuned default
SWIPE_DX = 0.18        # tuned default
SMOOTH_ALPHA = 0.5     # tuned default
SWIPE_WINDOW = 0.5
SWIPE_COOLDOWN = 0.7


class Pt:
    __slots__ = ("x", "y")
    def __init__(self, x, y): self.x, self.y = x, y


def _d(a, b): return ((a.x - b.x) ** 2 + (a.y - b.y) ** 2) ** 0.5


def finger_extended(lm, name):
    w = lm[WRIST]
    return _d(lm[TIPS[name]], w) > _d(lm[PIPS[name]], w) * 1.05


def pose_of(lm):
    """Returns a token. Uses the 4 main fingers (thumb ignored for pose):
       OPEN_PALM = all 4 out, FIST = all 4 curled, POINT_<DIR> = index only,
       else OTHER."""
    idx = finger_extended(lm, "index")
    mid = finger_extended(lm, "middle")
    rng = finger_extended(lm, "ring")
    pky = finger_extended(lm, "pinky")
    main = sum((idx, mid, rng, pky))

    if main >= 4:
        return "OPEN_PALM"
    if main == 0:
        return "FIST"
    if idx and not mid and not rng and not pky:
        return f"POINT_{point_dir(lm)}"
    return "OTHER"


def point_dir(lm):
    """Direction the index finger points: tip relative to its knuckle.
       Frame is mirrored, so pointing physically right reads as RIGHT."""
    tip, mcp = lm[TIPS["index"]], lm[PIPS["index"]]
    dx, dy = tip.x - mcp.x, tip.y - mcp.y
    if abs(dx) >= abs(dy):
        return "RIGHT" if dx > 0 else "LEFT"
    return "DOWN" if dy > 0 else "UP"


def emit(msg):
    print(msg, flush=True)


def main():
    cam = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    if not os.path.exists(MODEL):
        emit("ERROR:missing model"); return

    detector = vision.HandLandmarker.create_from_options(
        vision.HandLandmarkerOptions(
            base_options=BaseOptions(model_asset_path=MODEL),
            running_mode=vision.RunningMode.IMAGE,
            num_hands=1, min_hand_detection_confidence=0.6, min_tracking_confidence=0.5))

    cap = cv2.VideoCapture(cam, cv2.CAP_DSHOW)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280); cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
    if not cap.isOpened():
        emit(f"ERROR:camera {cam} unavailable"); return
    emit("READY")

    stable = candidate = "NONE"
    count = 0
    smoothed = None
    track = []
    last_swipe = 0.0

    try:
        while True:
            ok, frame = cap.read()
            if not ok:
                emit("ERROR:camera read failed"); break
            frame = cv2.flip(frame, 1)
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            res = detector.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb))
            now = time.time()

            if res.hand_landmarks:
                raw = [Pt(p.x, p.y) for p in res.hand_landmarks[0]]
                if smoothed is not None and len(smoothed) == len(raw):
                    smoothed = [Pt(SMOOTH_ALPHA * r.x + (1 - SMOOTH_ALPHA) * s.x,
                                   SMOOTH_ALPHA * r.y + (1 - SMOOTH_ALPHA) * s.y)
                                for r, s in zip(raw, smoothed)]
                else:
                    smoothed = raw
                lm = smoothed

                p = pose_of(lm)
                if p == candidate: count += 1
                else: candidate, count = p, 1
                if count == HELD_FRAMES and candidate != stable and candidate != "OTHER":
                    stable = candidate
                    emit(f"GESTURE:{stable}")

                track.append((now, lm[WRIST].x))
                track[:] = [(t, x) for (t, x) in track if now - t <= SWIPE_WINDOW]
                if p == "OPEN_PALM" and len(track) >= 4 and now - last_swipe > SWIPE_COOLDOWN:
                    dx = track[-1][1] - track[0][1]
                    if dx > SWIPE_DX:
                        emit("GESTURE:SWIPE_RIGHT"); last_swipe = now; track.clear()
                    elif dx < -SWIPE_DX:
                        emit("GESTURE:SWIPE_LEFT"); last_swipe = now; track.clear()
            else:
                candidate, count, stable = "NONE", 0, "NONE"
                smoothed = None; track.clear()
    finally:
        cap.release(); detector.close()


if __name__ == "__main__":
    main()
