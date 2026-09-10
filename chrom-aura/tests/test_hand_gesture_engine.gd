extends SceneTree

var _failures: Array[String] = []


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var engine := HandGestureEngine.new()
	root.add_child(engine)
	_test_static_classification(engine)
	_test_temporal_events(engine)
	_test_gun_temporal_events(engine)
	_test_thumb_down_temporal_events(engine)
	_test_track_association(engine)
	_test_heart_events(engine)
	engine.free()

	if _failures.is_empty():
		print("HandGestureEngine tests: PASS")
		quit(0)
	else:
		for failure in _failures:
			push_error(failure)
		print("HandGestureEngine tests: FAIL (%d)" % _failures.size())
		quit(1)


func _test_static_classification(engine: HandGestureEngine) -> void:
	var pointing := _make_pose(HandGestureEngine.INDEX_POINTING)
	var pointing_scores := engine.classify_pose(pointing)
	_expect_score(
		pointing_scores,
		HandGestureEngine.INDEX_POINTING,
		engine.activation_threshold,
		"pointing index",
	)

	for angle_degrees in [90.0, 180.0, 270.0]:
		var rotated := _rotate_pose(pointing, deg_to_rad(angle_degrees))
		var rotated_scores := engine.classify_pose(rotated)
		_expect_score(
			rotated_scores,
			HandGestureEngine.INDEX_POINTING,
			engine.activation_threshold,
			"pointing index rotated by %.0f degrees" % angle_degrees,
		)

	var thumb_up := _make_pose(HandGestureEngine.THUMB_UP)
	var thumb_scores := engine.classify_pose(thumb_up)
	_expect_score(thumb_scores, HandGestureEngine.THUMB_UP, engine.activation_threshold, "thumb up")
	var horizontal_thumb := _rotate_pose(thumb_up, deg_to_rad(90.0))
	var horizontal_thumb_scores := engine.classify_pose(horizontal_thumb)
	_expect_below(
		horizontal_thumb_scores,
		HandGestureEngine.THUMB_UP,
		engine.maintenance_threshold,
		"horizontal thumb as thumb up",
	)

	var thumb_down := _make_pose(HandGestureEngine.THUMB_DOWN)
	var thumb_down_scores := engine.classify_pose(thumb_down)
	_expect_score(thumb_down_scores, HandGestureEngine.THUMB_DOWN, engine.activation_threshold, "thumb down")
	_expect_below(
		thumb_down_scores,
		HandGestureEngine.THUMB_UP,
		engine.maintenance_threshold,
		"thumb down as thumb up",
	)
	_expect_below(
		thumb_scores,
		HandGestureEngine.THUMB_DOWN,
		engine.maintenance_threshold,
		"thumb up as thumb down",
	)
	var horizontal_thumb_down := _rotate_pose(thumb_down, deg_to_rad(90.0))
	var horizontal_down_scores := engine.classify_pose(horizontal_thumb_down)
	_expect_below(
		horizontal_down_scores,
		HandGestureEngine.THUMB_DOWN,
		engine.maintenance_threshold,
		"horizontal thumb as thumb down",
	)

	var two_finger_pointing := _make_pose(HandGestureEngine.INDEX_MIDDLE_POINTING)
	var two_finger_scores := engine.classify_pose(two_finger_pointing)
	_expect_score(
		two_finger_scores,
		HandGestureEngine.INDEX_MIDDLE_POINTING,
		engine.activation_threshold,
		"two-finger pointing",
	)
	_expect_below(
		two_finger_scores,
		HandGestureEngine.INDEX_POINTING,
		engine.maintenance_threshold,
		"two-finger pointing as index pointing",
	)
	var pointing_with_thumb_up := _make_pose(HandGestureEngine.INDEX_MIDDLE_POINTING)
	var points_with_thumb_up := pointing_with_thumb_up.landmarks_3d
	_set_thumb_up(points_with_thumb_up)
	pointing_with_thumb_up.landmarks_3d = points_with_thumb_up
	pointing_with_thumb_up.landmarks_2d = _world_to_uv(points_with_thumb_up)
	pointing_with_thumb_up.update_geometry()
	var thumb_independent_scores := engine.classify_pose(pointing_with_thumb_up)
	_expect_score(
		thumb_independent_scores,
		HandGestureEngine.INDEX_MIDDLE_POINTING,
		engine.activation_threshold,
		"two-finger pointing with thumb up",
	)
	_expect_below(
		thumb_independent_scores,
		HandGestureEngine.THUMB_UP,
		engine.maintenance_threshold,
		"pointing with thumb up as thumb up",
	)
	_expect_below(
		thumb_independent_scores,
		HandGestureEngine.THUMB_DOWN,
		engine.maintenance_threshold,
		"pointing with thumb up as thumb down",
	)

	var open_palm := _make_pose(&"OPEN_PALM")
	var open_scores := engine.classify_pose(open_palm)
	_expect_below(
		open_scores,
		HandGestureEngine.INDEX_POINTING,
		engine.maintenance_threshold,
		"open palm as pointing",
	)
	_expect_below(
		open_scores,
		HandGestureEngine.THUMB_UP,
		engine.maintenance_threshold,
		"open palm as thumb up",
	)
	_expect_below(
		open_scores,
		HandGestureEngine.FINGER_GUN,
		engine.maintenance_threshold,
		"open palm as finger gun",
	)
	_expect_below(
		open_scores,
		HandGestureEngine.THUMB_DOWN,
		engine.maintenance_threshold,
		"open palm as thumb down",
	)

	var gun_left := _make_gun_pose(true)
	var gun_left_scores := engine.classify_pose(gun_left)
	_expect_score(
		gun_left_scores,
		HandGestureEngine.FINGER_GUN,
		engine.activation_threshold,
		"finger gun pointing left",
	)
	_expect_below(
		gun_left_scores,
		HandGestureEngine.THUMB_UP,
		engine.maintenance_threshold,
		"finger gun left as thumb up",
	)
	_expect_below(
		gun_left_scores,
		HandGestureEngine.THUMB_DOWN,
		engine.maintenance_threshold,
		"finger gun left as thumb down",
	)

	var gun_right := _make_gun_pose(false)
	var gun_right_scores := engine.classify_pose(gun_right)
	_expect_score(
		gun_right_scores,
		HandGestureEngine.FINGER_GUN,
		engine.activation_threshold,
		"finger gun pointing right",
	)
	_expect_below(
		gun_right_scores,
		HandGestureEngine.THUMB_UP,
		engine.maintenance_threshold,
		"finger gun right as thumb up",
	)
	_expect_below(
		gun_right_scores,
		HandGestureEngine.THUMB_DOWN,
		engine.maintenance_threshold,
		"finger gun right as thumb down",
	)

	# 1-finger pointing (single index) must NOT be recognized as finger gun
	var gun_one_finger := _make_gun_pose(true, false)
	var gun_one_scores := engine.classify_pose(gun_one_finger)
	_expect_below(
		gun_one_scores,
		HandGestureEngine.FINGER_GUN,
		engine.maintenance_threshold,
		"single finger pointing rejected as finger gun",
	)

	# Permissive finger gun without thumb up (2-finger index + middle pointing horizontal)
	var gun_relaxed_thumb_left := _make_gun_pose(true, true, false)
	var gun_relaxed_thumb_left_scores := engine.classify_pose(gun_relaxed_thumb_left)
	_expect_score(
		gun_relaxed_thumb_left_scores,
		HandGestureEngine.FINGER_GUN,
		engine.activation_threshold,
		"2-finger gun without thumb up pointing left",
	)

	var gun_relaxed_thumb_right := _make_gun_pose(false, true, false)
	var gun_relaxed_thumb_right_scores := engine.classify_pose(gun_relaxed_thumb_right)
	_expect_score(
		gun_relaxed_thumb_right_scores,
		HandGestureEngine.FINGER_GUN,
		engine.activation_threshold,
		"2-finger gun without thumb up pointing right",
	)

	_expect_below(
		pointing_scores,
		HandGestureEngine.FINGER_GUN,
		engine.maintenance_threshold,
		"vertical pointing as finger gun",
	)
	_expect_below(
		thumb_scores,
		HandGestureEngine.FINGER_GUN,
		engine.maintenance_threshold,
		"thumb up as finger gun",
	)

	var finger_heart := _make_finger_heart_pose()
	var finger_heart_scores := engine.classify_pose(finger_heart)
	_expect_score(
		finger_heart_scores,
		HandGestureEngine.FINGER_HEART,
		engine.activation_threshold,
		"finger heart pose",
	)
	_expect_below(
		finger_heart_scores,
		HandGestureEngine.INDEX_POINTING,
		engine.maintenance_threshold,
		"finger heart rejected as index pointing",
	)
	_expect_below(
		pointing_scores,
		HandGestureEngine.FINGER_HEART,
		engine.maintenance_threshold,
		"pointing rejected as finger heart",
	)


