"""
BorderDock gesture spike — detection + LIVE TUNING.

Detects OPEN_PALM, FIST, SWIPE_LEFT / SWIPE_RIGHT. Tune the feel while it runs:

  [ / ]   hold time  (frames a pose must hold before it fires) - / +
  - / =   swipe sensitivity (smaller = easier to trigger)
  , / .   smoothing strength (jitter reduction) - / +
  s       toggle smoothing on/off
  q       quit

The HUD shows the current values + live gesture. Find values that feel right;
we then bake them into the real integration. Uses the MediaPipe Tasks API.
"""

import argparse
import os
import time
import cv2
import mediapipe as mp
from mediapipe.tasks.python import vision, BaseOptions

MODEL = os.path.join(os.path.dirname(os.path.abspath(__file__)), "hand_landmarker.task")

WRIST = 0
TIPS = {"thumb": 4, "index": 8, "middle": 12, "ring": 16, "pinky": 20}
PIPS = {"thumb": 2, "index": 6, "middle": 10, "ring": 14, "pinky": 18}

CONNECTIONS = [
    (0, 1), (1, 2), (2, 3), (3, 4), (0, 5), (5, 6), (6, 7), (7, 8),
    (5, 9), (9, 10), (10, 11), (11, 12), (9, 13), (13, 14), (14, 15), (15, 16),
    (13, 17), (17, 18), (18, 19), (19, 20), (0, 17),
]


class Pt:
    __slots__ = ("x", "y")
    def __init__(self, x, y): self.x, self.y = x, y


def _dist(a, b):
    return ((a.x - b.x) ** 2 + (a.y - b.y) ** 2) ** 0.5


def extended_fingers(lm):
    w = lm[WRIST]
    return sum(1 for k in TIPS if _dist(lm[TIPS[k]], w) > _dist(lm[PIPS[k]], w) * 1.05)


def classify_pose(lm):
    n = extended_fingers(lm)
    if n >= 4: return "OPEN_PALM", n
    if n <= 1: return "FIST", n
    return "OTHER", n


def draw_hand(frame, lm, w, h):
    pts = [(int(p.x * w), int(p.y * h)) for p in lm]
    for a, b in CONNECTIONS:
        cv2.line(frame, pts[a], pts[b], (0, 180, 0), 2)
    for (x, y) in pts:
        cv2.circle(frame, (x, y), 4, (0, 255, 0), -1)


def hud(frame, lines):
    y = 34
    for text, color in lines:
        cv2.putText(frame, text, (10, y), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 0), 4, cv2.LINE_AA)
        cv2.putText(frame, text, (10, y), cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 1, cv2.LINE_AA)
        y += 30


