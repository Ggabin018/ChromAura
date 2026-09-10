class_name HandGestureEngine
extends Node

## Tracks MediaPipe landmarks and emits stable, geometry-based gestures.
## Timestamps passed to process_observations() must be monotonic milliseconds.

## Emitted once after a gesture remains above the activation threshold.
signal gesture_started(detection: GestureDetection)
## Emitted for each observation while a gesture remains active.
signal gesture_updated(detection: GestureDetection)
## Emitted once when a gesture is released or its hand track expires.
signal gesture_ended(detection: GestureDetection)
## Emitted when a hand has not been observed for lost_track_timeout_ms.
signal hand_lost(track_id: int)

const INDEX_POINTING: StringName = &"INDEX_POINTING"
const INDEX_MIDDLE_POINTING: StringName = &"INDEX_MIDDLE_POINTING"
const THUMB_UP: StringName = &"THUMB_UP"
const FINGER_GUN: StringName = &"FINGER_GUN"
const FINGER_HEART: StringName = &"FINGER_HEART"
const TWO_HAND_HEART: StringName = &"TWO_HAND_HEART"
const TWO_HAND_HEART_TRACK_ID := 999

const WRIST := 0
const THUMB_CMC := 1
const THUMB_MCP := 2
const THUMB_IP := 3
const THUMB_TIP := 4
const INDEX_MCP := 5
const INDEX_PIP := 6
const INDEX_DIP := 7
const INDEX_TIP := 8
const MIDDLE_MCP := 9
const MIDDLE_PIP := 10
const MIDDLE_DIP := 11
const MIDDLE_TIP := 12
const RING_MCP := 13
const RING_PIP := 14
const RING_DIP := 15
const RING_TIP := 16
const PINKY_MCP := 17
const PINKY_PIP := 18
const PINKY_DIP := 19
const PINKY_TIP := 20
const PALM_INDICES := [WRIST, INDEX_MCP, MIDDLE_MCP, RING_MCP, PINKY_MCP]
const NEW_TRACK_ID := -1
const MAX_ASSOCIATION_COST := 2.5
const HANDEDNESS_MISMATCH_COST := 0.75
const MIN_PALM_SCALE_UV := 0.02

@export_group("Gesture Hysteresis")
@export_range(0.0, 1.0, 0.01) var activation_threshold := 0.78
@export_range(0.0, 1.0, 0.01) var maintenance_threshold := 0.60
@export_range(0, 1000, 10) var activation_delay_ms := 120
@export_range(0, 1000, 10) var release_delay_ms := 180
@export_range(0.0, 0.5, 0.01) var switch_margin := 0.08

@export_group("Tracking")
@export_range(0, 2000, 10) var lost_track_timeout_ms := 300

@export_group("Smoothing")
@export_range(0.1, 20.0, 0.1) var smoothing_min_cutoff := 2.5
@export_range(0.0, 2.0, 0.01) var smoothing_speed_factor := 0.35


class TrackState extends RefCounted:
	var pose: HandPose
	var last_seen_ms := 0
	var active_gesture: StringName = &""
	var active_score := 0.0
	var candidate_gesture: StringName = &""
	var candidate_since_ms := -1
	var release_since_ms := -1


class PoseAssignment extends RefCounted:
	var cost := 0.0
	var track_id := NEW_TRACK_ID
	var observation_index := -1


var _tracks: Dictionary[int, TrackState] = {}
var _next_track_id := 1

var _two_hand_heart_active := false
var _two_hand_heart_score := 0.0
var _two_hand_heart_anchor := Vector2.ZERO
var _two_hand_heart_candidate := false
var _two_hand_heart_candidate_since_ms := -1
var _two_hand_heart_release_since_ms := -1