func _test_temporal_events(engine: HandGestureEngine) -> void:
	engine.clear()
	var started: Array[GestureDetection] = []
	var updated: Array[GestureDetection] = []
	var ended: Array[GestureDetection] = []
	var lost: Array[int] = []
	engine.gesture_started.connect(
		func(detection: GestureDetection) -> void: started.append(detection)
	)
	engine.gesture_updated.connect(
		func(detection: GestureDetection) -> void: updated.append(detection)
	)
	engine.gesture_ended.connect(
		func(detection: GestureDetection) -> void: ended.append(detection)
	)
	engine.hand_lost.connect(func(track_id: int) -> void: lost.append(track_id))

	var observation := _observation_from_pose(_make_pose(HandGestureEngine.INDEX_POINTING))
	var observations: Array[HandObservation] = [observation]
	var no_observations: Array[HandObservation] = []
	engine.process_observations(observations, 0)
	engine.process_observations(observations, 60)
	_assert(started.is_empty(), "gesture activated before the 120 ms dwell")
	engine.process_observations(observations, 130)
	_assert(started.size() == 1, "gesture_started should be emitted exactly once")
	if not started.is_empty():
		_assert(started[0].gesture == HandGestureEngine.INDEX_POINTING, "wrong gesture_started payload")
		_assert(
			started[0].anchor_uv.is_equal_approx(observation.landmarks_2d[8]),
			"pointing anchor is not the index tip",
		)
	engine.process_observations(observations, 160)
	_assert(not updated.is_empty(), "gesture_updated was not emitted for an active gesture")

	engine.process_observations(no_observations, 200)
	engine.process_observations(no_observations, 390)
	_assert(ended.size() == 1, "gesture_ended should be emitted once after the release delay")
	engine.process_observations(no_observations, 470)
	_assert(lost.size() == 1, "hand_lost should be emitted after the tracking timeout")


