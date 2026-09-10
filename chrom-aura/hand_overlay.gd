class_name HandOverlay
extends Control

const HAND_CONNECTIONS := [
	0, 1, 1, 2, 2, 3, 3, 4,
	0, 5, 5, 6, 6, 7, 7, 8,
	5, 9, 9, 10, 10, 11, 11, 12,
	9, 13, 13, 14, 14, 15, 15, 16,
	13, 17, 0, 17, 17, 18, 18, 19, 19, 20,
]
const LANDMARK_COLOR := Color(1.0, 0.2, 0.35)
const CONNECTION_COLOR := Color(0.1, 1.0, 0.45)
const LANDMARK_RADIUS := 5.0
const CONNECTION_WIDTH := 3.0
const ACTIVE_COLOR := Color(1.0, 0.82, 0.15)

var _poses: Array[HandPose] = []
var _source_size := Vector2i(640, 480)


func show_hand_poses(poses: Array[HandPose], source_size: Vector2i) -> void:
	_poses = poses
	_source_size = source_size
	queue_redraw()


func _notification(what: int) -> void:
	if what == NOTIFICATION_RESIZED:
		queue_redraw()


func _draw() -> void:
	if _source_size.x <= 0 or _source_size.y <= 0:
		return

	var target := _content_rect()
	for pose in _poses:
		_draw_hand(pose.landmarks_2d, target)
		_draw_pose_diagnostics(pose, target)


func _draw_hand(hand: PackedVector2Array, target: Rect2) -> void:
	for connection_index in range(0, HAND_CONNECTIONS.size(), 2):
		var from_index: int = HAND_CONNECTIONS[connection_index]
		var to_index: int = HAND_CONNECTIONS[connection_index + 1]
		if from_index < hand.size() and to_index < hand.size():
			draw_line(
				_to_screen(hand[from_index], target),
				_to_screen(hand[to_index], target),
				CONNECTION_COLOR,
				CONNECTION_WIDTH,
				true,
			)

	for landmark in hand:
		draw_circle(_to_screen(landmark, target), LANDMARK_RADIUS, LANDMARK_COLOR)


func _draw_pose_diagnostics(pose: HandPose, target: Rect2) -> void:
	var palm_position := _to_screen(pose.palm_center_uv, target)
	var caption := "#%d %s" % [pose.track_id, pose.handedness]
	if not pose.gesture.is_empty():
		caption += "  %s %.0f%%" % [pose.gesture, pose.gesture_score * 100.0]
	draw_string(
		ThemeDB.fallback_font,
		palm_position + Vector2(12.0, -12.0),
		caption,
		HORIZONTAL_ALIGNMENT_LEFT,
		-1.0,
		16,
		ACTIVE_COLOR,
	)

	if pose.gesture == HandGestureEngine.INDEX_POINTING:
		_draw_anchor(
			pose.landmarks_2d[HandGestureEngine.INDEX_TIP],
			pose.landmarks_2d[HandGestureEngine.INDEX_TIP]
			- pose.landmarks_2d[HandGestureEngine.INDEX_DIP],
			target,
		)
	elif pose.gesture == HandGestureEngine.INDEX_MIDDLE_POINTING:
		var index_direction := (
			pose.landmarks_2d[HandGestureEngine.INDEX_TIP]
			- pose.landmarks_2d[HandGestureEngine.INDEX_DIP]
		).normalized()
		var middle_direction := (
			pose.landmarks_2d[HandGestureEngine.MIDDLE_TIP]
			- pose.landmarks_2d[HandGestureEngine.MIDDLE_DIP]
		).normalized()
		_draw_anchor(
			pose.landmarks_2d[HandGestureEngine.INDEX_TIP],
			index_direction + middle_direction,
			target,
		)
	elif pose.gesture == HandGestureEngine.THUMB_UP:
		_draw_anchor(
			pose.landmarks_2d[HandGestureEngine.THUMB_TIP],
			pose.landmarks_2d[HandGestureEngine.THUMB_TIP]
			- pose.landmarks_2d[HandGestureEngine.THUMB_IP],
			target,
		)
	elif pose.gesture == HandGestureEngine.FINGER_GUN:
		var index_tip: Vector2 = pose.landmarks_2d[HandGestureEngine.INDEX_TIP]
		var middle_tip: Vector2 = pose.landmarks_2d[HandGestureEngine.MIDDLE_TIP]
		var index_direction := (
			index_tip - pose.landmarks_2d[HandGestureEngine.INDEX_DIP]
		).normalized()
		var middle_direction := (
			middle_tip - pose.landmarks_2d[HandGestureEngine.MIDDLE_DIP]
		).normalized()
		var anchor: Vector2
		var direction: Vector2
		if index_direction.dot(middle_direction) > 0.5:
			anchor = (index_tip + middle_tip) * 0.5
			direction = index_direction + middle_direction
		else:
			anchor = index_tip
			direction = index_direction
		_draw_anchor(anchor, direction, target)
	elif pose.gesture == HandGestureEngine.FINGER_HEART:
		var heart_anchor := (
			pose.landmarks_2d[HandGestureEngine.THUMB_TIP]
			+ pose.landmarks_2d[HandGestureEngine.INDEX_TIP]
		) * 0.5
		_draw_anchor(heart_anchor, Vector2.UP, target)


func _draw_anchor(anchor_uv: Vector2, direction_uv: Vector2, target: Rect2) -> void:
	var anchor := _to_screen(anchor_uv, target)
	var direction := direction_uv.normalized() * 55.0
	draw_circle(anchor, LANDMARK_RADIUS * 1.8, ACTIVE_COLOR, false, 3.0, true)
	draw_line(anchor, anchor + direction, ACTIVE_COLOR, 4.0, true)


func _content_rect() -> Rect2:
	var source_aspect := float(_source_size.x) / float(_source_size.y)
	var control_aspect := size.x / maxf(size.y, 1.0)
	var rendered_size := size
	var origin := Vector2.ZERO

	if control_aspect > source_aspect:
		rendered_size.x = size.y * source_aspect
		origin.x = (size.x - rendered_size.x) * 0.5
	else:
		rendered_size.y = size.x / source_aspect
		origin.y = (size.y - rendered_size.y) * 0.5

	return Rect2(origin, rendered_size)


func _to_screen(normalized_point: Vector2, target: Rect2) -> Vector2:
	return target.position + normalized_point * target.size