func process_observations(observations: Array[HandObservation], timestamp_ms: int) -> void:
	var raw_poses: Array[HandPose] = []
	for observation in observations:
		if observation != null and observation.is_valid():
			raw_poses.append(_pose_from_observation(observation, timestamp_ms))

	var assignments := _associate_poses(raw_poses, timestamp_ms)
	var seen_track_ids: Dictionary[int, bool] = {}
	for assignment in assignments:
		var track_id := assignment.track_id
		var observation_index := assignment.observation_index
		var track: TrackState
		if track_id == NEW_TRACK_ID:
			track = TrackState.new()
			track_id = _next_track_id
			_next_track_id += 1
			raw_poses[observation_index].track_id = track_id
			track.pose = raw_poses[observation_index]
			track.last_seen_ms = timestamp_ms
			_tracks[track_id] = track
		else:
			track = _tracks[track_id]
			track.pose = _smooth_pose(track.pose, raw_poses[observation_index], timestamp_ms)
			track.pose.track_id = track_id
			track.last_seen_ms = timestamp_ms
		seen_track_ids[track_id] = true
		_update_gesture(track, timestamp_ms)

	var expired_track_ids: Array[int] = []
	for track_id in _tracks:
		if seen_track_ids.has(track_id):
			continue
		var track: TrackState = _tracks[track_id]
		_update_missing_track(track, timestamp_ms)
		if timestamp_ms - track.last_seen_ms >= lost_track_timeout_ms:
			if not track.active_gesture.is_empty():
				gesture_ended.emit(
					_make_detection(
						track,
						track.active_gesture,
						track.active_score,
						timestamp_ms,
					)
				)
			hand_lost.emit(track_id)
			expired_track_ids.append(track_id)
	for track_id in expired_track_ids:
		_tracks.erase(track_id)

	_evaluate_two_hand_heart(timestamp_ms)


## Returns live pose references for rendering. Consumers must treat them as read-only.
func get_hand_poses() -> Array[HandPose]:
	var poses: Array[HandPose] = []
	var track_ids := _tracks.keys()
	track_ids.sort()
	for track_id in track_ids:
		poses.append((_tracks[track_id] as TrackState).pose)
	return poses


## Returns immutable-by-convention snapshots of all currently active gestures.
func get_active_detections(timestamp_ms: int = -1) -> Array[GestureDetection]:
	var detections: Array[GestureDetection] = []
	var track_ids := _tracks.keys()
	track_ids.sort()
	for track_id in track_ids:
		var state := _tracks[track_id] as TrackState
		if not state.active_gesture.is_empty():
			var event_timestamp := timestamp_ms if timestamp_ms >= 0 else state.pose.timestamp_ms
			detections.append(
				_make_detection(
					state,
					state.active_gesture,
					state.active_score,
					event_timestamp,
				)
			)
	if _two_hand_heart_active:
		var event_timestamp := timestamp_ms if timestamp_ms >= 0 else 0
		detections.append(
			GestureDetection.new(
				TWO_HAND_HEART_TRACK_ID,
				TWO_HAND_HEART,
				_two_hand_heart_score,
				_two_hand_heart_anchor,
				_two_hand_heart_anchor,
				Vector2.UP,
				HandPose.UNKNOWN_HAND,
				event_timestamp,
			)
		)
	return detections


