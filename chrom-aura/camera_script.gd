extends Control

@export_file("*.task") var model_path := "res://assets/models/hand_landmarker.task"
@export_range(1, 8, 1) var max_hands := 4
@export_range(0.0, 1.0, 0.05) var hand_detection_confidence := 0.5
@export_range(0.0, 1.0, 0.05) var hand_presence_confidence := 0.5
@export_range(0.0, 1.0, 0.05) var hand_tracking_confidence := 0.5
@export_range(0.0, 1.0, 0.05) var depth_threshold := 0.65

@export_group("Laser lateral")
@export_range(0.10, 0.45, 0.01) var side_laser_zone_x := 0.34
@export_range(1, 4, 1) var side_laser_min_bent_fingers := 3
@export_range(50, 1200, 10) var side_laser_hold_grace_ms := 500
@export_range(80.0, 175.0, 1.0) var side_laser_max_tip_joint_angle := 150.0
@export_range(0.50, 2.00, 0.05) var side_laser_min_tip_reach := 0.80

@onready var depth_camera: DepthCameraNode = $DepthCameraNode
@onready var hand_overlay: HandOverlay = $HandDetectionLayer/HandOverlay
@onready var gesture_engine: HandGestureEngine = $HandGestureEngine
@onready var status_label: Label = $Status
@onready var body_status_label: RichTextLabel = $BodyStatus
@onready var gesture_status_label: Label = $GestureStatus

@export_range(0.0, 1.0, 0.01)
var depth_min_for_color := 0.65

@export_range(0.0, 1.0, 0.01)
var depth_max_for_color := 1.0

@export var depth_color_min := Color8(180, 250, 255)
@export var depth_color_max := Color8(110, 20, 220)

@export var mirror_depth := true
@onready var particles_layer: Node = $Particles
@onready var draw_instruction: Control = $DrawInstructionLayer/DrawInstruction
@onready var audio_manager: Node = get_node_or_null("AudioManager")

var _hand_landmarker: MediaPipeHandLandmarker
var _recognition_pending := false
var _last_timestamp_ms := 0
var _rgb_frame_size := Vector2i(640, 480)
var dev_mode_toggled := false

var _side_laser_active := false
var _side_laser_last_valid_ms := -1
var _side_laser_last_anchor_uv := Vector2.ZERO
var _side_laser_last_direction := 1.0

func _ready() -> void:
	if audio_manager == null:
		var audio_mgr_script: Script = load("res://audio_manager.gd")
		if audio_mgr_script != null:
			audio_manager = audio_mgr_script.new()
			audio_manager.name = "AudioManager"
			add_child(audio_manager)

	depth_camera.rgb_frame_ready.connect(_on_rgb_frame)
	depth_camera.depth_frame_ready.connect(_on_depth_frame)

	depth_camera.min_depth = 0.5
	depth_camera.max_depth = 3.0
	depth_camera.auto_reconnect = true

	hand_overlay.visible = false
	status_label.visible = false
	if is_instance_valid(body_status_label):
		body_status_label.visible = false
	gesture_status_label.visible = false
	set_process_input(true)

	if is_instance_valid(particles_layer) and particles_layer.has_signal("BodyCountChanged"):
		particles_layer.connect("BodyCountChanged", _on_body_count_changed)
	if is_instance_valid(particles_layer) and particles_layer.has_signal("GunShotFired"):
		particles_layer.connect("GunShotFired", _on_gun_shot_fired)
	_update_body_debug_ui()

	if _initialize_hand_landmarker():
		_set_status("Kinect & MediaPipe Initialized.")
	else:
		_set_status("Kinect started (MediaPipe failed).")

	depth_camera.start_streaming()


func _input(event: InputEvent) -> void:
	if event.is_action_pressed("dev-mode-toggle"):
		dev_mode_toggled = not dev_mode_toggled
		hand_overlay.visible = dev_mode_toggled
		status_label.visible = dev_mode_toggled
		if is_instance_valid(body_status_label):
			body_status_label.visible = dev_mode_toggled
		gesture_status_label.visible = dev_mode_toggled and gesture_status_label.text != ""


func _exit_tree() -> void:
	if depth_camera:
		depth_camera.stop_streaming()
	_hand_landmarker = null


