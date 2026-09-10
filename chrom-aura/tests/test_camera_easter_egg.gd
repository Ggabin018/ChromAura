extends SceneTree

var _failures: Array[String] = []


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var camera_script: Script = load("res://camera_script.gd")
	var cam_node: Node = camera_script.new()
	var gesture_engine := HandGestureEngine.new()
	cam_node.gesture_engine = gesture_engine

	# Mock audio manager extending Node
	var mock_audio := MockAudio.new()
	cam_node.audio_manager = mock_audio

	# Case 1: Pure Gun pose (Index Middle Pointing) -> Must NOT trigger Wilhelm scream
	var gun_pose := _make_gun_pose(true)
	var gun_obs := _obs_from_pose(gun_pose)
	var gun_obs_arr: Array[HandObservation] = [gun_obs]
	cam_node._apply_hand_result(gun_obs_arr, 100)
	cam_node._apply_hand_result(gun_obs_arr, 250)
	if mock_audio.wilhelm_play_count > 0:
		_failures.append("Wilhelm scream triggered during gun pose!")
	if cam_node._track_thumb_up_times.has(1):
		_failures.append("Gun pose incorrectly recorded thumb up time for track")
	if cam_node._last_global_thumb_up_time_ms > 0:
		_failures.append("Gun pose incorrectly set global thumb up time")

	# Case 2: Gun pose tilting down -> Must NOT trigger Wilhelm scream
	var gun_tilted_down := _make_gun_pose(true)
	for i in range(gun_tilted_down.landmarks_3d.size()):
		gun_tilted_down.landmarks_3d[i] = gun_tilted_down.landmarks_3d[i].rotated(Vector3.FORWARD, deg_to_rad(-45.0))
	gun_tilted_down.landmarks_2d = _world_to_uv(gun_tilted_down.landmarks_3d)
	gun_tilted_down.update_geometry()
	var gun_down_obs := _obs_from_pose(gun_tilted_down)
	var gun_down_arr: Array[HandObservation] = [gun_down_obs]
	cam_node._apply_hand_result(gun_down_arr, 600)
	if mock_audio.wilhelm_play_count > 0:
		_failures.append("Wilhelm scream triggered during downward gun tilt!")

	# Case 3: Thumbs down ALONE -> Must NOT trigger Wilhelm scream
	var down_pose := _make_pose(HandGestureEngine.THUMB_DOWN)
	var down_obs := _obs_from_pose(down_pose)
	var down_arr: Array[HandObservation] = [down_obs]
	gesture_engine.clear()
	cam_node._track_thumb_up_times.clear()
	cam_node._last_global_thumb_up_time_ms = -999999
	cam_node._apply_hand_result(down_arr, 1000)
	cam_node._apply_hand_result(down_arr, 1200)
	if mock_audio.wilhelm_play_count > 0:
		_failures.append("Wilhelm scream triggered on solitary thumb down without preceding thumb up!")

	# Case 4: Thumbs up THEN gun -> Gun MUST reset pending thumb up, must NOT trigger
	var up_pose := _make_pose(HandGestureEngine.THUMB_UP)
	var up_obs := _obs_from_pose(up_pose)
	var up_arr: Array[HandObservation] = [up_obs]
	cam_node._apply_hand_result(up_arr, 2000)
	cam_node._apply_hand_result(up_arr, 2150)
	if not (cam_node._track_thumb_up_times.has(1) or cam_node._last_global_thumb_up_time_ms > 0):
		_failures.append("Valid thumb up failed to register thumb up time")

	# Now switch to gun -> should wipe pending thumb up
	cam_node._apply_hand_result(gun_obs_arr, 2400)
	if cam_node._last_global_thumb_up_time_ms > 0 or cam_node._track_thumb_up_times.has(1):
		_failures.append("Gun gesture did not reset pending thumb up timestamp")
	if mock_audio.wilhelm_play_count > 0:
		_failures.append("Gun gesture triggered Wilhelm scream after thumb up!")

	# Case 5: Legitimate sequence: Thumbs up (fist) THEN Thumbs down (fist) -> MUST trigger Wilhelm scream!
	gesture_engine.clear()
	cam_node._track_thumb_up_times.clear()
	cam_node._last_global_thumb_up_time_ms = -999999
	mock_audio.wilhelm_play_count = 0
	cam_node._last_wilhelm_easter_egg_ms = -999999

	# Thumbs up at 5000ms
	cam_node._apply_hand_result(up_arr, 5000)
	cam_node._apply_hand_result(up_arr, 5150)
	if mock_audio.wilhelm_play_count > 0:
		_failures.append("Wilhelm scream triggered prematurely during thumb up")

	# Flip to Thumbs down at 5600ms (within 200ms - 3000ms window)
	cam_node._apply_hand_result(down_arr, 5600)
	cam_node._apply_hand_result(down_arr, 5750)
	if mock_audio.wilhelm_play_count != 1:
		_failures.append("Wilhelm scream failed to trigger on valid Thumbs Up -> Thumbs Down sequence! Count: %d" % mock_audio.wilhelm_play_count)

	# Case 6: Immediate re-detection: Thumbs up -> Thumbs down directly after without 2.5s delay
	cam_node._apply_hand_result(up_arr, 6000)
	cam_node._apply_hand_result(up_arr, 6150)
	cam_node._apply_hand_result(down_arr, 6450)
	cam_node._apply_hand_result(down_arr, 6600)
	if mock_audio.wilhelm_play_count != 2:
		_failures.append("Wilhelm scream failed to trigger on immediate second sequence! Count: %d" % mock_audio.wilhelm_play_count)

	# Case 7: Rock and Roll gesture toggles drawing mode
	var mock_particles := MockParticles.new()
	cam_node.particles_layer = mock_particles
	gesture_engine.clear()

	var rock_pose := _make_pose(HandGestureEngine.ROCK_AND_ROLL)
	var rock_obs := _obs_from_pose(rock_pose)
	var rock_arr: Array[HandObservation] = [rock_obs]

	cam_node._apply_hand_result(rock_arr, 7000)
	cam_node._apply_hand_result(rock_arr, 7150)
	if mock_particles.toggle_drawing_count != 1:
		_failures.append("Rock and Roll gesture failed to toggle drawing mode! Count: %d" % mock_particles.toggle_drawing_count)

	# Should respect cooldown within 800ms
	cam_node._apply_hand_result(rock_arr, 7300)
	if mock_particles.toggle_drawing_count != 1:
		_failures.append("Rock and Roll gesture toggled without cooldown! Count: %d" % mock_particles.toggle_drawing_count)

	# After cooldown, should toggle again
	for t in [7500, 7700, 7900, 8100]:
		cam_node._apply_hand_result(rock_arr, t)
	if mock_particles.toggle_drawing_count != 2:
		_failures.append("Rock and Roll gesture failed to toggle second time after cooldown! Count: %d" % mock_particles.toggle_drawing_count)

	# Case 8: Face Palm gesture triggers body color change
	var palm_pose := _make_pose(HandGestureEngine.FACE_PALM)
	var palm_obs := _obs_from_pose(palm_pose)
	var palm_arr: Array[HandObservation] = [palm_obs]
	gesture_engine.clear()

	cam_node._apply_hand_result(palm_arr, 9000)
	cam_node._apply_hand_result(palm_arr, 9150)
	if mock_particles.color_change_count != 1:
		_failures.append("Face Palm gesture failed to trigger color change! Count: %d" % mock_particles.color_change_count)

	# Should respect cooldown
	cam_node._apply_hand_result(palm_arr, 9300)
	if mock_particles.color_change_count != 1:
		_failures.append("Face Palm gesture triggered color change within cooldown! Count: %d" % mock_particles.color_change_count)

	# After cooldown, should trigger again
	for t in [9500, 9700, 9900, 10000]:
		cam_node._apply_hand_result(palm_arr, t)
	if mock_particles.color_change_count != 2:
		_failures.append("Face Palm gesture failed to trigger second color change after cooldown! Count: %d" % mock_particles.color_change_count)

	gesture_engine.free()
	mock_audio.free()
	mock_particles.free()
	cam_node.free()

	if _failures.is_empty():
		print("CameraEasterEgg tests: PASS")
		quit(0)
	else:
		for failure in _failures:
			push_error(failure)
		print("CameraEasterEgg tests: FAIL (%d)" % _failures.size())
		quit(1)