func classify_pose(pose: HandPose) -> Dictionary[StringName, float]:
	if (
		pose.landmarks_2d.size() != HandPose.LANDMARK_COUNT
		or pose.landmarks_3d.size() != HandPose.LANDMARK_COUNT
	):
		return {}
	var landmarks := _landmarks_in_hand_space(pose.landmarks_3d)
	if landmarks.size() != HandPose.LANDMARK_COUNT:
		return {}

	var index_extended := _finger_extended_score(
		landmarks, INDEX_MCP, INDEX_PIP, INDEX_DIP, INDEX_TIP
	)
	var middle_folded := _finger_folded_score(
		landmarks, MIDDLE_MCP, MIDDLE_PIP, MIDDLE_DIP, MIDDLE_TIP
	)
	var middle_extended := _finger_extended_score(
		landmarks, MIDDLE_MCP, MIDDLE_PIP, MIDDLE_DIP, MIDDLE_TIP
	)
	var ring_folded := _finger_folded_score(
		landmarks, RING_MCP, RING_PIP, RING_DIP, RING_TIP
	)
	var pinky_folded := _finger_folded_score(
		landmarks, PINKY_MCP, PINKY_PIP, PINKY_DIP, PINKY_TIP
	)
	var non_index_folded := minf(middle_folded, minf(ring_folded, pinky_folded))
	var pointing_score := 0.55 * index_extended + 0.45 * non_index_folded
	var two_finger_extension := minf(index_extended, middle_extended)
	var remaining_fingers_folded := minf(ring_folded, pinky_folded)
	var two_finger_pointing_score := (
		0.65 * two_finger_extension + 0.35 * remaining_fingers_folded
	)

	var thumb_extended := _thumb_extended_score(landmarks)
	var index_folded := _finger_folded_score(
		landmarks, INDEX_MCP, INDEX_PIP, INDEX_DIP, INDEX_TIP
	)
	var all_fingers_folded := minf(index_folded, non_index_folded)
	var thumb_direction := (pose.landmarks_2d[THUMB_TIP] - pose.landmarks_2d[THUMB_IP]).normalized()
	var upward_alignment := thumb_direction.dot(Vector2.UP)
	var upward_score := _smoothstep(cos(deg_to_rad(55.0)), cos(deg_to_rad(35.0)), upward_alignment)
	var thumb_shape_score := 0.50 * thumb_extended + 0.50 * all_fingers_folded
	var thumb_up_score := thumb_shape_score * (0.45 + 0.55 * upward_score)

	# Finger Gun requires INDEX and MIDDLE fingers extended (Index Middle Pointing gun)
	var two_finger_gun_extension := minf(index_extended, middle_extended)
	var ring_pinky_folded := 0.50 * ring_folded + 0.50 * pinky_folded
	var gun_shape := (
		0.60 * two_finger_gun_extension
		+ 0.40 * ring_pinky_folded
	)
	if two_finger_gun_extension < 0.45 or ring_pinky_folded < 0.35:
		gun_shape *= 0.10

	var index_delta: Vector2 = pose.landmarks_2d[INDEX_TIP] - pose.landmarks_2d[INDEX_MCP]
	var index_dir_2d: Vector2 = index_delta.normalized() if not index_delta.is_zero_approx() else Vector2.RIGHT
	var middle_delta: Vector2 = pose.landmarks_2d[MIDDLE_TIP] - pose.landmarks_2d[MIDDLE_MCP]
	var middle_dir_2d: Vector2 = middle_delta.normalized() if not middle_delta.is_zero_approx() else index_dir_2d
	var barrel_combined: Vector2 = index_dir_2d + middle_dir_2d
	var barrel_dir_2d: Vector2 = (
		barrel_combined.normalized() if not barrel_combined.is_zero_approx() else index_dir_2d
	)

	var horizontal_alignment := _smoothstep(0.15, 0.40, absf(barrel_dir_2d.x))

	# Thumb bonus: if thumb is cocked upward, it boosts score, but gun does NOT require it
	var thumb_delta: Vector2 = pose.landmarks_2d[THUMB_TIP] - pose.landmarks_2d[THUMB_MCP]
	var thumb_dir_2d: Vector2 = thumb_delta.normalized() if not thumb_delta.is_zero_approx() else Vector2.UP
	var thumb_upward := thumb_dir_2d.dot(Vector2.UP)
	var thumb_cocked := thumb_extended * _smoothstep(-0.25, 0.25, thumb_upward)
	var thumb_bonus := 0.05 * clampf(thumb_cocked, 0.0, 1.0)

	var finger_gun_score := (
		gun_shape * horizontal_alignment * 0.94
		+ thumb_bonus
	)
	finger_gun_score = clampf(finger_gun_score, 0.0, 1.0)

	if finger_gun_score >= 0.70:
		two_finger_pointing_score = minf(two_finger_pointing_score, finger_gun_score - 0.10)

	# Finger Heart (Korean mini heart: thumb & index cross/pinch, middle/ring/pinky folded)
	var pinch_dist := landmarks[THUMB_TIP].distance_to(landmarks[INDEX_TIP])
	var pinch_score := 1.0 - _smoothstep(0.18, 0.55, pinch_dist)
	var index_curl_score := 1.0 - _smoothstep(0.40, 0.78, index_extended)
	var finger_heart_score := (
		0.45 * pinch_score
		+ 0.40 * non_index_folded
		+ 0.15 * index_curl_score
	)
	if pinch_dist > 0.60 or non_index_folded < 0.35:
		finger_heart_score *= 0.10
	finger_heart_score = clampf(finger_heart_score, 0.0, 1.0)

	if finger_heart_score >= 0.70:
		pointing_score = minf(pointing_score, 0.30)

	return {
		INDEX_POINTING: clampf(pointing_score, 0.0, 1.0),
		INDEX_MIDDLE_POINTING: clampf(two_finger_pointing_score, 0.0, 1.0),
		THUMB_UP: clampf(thumb_up_score, 0.0, 1.0),
		FINGER_GUN: clampf(finger_gun_score, 0.0, 1.0),
		FINGER_HEART: clampf(finger_heart_score, 0.0, 1.0),
	}