def main(cam_index):
    if not os.path.exists(MODEL):
        print(f"ERROR: missing model {MODEL}."); return

    detector = vision.HandLandmarker.create_from_options(
        vision.HandLandmarkerOptions(
            base_options=BaseOptions(model_asset_path=MODEL),
            running_mode=vision.RunningMode.IMAGE,
            num_hands=1, min_hand_detection_confidence=0.6, min_tracking_confidence=0.5))

    cap = cv2.VideoCapture(cam_index, cv2.CAP_DSHOW)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280); cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
    if not cap.isOpened():
        print(f"ERROR: could not open camera {cam_index}. Try --cam 1."); return

    # --- tunables (adjust live; defaults are a reasonable starting point) ---
    held_frames = 8            # [ ]
    swipe_dx = 0.18            # - =
    smooth_alpha = 0.5         # , .   (1.0 = no smoothing, lower = smoother/laggier)
    smoothing = True           # s
    swipe_window = 0.5
    swipe_cooldown = 0.7

    stable_pose = candidate = "NONE"
    candidate_count = 0
    smoothed = None
    palm_track = []
    last_swipe = 0.0
    fps_t, fps_n, fps = time.time(), 0, 0.0

    print("Gesture detector (live tuning). Keys: [ ] hold, - = swipe, , . smooth, s toggle, q quit.")
    while True:
        ok, frame = cap.read()
        if not ok: print("ERROR: camera read failed."); break
        frame = cv2.flip(frame, 1)
        h, w = frame.shape[:2]
        rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
        result = detector.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb))
        now = time.time()

        fps_n += 1
        if now - fps_t >= 0.5: fps = fps_n / (now - fps_t); fps_t, fps_n = now, 0

        label, fingers = "no hand", 0
        if result.hand_landmarks:
            raw = [Pt(p.x, p.y) for p in result.hand_landmarks[0]]
            if smoothing and smoothed is not None and len(smoothed) == len(raw):
                smoothed = [Pt(smooth_alpha * r.x + (1 - smooth_alpha) * s.x,
                               smooth_alpha * r.y + (1 - smooth_alpha) * s.y)
                            for r, s in zip(raw, smoothed)]
            else:
                smoothed = raw
            lm = smoothed

            pose, fingers = classify_pose(lm)
            label = pose

            if pose == candidate: candidate_count += 1
            else: candidate, candidate_count = pose, 1
            if candidate_count == held_frames and candidate != stable_pose and candidate != "OTHER":
                stable_pose = candidate
                print(f"[{time.strftime('%H:%M:%S')}] GESTURE: {stable_pose}")

            # swipe: only while open palm, with a cooldown so it fires once per flick
            palm_track.append((now, lm[WRIST].x))
            palm_track[:] = [(t, x) for (t, x) in palm_track if now - t <= swipe_window]
            if pose == "OPEN_PALM" and len(palm_track) >= 4 and now - last_swipe > swipe_cooldown:
                dx = palm_track[-1][1] - palm_track[0][1]
                if dx > swipe_dx:
                    print(f"[{time.strftime('%H:%M:%S')}] GESTURE: SWIPE_RIGHT"); last_swipe = now; palm_track.clear()
                elif dx < -swipe_dx:
                    print(f"[{time.strftime('%H:%M:%S')}] GESTURE: SWIPE_LEFT"); last_swipe = now; palm_track.clear()

            draw_hand(frame, lm, w, h)
        else:
            candidate, candidate_count, stable_pose = "NONE", 0, "NONE"
            smoothed = None; palm_track.clear()

        approx_ms = int(held_frames * 1000 / fps) if fps > 0 else 0
        hud(frame, [
            (f"{label}  ({fingers} fingers)", (0, 255, 0)),
            (f"hold [ ]: {held_frames}f (~{approx_ms}ms)   swipe - =: {swipe_dx:.2f}", (255, 255, 0)),
            (f"smooth , . : {smooth_alpha:.2f}  ({'ON' if smoothing else 'OFF'}, key s)   fps {fps:.0f}", (255, 255, 0)),
            ("q to quit", (200, 200, 200)),
        ])
        cv2.imshow("BorderDock gesture spike", frame)

        k = cv2.waitKey(1) & 0xFF
        if k == ord("q"): break
        elif k == ord("["): held_frames = max(2, held_frames - 1)
        elif k == ord("]"): held_frames = min(40, held_frames + 1)
        elif k == ord("-"): swipe_dx = max(0.05, round(swipe_dx - 0.02, 2))
        elif k == ord("="): swipe_dx = min(0.5, round(swipe_dx + 0.02, 2))
        elif k == ord(","): smooth_alpha = max(0.1, round(smooth_alpha - 0.05, 2))
        elif k == ord("."): smooth_alpha = min(1.0, round(smooth_alpha + 0.05, 2))
        elif k == ord("s"): smoothing = not smoothing

    cap.release(); cv2.destroyAllWindows(); detector.close()
    print(f"\nFinal tuning — hold:{held_frames}f  swipe:{swipe_dx:.2f}  smooth:{smooth_alpha:.2f} ({'on' if smoothing else 'off'})")
    print("Tell me these values and I'll bake them into the integration.")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--cam", type=int, default=0)
    args = ap.parse_args()
    main(args.cam)