func _initialize_hand_landmarker() -> bool:
	var model_file := FileAccess.open(model_path, FileAccess.READ)
	if model_file == null:
		_show_error("MediaPipe model not found: %s" % model_path)
		return false

	var base_options := MediaPipeTaskBaseOptions.new()
	base_options.delegate = MediaPipeTaskBaseOptions.DELEGATE_CPU
	base_options.model_asset_buffer = model_file.get_buffer(model_file.get_length())

	_hand_landmarker = MediaPipeHandLandmarker.new()
	if not _hand_landmarker.initialize(
		base_options,
		MediaPipeVisionTask.RUNNING_MODE_LIVE_STREAM,
		max_hands,
		hand_detection_confidence,
		hand_presence_confidence,
		hand_tracking_confidence,
	):
		_hand_landmarker = null
		_show_error("Failed to initialize MediaPipe Hand Landmarker.")
		return false

	_hand_landmarker.result_callback.connect(_on_hand_result)
	return true


func _on_rgb_frame(image_texture: ImageTexture) -> void:
	if image_texture == null or _recognition_pending or _hand_landmarker == null:
		return

	var image := image_texture.get_image()
	if image == null or image.is_empty():
		return
	if image.get_format() != Image.FORMAT_RGB8:
		image.convert(Image.FORMAT_RGB8)

	image.flip_x()

	_rgb_frame_size = Vector2i(image.get_width(), image.get_height())
	_recognition_pending = true
	var media_pipe_image := MediaPipeImage.new()
	media_pipe_image.set_image(image)
	var timestamp_ms := maxi(Time.get_ticks_msec(), _last_timestamp_ms + 1)
	_last_timestamp_ms = timestamp_ms
	_hand_landmarker.detect_async(media_pipe_image, timestamp_ms)


func _on_hand_result(
	result: MediaPipeHandLandmarkerResult,
	_image: MediaPipeImage,
	timestamp_ms: int,
) -> void:
	var observations: Array[HandObservation] = []
	for hand_index in result.hand_landmarks.size():
		var detected_hand = result.hand_landmarks[hand_index]
		var points_2d := PackedVector2Array()
		var fallback_points_3d := PackedVector3Array()
		for landmark in detected_hand.landmarks:
			points_2d.append(Vector2(landmark.x, landmark.y))
			fallback_points_3d.append(Vector3(landmark.x, landmark.y, landmark.z))

		var points_3d := fallback_points_3d
		if hand_index < result.hand_world_landmarks.size():
			points_3d = PackedVector3Array()
			for landmark in result.hand_world_landmarks[hand_index].landmarks:
				points_3d.append(Vector3(landmark.x, landmark.y, landmark.z))

		var handedness: StringName = HandPose.UNKNOWN_HAND
		var handedness_score := 0.0
		if hand_index < result.handedness.size():
			for category in result.handedness[hand_index].categories:
				if category.score > handedness_score:
					handedness = StringName(category.category_name.to_upper())
					handedness_score = category.score

		var observation := HandObservation.new()
		observation.landmarks_2d = points_2d
		observation.landmarks_3d = points_3d
		observation.handedness = handedness
		observation.handedness_score = handedness_score
		observations.append(observation)

	_apply_hand_result.call_deferred(observations, timestamp_ms)