## Clears all tracks without emitting lifecycle signals. Intended for resets and tests.
func clear() -> void:
	_tracks.clear()
	_next_track_id = 1
	_two_hand_heart_active = false
	_two_hand_heart_score = 0.0
	_two_hand_heart_anchor = Vector2.ZERO
	_two_hand_heart_candidate = false
	_two_hand_heart_candidate_since_ms = -1
	_two_hand_heart_release_since_ms = -1


func _pose_from_observation(observation: HandObservation, timestamp_ms: int) -> HandPose:
	var pose := HandPose.new()
	pose.landmarks_2d = observation.landmarks_2d
	pose.landmarks_3d = observation.landmarks_3d
	pose.handedness = observation.handedness
	pose.handedness_score = observation.handedness_score
	pose.timestamp_ms = timestamp_ms
	pose.update_geometry()
	return pose


func _associate_poses(raw_poses: Array[HandPose], timestamp_ms: int) -> Array[PoseAssignment]:
	var pairs: Array[PoseAssignment] = []
	for track_id in _tracks:
		var track: TrackState = _tracks[track_id]
		if timestamp_ms - track.last_seen_ms >= lost_track_timeout_ms:
			continue
		var elapsed_seconds := maxf(
			float(timestamp_ms - track.last_seen_ms) / 1000.0,
			0.0,
		)
		var predicted_center := track.pose.palm_center_uv + track.pose.velocity_uv * elapsed_seconds
		for observation_index in raw_poses.size():
			var raw_pose := raw_poses[observation_index]
			var average_scale := maxf(
				(track.pose.palm_scale_uv + raw_pose.palm_scale_uv) * 0.5,
				MIN_PALM_SCALE_UV,
			)
			var position_cost := predicted_center.distance_to(raw_pose.palm_center_uv) / average_scale
			var scale_ratio := (
				maxf(raw_pose.palm_scale_uv, 0.001)
				/ maxf(track.pose.palm_scale_uv, 0.001)
			)
			var scale_cost := absf(log(scale_ratio)) * 0.25
			var handedness_cost := 0.0
			if (
				track.pose.handedness != HandPose.UNKNOWN_HAND
				and raw_pose.handedness != HandPose.UNKNOWN_HAND
				and track.pose.handedness != raw_pose.handedness
			):
				handedness_cost = HANDEDNESS_MISMATCH_COST
			var total_cost := position_cost + scale_cost + handedness_cost
			if total_cost <= MAX_ASSOCIATION_COST:
				var pair := PoseAssignment.new()
				pair.cost = total_cost
				pair.track_id = track_id
				pair.observation_index = observation_index
				pairs.append(pair)
	pairs.sort_custom(
		func(a: PoseAssignment, b: PoseAssignment) -> bool: return a.cost < b.cost
	)

	var used_tracks: Dictionary[int, bool] = {}
	var used_observations: Dictionary[int, bool] = {}
	var assignments: Array[PoseAssignment] = []
	for pair in pairs:
		if used_tracks.has(pair.track_id) or used_observations.has(pair.observation_index):
			continue
		used_tracks[pair.track_id] = true
		used_observations[pair.observation_index] = true
		assignments.append(pair)
	for observation_index in raw_poses.size():
		if not used_observations.has(observation_index):
			var assignment := PoseAssignment.new()
			assignment.observation_index = observation_index
			assignments.append(assignment)
	return assignments