func _test_track_association(engine: HandGestureEngine) -> void:
	engine.clear()
	var left_pose := _translated_pose(
		_make_pose(HandGestureEngine.INDEX_POINTING), Vector2(-0.18, 0.0)
	)
	var right_pose := _translated_pose(
		_make_pose(HandGestureEngine.INDEX_POINTING), Vector2(0.18, 0.0)
	)
	var ordered_observations: Array[HandObservation] = [
		_observation_from_pose(left_pose),
		_observation_from_pose(right_pose),
	]
	engine.process_observations(ordered_observations, 0)
	var first_poses := engine.get_hand_poses()
	_assert(first_poses.size() == 2, "two hand tracks were not created")
	if first_poses.size() != 2:
		return
	var left_id := _leftmost_track_id(first_poses)
	var reversed_observations: Array[HandObservation] = [
		_observation_from_pose(right_pose),
		_observation_from_pose(left_pose),
	]
	engine.process_observations(reversed_observations, 33)
	var second_poses := engine.get_hand_poses()
	var associated_left_id := _leftmost_track_id(second_poses)
	_assert(left_id == associated_left_id, "track id changed when MediaPipe hand order changed")


func _make_pose(gesture: StringName) -> HandPose:
	var points := PackedVector3Array()
	points.resize(HandPose.LANDMARK_COUNT)
	points[0] = Vector3(0.0, -0.65, 0.0)
	points[1] = Vector3(-0.35, -0.15, 0.0)
	points[2] = Vector3(-0.62, 0.0, 0.0)
	points[3] = Vector3(-0.52, 0.18, 0.0)
	points[4] = Vector3(-0.30, 0.08, 0.0)
	_set_finger(
		points,
		5,
		gesture == HandGestureEngine.INDEX_POINTING
		or gesture == HandGestureEngine.INDEX_MIDDLE_POINTING
		or gesture == &"OPEN_PALM",
	)
	_set_finger(
		points,
		9,
		gesture == HandGestureEngine.INDEX_MIDDLE_POINTING or gesture == &"OPEN_PALM",
	)
	_set_finger(points, 13, gesture == &"OPEN_PALM")
	_set_finger(points, 17, gesture == &"OPEN_PALM")

	if gesture == HandGestureEngine.THUMB_UP:
		_set_thumb_up(points)
	elif gesture == HandGestureEngine.THUMB_DOWN:
		_set_thumb_down(points)

	var pose := HandPose.new()
	pose.landmarks_3d = points
	pose.landmarks_2d = _world_to_uv(points)
	pose.handedness = &"RIGHT"
	pose.handedness_score = 0.99
	pose.update_geometry()
	return pose