func _apply_hand_result(observations: Array[HandObservation], timestamp_ms: int) -> void:
	_recognition_pending = false
	gesture_engine.process_observations(observations, timestamp_ms)
	var hand_poses := gesture_engine.get_hand_poses()
	var side_laser := _update_side_laser(hand_poses, timestamp_ms)

	if is_instance_valid(particles_layer) and particles_layer.has_method("UpdateSideLaserState"):
		particles_layer.UpdateSideLaserState(
			bool(side_laser.get("active", false)),
			side_laser.get("anchor", Vector2.ZERO),
			float(side_laser.get("direction", 1.0)),
			float(side_laser.get("strength", 0.0)),
		)

	var detections := gesture_engine.get_active_detections(timestamp_ms)
	var pointing_fingers: Array[Vector2] = []
	var gun_detections: Array[Dictionary] = []
	for detection in detections:
		if detection.gesture == HandGestureEngine.INDEX_POINTING:
			pointing_fingers.append(detection.anchor_uv)
		elif detection.gesture == HandGestureEngine.FINGER_GUN:
			gun_detections.append({
				"track_id": detection.track_id,
				"anchor": detection.anchor_uv,
				"direction": detection.direction_uv,
			})
		elif detection.gesture == HandGestureEngine.INDEX_MIDDLE_POINTING:
			if absf(detection.direction_uv.x) >= 0.40:
				gun_detections.append({
					"track_id": detection.track_id,
					"anchor": detection.anchor_uv,
					"direction": detection.direction_uv,
				})
			else:
				pointing_fingers.append(detection.anchor_uv)
	var is_pointing := not pointing_fingers.is_empty()

	if is_instance_valid(particles_layer) and particles_layer.has_method("UpdatePointingState"):
		particles_layer.UpdatePointingState(is_pointing, pointing_fingers)

	if is_instance_valid(particles_layer) and particles_layer.has_method("UpdateGunState"):
		particles_layer.UpdateGunState(gun_detections)

	_update_draw_instruction(is_pointing)

	if is_instance_valid(audio_manager) and audio_manager.has_method("set_drawing"):
		audio_manager.set_drawing(is_pointing)

	if dev_mode_toggled:
		hand_overlay.show_hand_poses(hand_poses, _rgb_frame_size)
		gesture_status_label.visible = (not detections.is_empty()) or bool(side_laser.get("active", false))
		if gesture_status_label.visible:
			var messages: Array[String] = []
			if bool(side_laser.get("active", false)):
				messages.append("SIDE LASER (%.0f%%)" % [float(side_laser.get("strength", 0.0)) * 100.0])
			for detection in detections:
				messages.append(
					"#%d %s (%.0f%%)"
					% [detection.track_id, detection.gesture, detection.score * 100.0]
				)
			gesture_status_label.text = "  |  ".join(messages)
	else:
		gesture_status_label.visible = false


func _update_side_laser(poses: Array[HandPose], timestamp_ms: int) -> Dictionary:
	var result := {
		"active": false,
		"anchor": Vector2.ZERO,
		"direction": 1.0,
		"strength": 0.0,
	}

	var best_pose: HandPose = null
	var best_score := -INF
	var best_direction := 1.0

	for pose in poses:
		if pose == null or pose.landmarks_2d.size() < 21:
			continue

		var palm_x := pose.palm_center_uv.x
		var on_left_side := palm_x <= side_laser_zone_x
		var on_right_side := palm_x >= 1.0 - side_laser_zone_x
		if not on_left_side and not on_right_side:
			continue

		var pose_score := _side_laser_hand_score(pose)
		if pose_score <= 0.0:
			continue

		# On favorise la main la plus proche du bord de l'image.
		var edge_bonus := (0.5 - palm_x) if on_left_side else (palm_x - 0.5)
		var total_score := pose_score + edge_bonus * 0.35
		if total_score > best_score:
			best_score = total_score
			best_pose = pose
			best_direction = -1.0 if on_left_side else 1.0

	if best_pose != null:
		var points := best_pose.landmarks_2d
		# Le laser part du centre des quatre bouts de doigts.
		var anchor := (points[8] + points[12] + points[16] + points[20]) * 0.25
		_side_laser_active = true
		_side_laser_last_valid_ms = timestamp_ms
		_side_laser_last_anchor_uv = anchor
		_side_laser_last_direction = best_direction

		result["active"] = true
		result["anchor"] = anchor
		result["direction"] = best_direction
		result["strength"] = clampf(best_score, 0.65, 1.0)
		return result

	# Stabilisation : une perte de quelques frames ne coupe pas instantanement le rayon.
	if (
		_side_laser_active
		and _side_laser_last_valid_ms >= 0
		and timestamp_ms - _side_laser_last_valid_ms <= side_laser_hold_grace_ms
	):
		result["active"] = true
		result["anchor"] = _side_laser_last_anchor_uv
		result["direction"] = _side_laser_last_direction
		result["strength"] = 0.85
		return result

	_side_laser_active = false
	_side_laser_last_valid_ms = -1
	return result


