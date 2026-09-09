# ChromAura

## Hand gesture API

MediaPipe is used only to extract the 21 two-dimensional and three-dimensional
hand landmarks. Gesture classification, multi-hand tracking and temporal
stabilization are implemented by `HandGestureEngine` in Godot.

The engine currently recognizes `INDEX_POINTING`, `INDEX_MIDDLE_POINTING` and
`THUMB_UP`, and reports them through these signals:

```gdscript
@onready var gestures: HandGestureEngine = $HandGestureEngine

func _ready() -> void:
	gestures.gesture_started.connect(_on_gesture_started)
	gestures.gesture_updated.connect(_on_gesture_updated)
	gestures.gesture_ended.connect(_on_gesture_ended)

func _on_gesture_started(detection: GestureDetection) -> void:
	# anchor_uv is normalized in the camera image (0..1).
	start_effect(detection.track_id, detection.gesture, detection.anchor_uv)
```

`GestureDetection` also exposes the gesture score, palm center, pointing
direction, handedness and source timestamp. Effects should use `track_id` as
their stable per-hand key.

Run the deterministic gesture tests with:

```bash
godot --headless --path chrom-aura \
  --script res://tests/test_hand_gesture_engine.gd
```

## Camera source

The Kinect is the default source and provides both the RGB stream used by
MediaPipe and the depth stream used by the particle renderer. For webcam-only
development, select the root `Control` node in `main.tscn` and change
**Camera > Camera Source** from `KINECT` to `WEBCAM`. No code or scene
connection needs to be changed.