func _set_thumb_up(points: PackedVector3Array) -> void:
	points[1] = Vector3(-0.42, -0.12, 0.0)
	points[2] = Vector3(-0.42, 0.28, 0.0)
	points[3] = Vector3(-0.42, 0.68, 0.0)
	points[4] = Vector3(-0.42, 1.10, 0.0)


func _set_thumb_down(points: PackedVector3Array) -> void:
	points[1] = Vector3(-0.42, 0.12, 0.0)
	points[2] = Vector3(-0.42, -0.28, 0.0)
	points[3] = Vector3(-0.42, -0.68, 0.0)
	points[4] = Vector3(-0.42, -1.10, 0.0)


func _set_finger(points: PackedVector3Array, mcp_index: int, extended: bool) -> void:
	var x_positions := {5: -0.38, 9: -0.12, 13: 0.16, 17: 0.42}
	var x: float = x_positions[mcp_index]
	points[mcp_index] = Vector3(x, 0.0, 0.0)
	if extended:
		points[mcp_index + 1] = Vector3(x, 0.48, 0.0)
		points[mcp_index + 2] = Vector3(x, 0.92, 0.0)
		points[mcp_index + 3] = Vector3(x, 1.35, 0.0)
	else:
		points[mcp_index + 1] = Vector3(x, 0.45, 0.0)
		points[mcp_index + 2] = Vector3(x + 0.25, 0.20, 0.04)
		points[mcp_index + 3] = Vector3(x + 0.10, 0.02, 0.03)


func _world_to_uv(points: PackedVector3Array) -> PackedVector2Array:
	var result := PackedVector2Array()
	for point in points:
		result.append(Vector2(0.5 + point.x * 0.16, 0.55 - point.y * 0.16))
	return result


func _rotate_pose(source: HandPose, angle: float) -> HandPose:
	var pose := HandPose.new()
	for point in source.landmarks_3d:
		pose.landmarks_3d.append(point.rotated(Vector3.FORWARD, angle))
	pose.landmarks_2d = _world_to_uv(pose.landmarks_3d)
	pose.handedness = source.handedness
	pose.handedness_score = source.handedness_score
	pose.update_geometry()
	return pose


func _translated_pose(source: HandPose, offset: Vector2) -> HandPose:
	var pose := HandPose.new()
	pose.landmarks_3d = source.landmarks_3d
	for point in source.landmarks_2d:
		pose.landmarks_2d.append(point + offset)
	pose.handedness = source.handedness
	pose.handedness_score = source.handedness_score
	pose.update_geometry()
	return pose