func _make_gun_pose(pointing_left: bool) -> HandPose:
	var points := PackedVector3Array()
	points.resize(HandPose.LANDMARK_COUNT)
	var sign := -1.0 if pointing_left else 1.0

	points[0] = Vector3(-sign * 0.65, 0.0, 0.0)
	# Cocked thumb
	points[1] = Vector3(-sign * 0.20, 0.15, 0.0)
	points[2] = Vector3(-sign * 0.20, 0.50, 0.0)
	points[3] = Vector3(-sign * 0.20, 0.85, 0.0)
	points[4] = Vector3(-sign * 0.20, 1.25, 0.0)

	# Index extended
	points[5] = Vector3(sign * 0.0, 0.20, 0.0)
	points[6] = Vector3(sign * 0.45, 0.20, 0.0)
	points[7] = Vector3(sign * 0.90, 0.20, 0.0)
	points[8] = Vector3(sign * 1.35, 0.20, 0.0)

	# Middle extended
	points[9] = Vector3(sign * 0.0, -0.05, 0.0)
	points[10] = Vector3(sign * 0.45, -0.05, 0.0)
	points[11] = Vector3(sign * 0.90, -0.05, 0.0)
	points[12] = Vector3(sign * 1.35, -0.05, 0.0)

	# Ring folded
	points[13] = Vector3(sign * 0.0, -0.28, 0.0)
	points[14] = Vector3(sign * 0.35, -0.28, 0.0)
	points[15] = Vector3(sign * 0.20, -0.15, 0.04)
	points[16] = Vector3(sign * 0.05, -0.10, 0.03)

	# Pinky folded
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


