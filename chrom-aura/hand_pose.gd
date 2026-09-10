class_name HandPose
extends RefCounted

## Smoothed state of one tracked hand.

const LANDMARK_COUNT := 21
const UNKNOWN_HAND: StringName = &"UNKNOWN"
const PALM_LANDMARKS := [0, 5, 9, 13, 17]

var track_id := -1
var landmarks_2d := PackedVector2Array()
var landmarks_3d := PackedVector3Array()
var handedness: StringName = UNKNOWN_HAND
var handedness_score := 0.0
var palm_center_uv := Vector2.ZERO
var palm_scale_uv := 0.0
var velocity_uv := Vector2.ZERO
var gesture: StringName = &""
var gesture_score := 0.0
var timestamp_ms := 0


func update_geometry() -> void:
	palm_center_uv = Vector2.ZERO
	var point_count := 0
	for landmark_index in PALM_LANDMARKS:
		if landmark_index < landmarks_2d.size():
			palm_center_uv += landmarks_2d[landmark_index]
			point_count += 1
	if point_count > 0:
		palm_center_uv /= float(point_count)

	if landmarks_2d.size() > 17:
		palm_scale_uv = landmarks_2d[5].distance_to(landmarks_2d[17])
	else:
		palm_scale_uv = 0.0