func _observation_from_pose(pose: HandPose) -> HandObservation:
	var observation := HandObservation.new()
	observation.landmarks_2d = pose.landmarks_2d
	observation.landmarks_3d = pose.landmarks_3d
	observation.handedness = pose.handedness
	observation.handedness_score = pose.handedness_score
	return observation


func _leftmost_track_id(poses: Array[HandPose]) -> int:
	if poses[0].palm_center_uv.x < poses[1].palm_center_uv.x:
		return poses[0].track_id
	return poses[1].track_id


func _expect_score(
	scores: Dictionary[StringName, float],
	gesture: StringName,
	minimum: float,
	context: String,
) -> void:
	var score: float = scores.get(gesture, 0.0)
	_assert(score >= minimum, "%s score %.3f is below %.3f" % [context, score, minimum])


func _expect_below(
	scores: Dictionary[StringName, float],
	gesture: StringName,
	maximum: float,
	context: String,
) -> void:
	var score: float = scores.get(gesture, 0.0)
	_assert(score < maximum, "%s score %.3f is not below %.3f" % [context, score, maximum])


func _assert(condition: bool, message: String) -> void:
	if not condition:
		_failures.append(message)


func _test_gun_temporal_events(engine: HandGestureEngine) -> void:
	engine.clear()
	var started: Array[GestureDetection] = []
	var updated: Array[GestureDetection] = []
	var ended: Array[GestureDetection] = []
	engine.gesture_started.connect(func(detection: GestureDetection) -> void: started.append(detection))
	engine.gesture_updated.connect(func(detection: GestureDetection) -> void: updated.append(detection))
	engine.gesture_ended.connect(func(detection: GestureDetection) -> void: ended.append(detection))

	var observation := _observation_from_pose(_make_gun_pose(true))
	var observations: Array[HandObservation] = [observation]
	var no_observations: Array[HandObservation] = []

	engine.process_observations(observations, 0)
	engine.process_observations(observations, 60)
	_assert(started.is_empty(), "gun activated before 120 ms dwell")
	engine.process_observations(observations, 130)
	_assert(started.size() == 1, "gun gesture_started should be emitted exactly once")
	if not started.is_empty():
		_assert(started[0].gesture == HandGestureEngine.FINGER_GUN, "wrong gesture_started for gun")
		_assert(started[0].direction_uv.x < -0.4, "gun pointing left direction x is not negative")
	engine.process_observations(observations, 160)
	_assert(not updated.is_empty(), "gesture_updated was not emitted for active gun")

	engine.process_observations(no_observations, 200)
	engine.process_observations(no_observations, 390)
	_assert(ended.size() == 1, "gun gesture_ended should be emitted after release delay")

	# Test right-pointing gun temporal activation
	engine.clear()
	started.clear()
	var right_obs := _observation_from_pose(_make_gun_pose(false))
	var right_obs_arr: Array[HandObservation] = [right_obs]
	engine.process_observations(right_obs_arr, 0)
	engine.process_observations(right_obs_arr, 130)
	_assert(started.size() == 1, "gun pointing right gesture_started should be emitted")
	if not started.is_empty():
		_assert(started[0].gesture == HandGestureEngine.FINGER_GUN, "wrong gesture for right gun")
		_assert(started[0].direction_uv.x > 0.4, "gun pointing right direction x is not positive")

	# Test relaxed thumb 2-finger gun (index + middle) temporal activation
	engine.clear()
	started.clear()
	var relaxed_obs := _observation_from_pose(_make_gun_pose(true, true, false))
	var relaxed_obs_arr: Array[HandObservation] = [relaxed_obs]
	engine.process_observations(relaxed_obs_arr, 0)
	engine.process_observations(relaxed_obs_arr, 130)
	_assert(started.size() == 1, "relaxed thumb gun gesture_started should be emitted")
	if not started.is_empty():
		_assert(started[0].gesture == HandGestureEngine.FINGER_GUN, "wrong gesture for relaxed thumb gun")
		_assert(started[0].direction_uv.x < -0.4, "relaxed thumb gun pointing left direction x is not negative")

	# Test 1-finger horizontal pointing does NOT activate gun
	engine.clear()
	started.clear()
	var single_finger_obs := _observation_from_pose(_make_gun_pose(true, false, false))
	var single_finger_arr: Array[HandObservation] = [single_finger_obs]
	engine.process_observations(single_finger_arr, 0)
	engine.process_observations(single_finger_arr, 130)
	if not started.is_empty():
		_assert(started[0].gesture != HandGestureEngine.FINGER_GUN, "1-finger pointing must NOT activate FINGER_GUN")