func _side_laser_hand_score(pose: HandPose) -> float:
	# Pose visee : une seule main avec les grandes phalanges encore deployees,
	# mais les extremites des doigts repliees vers l'avant (aspect "crochet/canon").
	var points := pose.landmarks_2d
	var palm := pose.palm_center_uv
	var scale := maxf(pose.palm_scale_uv, 0.001)
	var bent_count := 0
	var score_sum := 0.0

	var fingers := [
		[5, 6, 7, 8],
		[9, 10, 11, 12],
		[13, 14, 15, 16],
		[17, 18, 19, 20],
	]

	for finger in fingers:
		var mcp: Vector2 = points[finger[0]]
		var pip: Vector2 = points[finger[1]]
		var dip: Vector2 = points[finger[2]]
		var tip: Vector2 = points[finger[3]]
		var pip_angle := _joint_angle_degrees(mcp, pip, dip)
		var dip_angle := _joint_angle_degrees(pip, dip, tip)
		var tip_reach := tip.distance_to(palm) / scale

		# PIP reste relativement deploye, DIP se replie : on detecte surtout
		# la partie haute du doigt penchee plutot qu'un poing ferme.
		var upper_finger_bent := pip_angle >= 105.0 and dip_angle <= side_laser_max_tip_joint_angle
		var not_a_fist := tip_reach >= side_laser_min_tip_reach
		if upper_finger_bent and not_a_fist:
			bent_count += 1
			var bend_strength := clampf((side_laser_max_tip_joint_angle - dip_angle) / 55.0, 0.0, 1.0)
			score_sum += 0.55 + bend_strength * 0.45

	if bent_count < side_laser_min_bent_fingers:
		return 0.0

	return clampf(score_sum / float(maxi(bent_count, 1)), 0.0, 1.0)


func _joint_angle_degrees(a: Vector2, b: Vector2, c: Vector2) -> float:
	var ba := a - b
	var bc := c - b
	if ba.length_squared() < 0.0000001 or bc.length_squared() < 0.0000001:
		return 0.0
	return rad_to_deg(ba.angle_to(bc))


func _on_gun_shot_fired(_track_id: int, _screen_pos: Vector2, _direction: Vector2) -> void:
	if is_instance_valid(audio_manager) and audio_manager.has_method("play_gun_shot"):
		audio_manager.play_gun_shot()


func _update_draw_instruction(is_pointing: bool) -> void:
	if not is_instance_valid(draw_instruction):
		return
	if is_pointing:
		draw_instruction.modulate = Color(0.25, 1.0, 0.85, 1.0)
	else:
		draw_instruction.modulate = Color(1.0, 1.0, 1.0, 0.85)


func _on_depth_frame(image_texture: ImageTexture) -> void:
	if image_texture == null:
		return

	var source := image_texture.get_image()
	if source == null or source.is_empty():
		return

	var width := source.get_width()
	var height := source.get_height()
	var depth_mask := Image.create(width, height, false, Image.FORMAT_RGB8)
	for y in range(height):
		for x in range(width):
			var raw_depth := source.get_pixel(x, y).r
			if raw_depth >= depth_threshold:
				depth_mask.set_pixel(width - 1 - x, y, Color(raw_depth, 0, 0, 1))

	particles_layer.SetDepthImageMask(depth_mask)
	_update_body_debug_ui()


func _on_body_count_changed(_count: int) -> void:
	_update_body_debug_ui()


func _update_body_debug_ui() -> void:
	if not is_instance_valid(body_status_label):
		return
	if not dev_mode_toggled:
		body_status_label.visible = false
		return

	body_status_label.visible = true
	var count := 0
	if is_instance_valid(particles_layer) and "DetectedBodyCount" in particles_layer:
		count = int(particles_layer.DetectedBodyCount)

	var text := "[b]👤 Corps détectés : %d[/b]" % count
	if is_instance_valid(particles_layer) and particles_layer.has_method("GetBodiesDebugInfo"):
		var info: Array = particles_layer.GetBodiesDebugInfo()
		if info.size() > 0:
			text += "  |"
			for body in info:
				var palette_color: String = body.get("color_near", "ffffff")
				var palette_name: String = body.get("palette_name", "")
				var body_id: int = body.get("id", 0)
				var is_drawing: bool = body.get("is_pointing", false)
				var drawing_icon := " ✍️" if is_drawing else ""
				text += (
					"  [color=#%s]● P%d: %s%s[/color]"
					% [palette_color, body_id, palette_name, drawing_icon]
				)

	body_status_label.text = text


func _set_status(message: String) -> void:
	status_label.text = message


func _show_error(message: String) -> void:
	_set_status(message)
	push_error(message)