func _smooth_pose(previous: HandPose, raw: HandPose, timestamp_ms: int) -> HandPose:
	var elapsed_seconds := maxf(
		float(timestamp_ms - previous.timestamp_ms) / 1000.0,
		0.001,
	)
	var palm_scale := maxf(
		(previous.palm_scale_uv + raw.palm_scale_uv) * 0.5,
		MIN_PALM_SCALE_UV,
	)
	var normalized_speed := (
		previous.palm_center_uv.distance_to(raw.palm_center_uv)
		/ palm_scale
		/ elapsed_seconds
	)
	var cutoff := smoothing_min_cutoff + smoothing_speed_factor * normalized_speed
	var alpha := 1.0 - exp(-TAU * cutoff * elapsed_seconds)
	alpha = clampf(alpha, 0.05, 1.0)

	var smoothed := HandPose.new()
	for landmark_index in raw.landmarks_2d.size():
		smoothed.landmarks_2d.append(
			previous.landmarks_2d[landmark_index].lerp(
				raw.landmarks_2d[landmark_index],
				alpha,
			)
		)
	for landmark_index in raw.landmarks_3d.size():
		smoothed.landmarks_3d.append(
			previous.landmarks_3d[landmark_index].lerp(
				raw.landmarks_3d[landmark_index],
				alpha,
			)
		)
	smoothed.handedness = raw.handedness
	smoothed.handedness_score = raw.handedness_score
	smoothed.timestamp_ms = timestamp_ms
	smoothed.update_geometry()
	var measured_velocity := (smoothed.palm_center_uv - previous.palm_center_uv) / elapsed_seconds
	smoothed.velocity_uv = previous.velocity_uv.lerp(measured_velocity, alpha)
	return smoothed


func _update_gesture(track: TrackState, timestamp_ms: int) -> void:
	var scores := classify_pose(track.pose)
	var best_gesture: StringName = &""
	var best_score := 0.0
	for gesture_variant in scores.keys():
		var score: float = scores[gesture_variant]
		if score > best_score:
			best_gesture = gesture_variant
			best_score = score

	if track.active_gesture.is_empty():
		track.pose.gesture = best_gesture
		track.pose.gesture_score = best_score
		_consider_candidate(track, best_gesture, best_score, timestamp_ms)
		var candidate_is_ready := (
			not track.candidate_gesture.is_empty()
			and timestamp_ms - track.candidate_since_ms >= activation_delay_ms
		)
		if candidate_is_ready:
			_activate_gesture(
				track,
				track.candidate_gesture,
				scores.get(track.candidate_gesture, best_score),
				timestamp_ms,
			)
		return

	var active_score: float = scores.get(track.active_gesture, 0.0)
	track.active_score = active_score
	track.pose.gesture = track.active_gesture
	track.pose.gesture_score = active_score
	var challenger_qualifies := (
		best_gesture != track.active_gesture
		and best_score >= activation_threshold
		and best_score >= active_score + switch_margin
	)
	if challenger_qualifies:
		_consider_candidate(track, best_gesture, best_score, timestamp_ms)
		if timestamp_ms - track.candidate_since_ms >= activation_delay_ms:
			gesture_ended.emit(_make_detection(track, track.active_gesture, active_score, timestamp_ms))
			_activate_gesture(track, best_gesture, best_score, timestamp_ms)
			return
	else:
		_clear_candidate(track)

	if active_score >= maintenance_threshold:
		track.release_since_ms = -1
	else:
		if track.release_since_ms < 0:
			track.release_since_ms = timestamp_ms
		elif timestamp_ms - track.release_since_ms >= release_delay_ms:
			gesture_ended.emit(_make_detection(track, track.active_gesture, active_score, timestamp_ms))
			track.active_gesture = &""
			track.active_score = 0.0
			track.pose.gesture = best_gesture
			track.pose.gesture_score = best_score
			track.release_since_ms = -1
			_consider_candidate(track, best_gesture, best_score, timestamp_ms)
			return
	gesture_updated.emit(_make_detection(track, track.active_gesture, active_score, timestamp_ms))


func _update_missing_track(track: TrackState, timestamp_ms: int) -> void:
	if track.active_gesture.is_empty():
		return
	if track.release_since_ms < 0:
		track.release_since_ms = timestamp_ms
	elif timestamp_ms - track.release_since_ms >= release_delay_ms:
		gesture_ended.emit(_make_detection(track, track.active_gesture, track.active_score, timestamp_ms))
		track.active_gesture = &""
		track.active_score = 0.0
		track.pose.gesture = &""
		track.pose.gesture_score = 0.0