func _make_gun_pose(pointing_left: bool, two_fingers: bool = true, thumb_up: bool = true) -> HandPose:
	var points := PackedVector3Array()
	points.resize(HandPose.LANDMARK_COUNT)
	var sign := -1.0 if pointing_left else 1.0

	# Palm / wrist: fingers extend along X (sign)
	points[0] = Vector3(-sign * 0.65, 0.0, 0.0)
	if thumb_up:
		points[1] = Vector3(-sign * 0.20, 0.15, 0.0)
		points[2] = Vector3(-sign * 0.20, 0.50, 0.0)
		points[3] = Vector3(-sign * 0.20, 0.85, 0.0)
		points[4] = Vector3(-sign * 0.20, 1.25, 0.0)
	else:
		# Relaxed / curled thumb (index pointing horizontal style)
		points[1] = Vector3(-sign * 0.25, 0.0, 0.0)
		points[2] = Vector3(-sign * 0.35, 0.08, 0.0)
		points[3] = Vector3(-sign * 0.25, 0.12, 0.04)
		points[4] = Vector3(-sign * 0.10, 0.08, 0.03)

	# Index finger extended along X
	points[5] = Vector3(sign * 0.0, 0.20, 0.0)
	points[6] = Vector3(sign * 0.45, 0.20, 0.0)
	points[7] = Vector3(sign * 0.90, 0.20, 0.0)
	points[8] = Vector3(sign * 1.35, 0.20, 0.0)

	# Middle finger: extended (2-finger gun) or folded (1-finger gun)
	if two_fingers:
		points[9] = Vector3(sign * 0.0, -0.05, 0.0)
		points[10] = Vector3(sign * 0.45, -0.05, 0.0)
		points[11] = Vector3(sign * 0.90, -0.05, 0.0)
		points[12] = Vector3(sign * 1.35, -0.05, 0.0)
	else:
		points[9] = Vector3(sign * 0.0, -0.05, 0.0)
		points[10] = Vector3(sign * 0.35, -0.05, 0.0)
		points[11] = Vector3(sign * 0.20, 0.05, 0.04)
		points[12] = Vector3(sign * 0.05, 0.10, 0.03)

	# Ring finger folded
	points[13] = Vector3(sign * 0.0, -0.28, 0.0)
	points[14] = Vector3(sign * 0.35, -0.28, 0.0)
	points[15] = Vector3(sign * 0.20, -0.15, 0.04)
	points[16] = Vector3(sign * 0.05, -0.10, 0.03)

	# Pinky finger folded
	points[17] = Vector3(sign * 0.0, -0.50, 0.0)
	points[18] = Vector3(sign * 0.30, -0.50, 0.0)
	points[19] = Vector3(sign * 0.18, -0.40, 0.04)
	points[20] = Vector3(sign * 0.04, -0.32, 0.03)

	var pose := HandPose.new()
	pose.landmarks_3d = points
	pose.landmarks_2d = _world_to_uv(points)
	pose.handedness = &"RIGHT" if not pointing_left else &"LEFT"
	pose.handedness_score = 0.99
	pose.update_geometry()
	return pose