func _make_pose(gesture: StringName) -> HandPose:
	var points := PackedVector3Array()
	points.resize(HandPose.LANDMARK_COUNT)
	points[0] = Vector3(0.0, -0.65, 0.0)
	points[1] = Vector3(-0.35, -0.15, 0.0)
	points[2] = Vector3(-0.62, 0.0, 0.0)
	points[3] = Vector3(-0.52, 0.18, 0.0)
	points[4] = Vector3(-0.30, 0.08, 0.0)

	for mcp in [5, 9, 13, 17]:
		var x_positions := {5: -0.38, 9: -0.12, 13: 0.16, 17: 0.42}
		var x: float = x_positions[mcp]
		points[mcp] = Vector3(x, 0.0, 0.0)
		var is_ext := false
		if gesture == HandGestureEngine.ROCK_AND_ROLL:
			is_ext = (mcp == 5 or mcp == 17)
		elif gesture == HandGestureEngine.FACE_PALM:
			is_ext = true
		if is_ext:
			points[mcp + 1] = Vector3(x, 0.48, 0.0)
			points[mcp + 2] = Vector3(x, 0.92, 0.0)
			points[mcp + 3] = Vector3(x, 1.35, 0.0)
		else:
			points[mcp + 1] = Vector3(x, 0.45, 0.0)
			points[mcp + 2] = Vector3(x + 0.25, 0.20, 0.04)
			points[mcp + 3] = Vector3(x + 0.10, 0.02, 0.03)

	if gesture == HandGestureEngine.THUMB_UP:
		points[1] = Vector3(-0.42, -0.12, 0.0)
		points[2] = Vector3(-0.42, 0.28, 0.0)
		points[3] = Vector3(-0.42, 0.68, 0.0)
		points[4] = Vector3(-0.42, 1.10, 0.0)
	elif gesture == HandGestureEngine.THUMB_DOWN:
		points[1] = Vector3(-0.42, 0.12, 0.0)
		points[2] = Vector3(-0.42, -0.28, 0.0)
		points[3] = Vector3(-0.42, -0.68, 0.0)
		points[4] = Vector3(-0.42, -1.10, 0.0)
	elif gesture == HandGestureEngine.FACE_PALM:
		points[1] = Vector3(-0.35, -0.15, 0.0)
		points[2] = Vector3(-0.62, 0.10, 0.0)
		points[3] = Vector3(-0.75, 0.35, 0.0)
		points[4] = Vector3(-0.85, 0.60, 0.0)

	var pose := HandPose.new()
	pose.landmarks_3d = points
	pose.landmarks_2d = _world_to_uv(points)
	pose.handedness = &"RIGHT"
	pose.handedness_score = 0.99
	pose.update_geometry()
	return pose


func _obs_from_pose(pose: HandPose) -> HandObservation:
	var obs := HandObservation.new()
	obs.landmarks_2d = pose.landmarks_2d
	obs.landmarks_3d = pose.landmarks_3d
	obs.handedness = pose.handedness
	obs.handedness_score = pose.handedness_score
	return obs


func _world_to_uv(points: PackedVector3Array) -> PackedVector2Array:
	var result := PackedVector2Array()
	for point in points:
		result.append(Vector2(0.5 + point.x * 0.16, 0.55 - point.y * 0.16))
	return result


class MockAudio:
	extends Node
	var wilhelm_play_count: int = 0
	func play_wilhelm_scream() -> void:
		wilhelm_play_count += 1


class MockParticles:
	extends Node
	var toggle_drawing_count: int = 0
	var color_change_count: int = 0
	var last_anchor: Vector2 = Vector2.ZERO
	var GravityEnabled: bool = false

	func ToggleDrawingMode() -> bool:
		toggle_drawing_count += 1
		GravityEnabled = not GravityEnabled
		return GravityEnabled

	func ChangeBodyColorRandomly(anchor: Vector2 = Vector2.ZERO) -> void:
		color_change_count += 1
		last_anchor = anchor