func _consider_candidate(
	track: TrackState,
	gesture: StringName,
	score: float,
	timestamp_ms: int,
) -> void:
	if gesture.is_empty() or score < activation_threshold:
		_clear_candidate(track)
		return
	if track.candidate_gesture != gesture:
		track.candidate_gesture = gesture
		track.candidate_since_ms = timestamp_ms


func _clear_candidate(track: TrackState) -> void:
	track.candidate_gesture = &""
	track.candidate_since_ms = -1


func _activate_gesture(
	track: TrackState,
	gesture: StringName,
	score: float,
	timestamp_ms: int,
) -> void:
	track.active_gesture = gesture
	track.active_score = score
	track.pose.gesture = gesture
	track.pose.gesture_score = score
	track.release_since_ms = -1
	_clear_candidate(track)
	gesture_started.emit(_make_detection(track, gesture, score, timestamp_ms))


func _make_detection(
	track: TrackState,
	gesture: StringName,
	score: float,
	timestamp_ms: int,
) -> GestureDetection:
	var anchor := track.pose.palm_center_uv
	var direction := Vector2.ZERO
	if gesture == INDEX_POINTING:
		anchor = track.pose.landmarks_2d[INDEX_TIP]
		direction = (anchor - track.pose.landmarks_2d[INDEX_DIP]).normalized()
	elif gesture == INDEX_MIDDLE_POINTING:
		anchor = track.pose.landmarks_2d[INDEX_TIP]
		var index_direction := (
			track.pose.landmarks_2d[INDEX_TIP] - track.pose.landmarks_2d[INDEX_DIP]
		).normalized()
		var middle_direction := (
			track.pose.landmarks_2d[MIDDLE_TIP] - track.pose.landmarks_2d[MIDDLE_DIP]
		).normalized()
		direction = (index_direction + middle_direction).normalized()
	elif gesture == THUMB_UP:
		anchor = track.pose.landmarks_2d[THUMB_TIP]
		direction = (anchor - track.pose.landmarks_2d[THUMB_IP]).normalized()
	elif gesture == FINGER_GUN:
		var index_tip: Vector2 = track.pose.landmarks_2d[INDEX_TIP]
		var middle_tip: Vector2 = track.pose.landmarks_2d[MIDDLE_TIP]
		var index_dir := (index_tip - track.pose.landmarks_2d[INDEX_DIP]).normalized()
		var middle_dir := (middle_tip - track.pose.landmarks_2d[MIDDLE_DIP]).normalized()
		if index_dir.dot(middle_dir) > 0.5:
			anchor = (index_tip + middle_tip) * 0.5
			direction = (index_dir + middle_dir).normalized()
		else:
			anchor = index_tip
			direction = index_dir
		if direction.is_zero_approx():
			var fallback_dir: Vector2 = index_tip - track.pose.landmarks_2d[INDEX_MCP]
			direction = fallback_dir.normalized() if not fallback_dir.is_zero_approx() else Vector2.RIGHT
	elif gesture == FINGER_HEART:
		anchor = (track.pose.landmarks_2d[THUMB_TIP] + track.pose.landmarks_2d[INDEX_TIP]) * 0.5
		var thumb_dir := (track.pose.landmarks_2d[THUMB_TIP] - track.pose.landmarks_2d[THUMB_MCP]).normalized()
		var idx_dir := (track.pose.landmarks_2d[INDEX_TIP] - track.pose.landmarks_2d[INDEX_MCP]).normalized()
		direction = (thumb_dir + idx_dir).normalized()
		if direction.is_zero_approx():
			direction = Vector2.UP
	return GestureDetection.new(
		track.pose.track_id,
		gesture,
		score,
		anchor,
		track.pose.palm_center_uv,
		direction,
		track.pose.handedness,
		timestamp_ms,
	)