func _make_finger_heart_pose() -> HandPose:
	var points := PackedVector3Array()
	points.resize(HandPose.LANDMARK_COUNT)
	points[0] = Vector3(0.0, -0.65, 0.0)
	points[1] = Vector3(-0.35, -0.15, 0.0)
	points[2] = Vector3(-0.45, 0.05, 0.0)
	points[3] = Vector3(-0.38, 0.22, 0.0)
	points[4] = Vector3(-0.28, 0.32, 0.0)

	points[5] = Vector3(-0.38, 0.0, 0.0)
	points[6] = Vector3(-0.38, 0.35, 0.0)
	points[7] = Vector3(-0.32, 0.36, 0.02)
	points[8] = Vector3(-0.27, 0.31, 0.02)

	_set_finger(points, 9, false)
	_set_finger(points, 13, false)
	_set_finger(points, 17, false)

	var pose := HandPose.new()
	pose.landmarks_3d = points
	pose.landmarks_2d = _world_to_uv(points)
	pose.handedness = &"RIGHT"
	pose.handedness_score = 0.99
	pose.update_geometry()
	return pose


func _test_thumb_down_temporal_events(engine: HandGestureEngine) -> void:
	engine.clear()
	var started: Array[GestureDetection] = []
	var updated: Array[GestureDetection] = []
	var ended: Array[GestureDetection] = []
	engine.gesture_started.connect(func(detection: GestureDetection) -> void: started.append(detection))
	engine.gesture_updated.connect(func(detection: GestureDetection) -> void: updated.append(detection))
	engine.gesture_ended.connect(func(detection: GestureDetection) -> void: ended.append(detection))

	var down_obs := _observation_from_pose(_make_pose(HandGestureEngine.THUMB_DOWN))
	var observations: Array[HandObservation] = [down_obs]
	var no_obs: Array[HandObservation] = []

	engine.process_observations(observations, 0)
	engine.process_observations(observations, 60)
	_assert(started.is_empty(), "thumb down activated before dwell delay")
	engine.process_observations(observations, 130)
	_assert(started.size() == 1, "thumb down gesture_started should be emitted once")
	if not started.is_empty():
		_assert(started[0].gesture == HandGestureEngine.THUMB_DOWN, "wrong gesture for thumb down")
		_assert(started[0].direction_uv.y > 0.4, "thumb down direction y should be positive")
	engine.process_observations(observations, 160)
	_assert(not updated.is_empty(), "gesture_updated was not emitted for thumb down")

	engine.process_observations(no_obs, 200)
	engine.process_observations(no_obs, 390)
	_assert(ended.size() == 1, "thumb down gesture_ended should be emitted")

	# Test transition: THUMB_UP to THUMB_DOWN (Gladiator Flip)
	engine.clear()
	started.clear()
	ended.clear()
	var up_obs := _observation_from_pose(_make_pose(HandGestureEngine.THUMB_UP))
	engine.process_observations([up_obs], 0)
	engine.process_observations([up_obs], 130)
	_assert(started.size() == 1 and started[0].gesture == HandGestureEngine.THUMB_UP, "thumb up should activate")

	# Flip to THUMB_DOWN
	engine.process_observations([down_obs], 260)
	engine.process_observations([down_obs], 390)
	_assert(started.size() == 2, "thumb down should activate after flip")
	if started.size() >= 2:
		_assert(started[1].gesture == HandGestureEngine.THUMB_DOWN, "second activated gesture must be THUMB_DOWN")


