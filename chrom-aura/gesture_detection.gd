class_name GestureDetection
extends RefCounted

## Immutable-by-convention snapshot emitted by HandGestureEngine.

var track_id := -1
var gesture: StringName = &""
var score := 0.0
var anchor_uv := Vector2.ZERO
var palm_center_uv := Vector2.ZERO
var direction_uv := Vector2.ZERO
var handedness: StringName = HandPose.UNKNOWN_HAND
var timestamp_ms := 0


func _init(
	p_track_id: int = -1,
	p_gesture: StringName = &"",
	p_score: float = 0.0,
	p_anchor_uv: Vector2 = Vector2.ZERO,
	p_palm_center_uv: Vector2 = Vector2.ZERO,
	p_direction_uv: Vector2 = Vector2.ZERO,
	p_handedness: StringName = HandPose.UNKNOWN_HAND,
	p_timestamp_ms: int = 0,
) -> void:
	track_id = p_track_id
	gesture = p_gesture
	score = p_score
	anchor_uv = p_anchor_uv
	palm_center_uv = p_palm_center_uv
	direction_uv = p_direction_uv
	handedness = p_handedness
	timestamp_ms = p_timestamp_ms