func _evaluate_two_hand_heart(timestamp_ms: int) -> void:
	var best_score := 0.0
	var best_anchor := Vector2.ZERO

	var track_ids := _tracks.keys()
	if track_ids.size() >= 2:
		for i in range(track_ids.size()):
			for j in range(i + 1, track_ids.size()):
				var track_a: TrackState = _tracks[track_ids[i]]
				var track_b: TrackState = _tracks[track_ids[j]]
				if (
					track_a.pose.landmarks_2d.size() != HandPose.LANDMARK_COUNT
					or track_b.pose.landmarks_2d.size() != HandPose.LANDMARK_COUNT
					or track_a.last_seen_ms != timestamp_ms
					or track_b.last_seen_ms != timestamp_ms
				):
					continue

				var avg_scale: float = maxf(
					(track_a.pose.palm_scale_uv + track_b.pose.palm_scale_uv) * 0.5,
					MIN_PALM_SCALE_UV,
				)
				var thumb_dist: float = track_a.pose.landmarks_2d[THUMB_TIP].distance_to(
					track_b.pose.landmarks_2d[THUMB_TIP]
				)
				var index_dist: float = track_a.pose.landmarks_2d[INDEX_TIP].distance_to(
					track_b.pose.landmarks_2d[INDEX_TIP]
				)

				var norm_thumb_dist := thumb_dist / avg_scale
				var norm_index_dist := index_dist / avg_scale

				var thumb_touch := 1.0 - _smoothstep(0.35, 1.45, norm_thumb_dist)
				var index_touch := 1.0 - _smoothstep(0.35, 1.45, norm_index_dist)

				var index_center := (
					track_a.pose.landmarks_2d[INDEX_TIP] + track_b.pose.landmarks_2d[INDEX_TIP]
				) * 0.5
				var thumb_center := (
					track_a.pose.landmarks_2d[THUMB_TIP] + track_b.pose.landmarks_2d[THUMB_TIP]
				) * 0.5
				# In UV space, Y goes downward. In a heart, index tips are higher (smaller Y) than thumbs.
				var vertical_delta := (thumb_center.y - index_center.y) / avg_scale
				var vertical_score := _smoothstep(0.10, 0.50, vertical_delta)

				var palm_dist := (
					track_a.pose.palm_center_uv.distance_to(track_b.pose.palm_center_uv) / avg_scale
				)
				var palm_proximity := 1.0 - _smoothstep(1.5, 4.5, palm_dist)

				var pair_score := (
					0.35 * index_touch
					+ 0.35 * thumb_touch
					+ 0.20 * vertical_score
					+ 0.10 * palm_proximity
				)
				if norm_thumb_dist > 1.65 or norm_index_dist > 1.65 or vertical_delta < 0.04:
					pair_score *= 0.10

				if pair_score > best_score:
					best_score = pair_score
					best_anchor = (index_center + thumb_center) * 0.5

	if not _two_hand_heart_active:
		if best_score >= activation_threshold:
			if not _two_hand_heart_candidate:
				_two_hand_heart_candidate = true
				_two_hand_heart_candidate_since_ms = timestamp_ms
			elif timestamp_ms - _two_hand_heart_candidate_since_ms >= activation_delay_ms:
				_two_hand_heart_active = true
				_two_hand_heart_score = best_score
				_two_hand_heart_anchor = best_anchor
				_two_hand_heart_candidate = false
				gesture_started.emit(
					GestureDetection.new(
						TWO_HAND_HEART_TRACK_ID,
						TWO_HAND_HEART,
						best_score,
						best_anchor,
						best_anchor,
						Vector2.UP,
						HandPose.UNKNOWN_HAND,
						timestamp_ms,
					)
				)
		else:
			_two_hand_heart_candidate = false
			_two_hand_heart_candidate_since_ms = -1
	else:
		_two_hand_heart_score = best_score
		if best_score >= maintenance_threshold:
			_two_hand_heart_anchor = best_anchor
			_two_hand_heart_release_since_ms = -1
			gesture_updated.emit(
				GestureDetection.new(
					TWO_HAND_HEART_TRACK_ID,
					TWO_HAND_HEART,
					best_score,
					best_anchor,
					best_anchor,
					Vector2.UP,
					HandPose.UNKNOWN_HAND,
					timestamp_ms,
				)
			)
		else:
			if _two_hand_heart_release_since_ms < 0:
				_two_hand_heart_release_since_ms = timestamp_ms
			elif timestamp_ms - _two_hand_heart_release_since_ms >= release_delay_ms:
				_two_hand_heart_active = false
				gesture_ended.emit(
					GestureDetection.new(
						TWO_HAND_HEART_TRACK_ID,
						TWO_HAND_HEART,
						_two_hand_heart_score,
						_two_hand_heart_anchor,
						_two_hand_heart_anchor,
						Vector2.UP,
						HandPose.UNKNOWN_HAND,
						timestamp_ms,
					)
				)
				_two_hand_heart_release_since_ms = -1