func _test_heart_events(engine: HandGestureEngine) -> void:
	# Test 1: Single hand FINGER_HEART temporal events
	engine.clear()
	var started: Array[GestureDetection] = []
	var updated: Array[GestureDetection] = []
	var ended: Array[GestureDetection] = []
	engine.gesture_started.connect(func(d: GestureDetection) -> void: started.append(d))
	engine.gesture_updated.connect(func(d: GestureDetection) -> void: updated.append(d))
	engine.gesture_ended.connect(func(d: GestureDetection) -> void: ended.append(d))

	var heart_pose := _make_finger_heart_pose()
	var heart_obs: Array[HandObservation] = [_observation_from_pose(heart_pose)]

	engine.process_observations(heart_obs, 0)
	_assert(started.is_empty(), "finger heart started before activation delay")

	engine.process_observations(heart_obs, engine.activation_delay_ms + 10)
	_assert(started.size() == 1, "finger heart started should be emitted once")
	if not started.is_empty():
		_assert(started[0].gesture == HandGestureEngine.FINGER_HEART, "wrong gesture_started for finger heart")

	engine.process_observations(heart_obs, engine.activation_delay_ms + 50)
	_assert(not updated.is_empty(), "finger heart updated should be emitted")

	var no_obs: Array[HandObservation] = []
	engine.process_observations(no_obs, 200)
	_assert(ended.is_empty(), "finger heart ended before release delay")

	engine.process_observations(no_obs, 390)
	_assert(ended.size() == 1, "finger heart ended should be emitted after release delay")

	# Test 2: TWO_HAND_HEART temporal events
	engine.clear()
	started.clear()
	updated.clear()
	ended.clear()

	var left_pose := _make_pose(HandGestureEngine.INDEX_POINTING)
	left_pose.handedness = &"LEFT"
	left_pose.landmarks_2d[HandGestureEngine.INDEX_TIP] = Vector2(0.49, 0.45)
	left_pose.landmarks_2d[HandGestureEngine.THUMB_TIP] = Vector2(0.49, 0.60)
	left_pose.landmarks_2d[HandGestureEngine.INDEX_MCP] = Vector2(0.42, 0.50)
	left_pose.landmarks_2d[HandGestureEngine.PINKY_MCP] = Vector2(0.35, 0.55)
	left_pose.update_geometry()

	var right_pose := _make_pose(HandGestureEngine.INDEX_POINTING)
	right_pose.handedness = &"RIGHT"
	right_pose.landmarks_2d[HandGestureEngine.INDEX_TIP] = Vector2(0.51, 0.45)
	right_pose.landmarks_2d[HandGestureEngine.THUMB_TIP] = Vector2(0.51, 0.60)
	right_pose.landmarks_2d[HandGestureEngine.INDEX_MCP] = Vector2(0.58, 0.50)
	right_pose.landmarks_2d[HandGestureEngine.PINKY_MCP] = Vector2(0.65, 0.55)
	right_pose.update_geometry()

	var two_hand_obs: Array[HandObservation] = [
		_observation_from_pose(left_pose),
		_observation_from_pose(right_pose),
	]

	engine.process_observations(two_hand_obs, 0)
	var two_starts := started.filter(func(d: GestureDetection) -> bool: return d.gesture == HandGestureEngine.TWO_HAND_HEART)
	_assert(two_starts.is_empty(), "TWO_HAND_HEART started before activation delay")

	engine.process_observations(two_hand_obs, engine.activation_delay_ms + 10)
	two_starts = started.filter(func(d: GestureDetection) -> bool: return d.gesture == HandGestureEngine.TWO_HAND_HEART)
	_assert(two_starts.size() == 1, "TWO_HAND_HEART started should be emitted once")

	engine.process_observations(two_hand_obs, engine.activation_delay_ms + 50)
	var two_updates := updated.filter(func(d: GestureDetection) -> bool: return d.gesture == HandGestureEngine.TWO_HAND_HEART)
	_assert(not two_updates.is_empty(), "TWO_HAND_HEART updated should be emitted")

	# Hands leave frame
	engine.process_observations(no_obs, 200)
	var two_ends := ended.filter(func(d: GestureDetection) -> bool: return d.gesture == HandGestureEngine.TWO_HAND_HEART)
	_assert(two_ends.is_empty(), "TWO_HAND_HEART ended before release delay")

	engine.process_observations(no_obs, 390)
	two_ends = ended.filter(func(d: GestureDetection) -> bool: return d.gesture == HandGestureEngine.TWO_HAND_HEART)
	_assert(two_ends.size() == 1, "TWO_HAND_HEART ended should be emitted after release delay")