func _landmarks_in_hand_space(points: PackedVector3Array) -> PackedVector3Array:
	if points.size() != HandPose.LANDMARK_COUNT:
		return PackedVector3Array()
	var center := Vector3.ZERO
	for landmark_index in PALM_INDICES:
		center += points[landmark_index]
	center /= float(PALM_INDICES.size())
	var x_axis := (points[INDEX_MCP] - points[PINKY_MCP]).normalized()
	var y_hint := (points[MIDDLE_MCP] - points[WRIST]).normalized()
	var z_axis := x_axis.cross(y_hint).normalized()
	if x_axis.is_zero_approx() or y_hint.is_zero_approx() or z_axis.is_zero_approx():
		return PackedVector3Array()
	var y_axis := z_axis.cross(x_axis).normalized()
	var scale := maxf(points[INDEX_MCP].distance_to(points[PINKY_MCP]), 0.0001)
	var normalized := PackedVector3Array()
	for point in points:
		var relative := point - center
		var local_point := Vector3(
			relative.dot(x_axis),
			relative.dot(y_axis),
			relative.dot(z_axis),
		)
		normalized.append(local_point / scale)
	return normalized


func _finger_extended_score(
	points: PackedVector3Array,
	mcp: int,
	pip: int,
	dip: int,
	tip: int,
) -> float:
	var pip_straight := _straight_score(points[mcp], points[pip], points[dip])
	var dip_straight := _straight_score(points[pip], points[dip], points[tip])
	var palm_center := _normalized_palm_center(points)
	var reach_gain := points[tip].distance_to(palm_center) - points[pip].distance_to(palm_center)
	var reach_score := _smoothstep(0.05, 0.45, reach_gain)
	return 0.40 * pip_straight + 0.35 * dip_straight + 0.25 * reach_score


func _finger_folded_score(
	points: PackedVector3Array,
	mcp: int,
	pip: int,
	dip: int,
	tip: int,
) -> float:
	var pip_bent := 1.0 - _straight_score(points[mcp], points[pip], points[dip])
	var dip_bent := 1.0 - _straight_score(points[pip], points[dip], points[tip])
	var distance_to_palm := points[tip].distance_to(_normalized_palm_center(points))
	var close_score := 1.0 - _smoothstep(0.75, 1.35, distance_to_palm)
	return 0.40 * pip_bent + 0.25 * dip_bent + 0.35 * close_score


func _thumb_extended_score(points: PackedVector3Array) -> float:
	var mcp_straight := _straight_score(points[THUMB_CMC], points[THUMB_MCP], points[THUMB_IP])
	var ip_straight := _straight_score(points[THUMB_MCP], points[THUMB_IP], points[THUMB_TIP])
	var reach := points[THUMB_TIP].distance_to(_normalized_palm_center(points))
	var reach_score := _smoothstep(0.45, 1.0, reach)
	return 0.35 * mcp_straight + 0.35 * ip_straight + 0.30 * reach_score


func _normalized_palm_center(points: PackedVector3Array) -> Vector3:
	var center := Vector3.ZERO
	for landmark_index in PALM_INDICES:
		center += points[landmark_index]
	return center / float(PALM_INDICES.size())


func _straight_score(a: Vector3, b: Vector3, c: Vector3) -> float:
	var incoming := a - b
	var outgoing := c - b
	if incoming.is_zero_approx() or outgoing.is_zero_approx():
		return 0.0
	var angle_degrees := rad_to_deg(incoming.angle_to(outgoing))
	return _smoothstep(125.0, 170.0, angle_degrees)


func _smoothstep(edge0: float, edge1: float, value: float) -> float:
	var t := clampf((value - edge0) / maxf(edge1 - edge0, 0.000001), 0.0, 1.0)
	return t * t * (3.0 - 2.0 * t)
