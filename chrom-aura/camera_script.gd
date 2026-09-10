extends Control

@export_file("*.task") var model_path := "res://assets/models/hand_landmarker.task"
@export_range(1, 8, 1) var max_hands := 4
@export_range(0.0, 1.0, 0.05) var hand_detection_confidence := 0.4
@export_range(0.0, 1.0, 0.05) var hand_presence_confidence := 0.6
@export_range(0.0, 1.0, 0.05) var hand_tracking_confidence := 0.6
@export_range(0.0, 1.0, 0.05) var depth_threshold := 0.65

@onready var depth_camera: DepthCameraNode = $DepthCameraNode
@onready var hand_overlay: HandOverlay = $HandDetectionLayer/HandOverlay
@onready var gesture_engine: HandGestureEngine = $HandGestureEngine
@onready var status_label: Label = $Status
@onready var body_status_label: RichTextLabel = $BodyStatus
@onready var gesture_status_label: Label = $GestureStatus
@onready var rgb_debug_layer: CanvasLayer = $RgbDebugLayer
@onready var rgb_debug_view: TextureRect = $RgbDebugLayer/CameraView

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
var _hand_landmarker_delegate := ""
var _recognition_pending := false
var _last_timestamp_ms := 0
var _rgb_frame_size := Vector2i(640, 480)
var _rgb_debug_texture: ImageTexture
var dev_mode_toggled := false
var _track_thumb_up_times: Dictionary[int, int] = {}
var _last_global_thumb_up_time_ms: int = -999999
var _last_wilhelm_easter_egg_ms: int = -999999
var _wilhelm_easter_egg_active_until_ms: int = -1
var rgb_debug_toggled := false

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
	if is_instance_valid(gesture_engine):
		gesture_engine.activation_threshold = 0.65
		gesture_engine.maintenance_threshold = 0.45
		gesture_engine.activation_delay_ms = 60
		gesture_engine.release_delay_ms = 250
	rgb_debug_layer.visible = false
	set_process_input(true)

	if is_instance_valid(particles_layer) and particles_layer.has_signal("BodyCountChanged"):
		particles_layer.connect("BodyCountChanged", _on_body_count_changed)
	if is_instance_valid(particles_layer) and particles_layer.has_signal("GunShotFired"):
		particles_layer.connect("GunShotFired", _on_gun_shot_fired)
	_update_body_debug_ui()

	if _initialize_hand_landmarker():
		_set_status("Kinect & MediaPipe Initialized (%s)." % _hand_landmarker_delegate)
	else:
		_set_status("Kinect started (MediaPipe failed).")

	depth_camera.start_streaming()


func _input(event: InputEvent) -> void:
	if event.is_action_pressed("rgb-debug-toggle"):
		rgb_debug_toggled = not rgb_debug_toggled
		rgb_debug_layer.visible = rgb_debug_toggled
		get_viewport().set_input_as_handled()
		return

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
	var model_buffer := model_file.get_buffer(model_file.get_length())

	if _try_initialize_hand_landmarker(model_buffer, MediaPipeTaskBaseOptions.DELEGATE_GPU):
		_hand_landmarker_delegate = "GPU"
		return true

	push_warning("MediaPipe GPU delegate unavailable; falling back to CPU.")
	if _try_initialize_hand_landmarker(model_buffer, MediaPipeTaskBaseOptions.DELEGATE_CPU):
		_hand_landmarker_delegate = "CPU fallback"
		return true

	_hand_landmarker = null
	_show_error("Failed to initialize MediaPipe Hand Landmarker with GPU and CPU delegates.")
	return false


func _try_initialize_hand_landmarker(model_buffer: PackedByteArray, delegate: int) -> bool:
	var base_options := MediaPipeTaskBaseOptions.new()
	base_options.delegate = delegate
	base_options.model_asset_buffer = model_buffer

	var candidate := MediaPipeHandLandmarker.new()
	if not candidate.initialize(
		base_options,
		MediaPipeVisionTask.RUNNING_MODE_LIVE_STREAM,
		max_hands,
		hand_detection_confidence,
		hand_presence_confidence,
		hand_tracking_confidence,
	):
		return false

	_hand_landmarker = candidate
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
	if rgb_debug_toggled:
		_update_rgb_debug_view(image)

	_rgb_frame_size = Vector2i(image.get_width(), image.get_height())
	_recognition_pending = true
	var media_pipe_image := MediaPipeImage.new()
	media_pipe_image.set_image(image)
	var timestamp_ms := maxi(Time.get_ticks_msec(), _last_timestamp_ms + 1)
	_last_timestamp_ms = timestamp_ms
	_hand_landmarker.detect_async(media_pipe_image, timestamp_ms)


func _update_rgb_debug_view(image: Image) -> void:
	if (
		_rgb_debug_texture == null
		or _rgb_debug_texture.get_width() != image.get_width()
		or _rgb_debug_texture.get_height() != image.get_height()
	):
		_rgb_debug_texture = ImageTexture.create_from_image(image)
		rgb_debug_view.texture = _rgb_debug_texture
	else:
		_rgb_debug_texture.update(image)


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

	var detections := gesture_engine.get_active_detections(timestamp_ms)
	var pointing_fingers: Array[Vector2] = []
	var gun_detections: Array[Dictionary] = []
	for detection in detections:
		if detection.gesture == HandGestureEngine.FINGER_GUN:
			gun_detections.append({
				"track_id": detection.track_id,
				"anchor": detection.anchor_uv,
				"direction": detection.direction_uv,
			})
			_track_thumb_up_times.erase(detection.track_id)
			_last_global_thumb_up_time_ms = -999999
		elif detection.gesture == HandGestureEngine.INDEX_MIDDLE_POINTING:
			gun_detections.append({
				"track_id": detection.track_id,
				"anchor": detection.anchor_uv,
				"direction": detection.direction_uv,
			})
			_track_thumb_up_times.erase(detection.track_id)
			_last_global_thumb_up_time_ms = -999999
		elif detection.gesture == HandGestureEngine.INDEX_POINTING:
			var is_gun_thumb := false
			for pose in gesture_engine.get_hand_poses():
				if pose.track_id == detection.track_id:
					if pose.landmarks_2d.size() > 4:
						var thumb_vec: Vector2 = pose.landmarks_2d[4] - pose.landmarks_2d[2]
						if thumb_vec.y < -0.03 and thumb_vec.length() > 0.04:
							is_gun_thumb = true
					break
			if is_gun_thumb:
				gun_detections.append({
					"track_id": detection.track_id,
					"anchor": detection.anchor_uv,
					"direction": detection.direction_uv,
				})
			else:
				pointing_fingers.append(detection.anchor_uv)
			_track_thumb_up_times.erase(detection.track_id)
			_last_global_thumb_up_time_ms = -999999
		elif detection.gesture == HandGestureEngine.THUMB_UP:
			if gun_detections.is_empty() and pointing_fingers.is_empty():
				_track_thumb_up_times[detection.track_id] = timestamp_ms
				_last_global_thumb_up_time_ms = timestamp_ms
		elif detection.gesture == HandGestureEngine.THUMB_DOWN:
			if gun_detections.is_empty() and pointing_fingers.is_empty():
				var has_track_up := (
					_track_thumb_up_times.has(detection.track_id)
					and (timestamp_ms - _track_thumb_up_times[detection.track_id]) >= 200
					and (timestamp_ms - _track_thumb_up_times[detection.track_id]) <= 3000
				)
				var has_global_up := (
					(timestamp_ms - _last_global_thumb_up_time_ms) >= 200
					and (timestamp_ms - _last_global_thumb_up_time_ms) <= 3000
				)
				var is_gladiator_flip := has_track_up or has_global_up

				if is_gladiator_flip:
					_trigger_wilhelm_easter_egg(detection.track_id, detection.anchor_uv, timestamp_ms)

	# Détection réactive instantanée depuis les poses brutes (capture les flips rapides sans délai)
	for pose in gesture_engine.get_hand_poses():
		var scores := gesture_engine.classify_pose(pose)
		var gun_s: float = scores.get(HandGestureEngine.FINGER_GUN, 0.0)
		var p1_s: float = scores.get(HandGestureEngine.INDEX_POINTING, 0.0)
		var p2_s: float = scores.get(HandGestureEngine.INDEX_MIDDLE_POINTING, 0.0)
		if gun_s >= 0.20 or p1_s >= 0.25 or p2_s >= 0.25:
			_track_thumb_up_times.erase(pose.track_id)
			_last_global_thumb_up_time_ms = -999999
			continue

		if not gun_detections.is_empty() or not pointing_fingers.is_empty():
			continue

		var up_score: float = scores.get(HandGestureEngine.THUMB_UP, 0.0)
		var down_score: float = scores.get(HandGestureEngine.THUMB_DOWN, 0.0)
		if up_score >= 0.55:
			_track_thumb_up_times[pose.track_id] = timestamp_ms
			_last_global_thumb_up_time_ms = timestamp_ms
		elif down_score >= 0.55:
			var has_track_up := (
				_track_thumb_up_times.has(pose.track_id)
				and (timestamp_ms - _track_thumb_up_times[pose.track_id]) >= 200
				and (timestamp_ms - _track_thumb_up_times[pose.track_id]) <= 3000
			)
			var has_global_up := (
				(timestamp_ms - _last_global_thumb_up_time_ms) >= 200
				and (timestamp_ms - _last_global_thumb_up_time_ms) <= 3000
			)
			if has_track_up or has_global_up:
				var anchor_uv := pose.landmarks_2d[HandGestureEngine.THUMB_TIP] if pose.landmarks_2d.size() > HandGestureEngine.THUMB_TIP else pose.palm_center_uv
				_trigger_wilhelm_easter_egg(pose.track_id, anchor_uv, timestamp_ms)

	var is_pointing := not pointing_fingers.is_empty()

	if is_instance_valid(particles_layer) and particles_layer.has_method("UpdatePointingState"):
		particles_layer.UpdatePointingState(is_pointing, pointing_fingers)

	if is_instance_valid(particles_layer) and particles_layer.has_method("UpdateGunState"):
		particles_layer.UpdateGunState(gun_detections)

	_update_draw_instruction(is_pointing)

	if is_instance_valid(audio_manager) and audio_manager.has_method("set_drawing"):
		audio_manager.set_drawing(is_pointing)

	if dev_mode_toggled:
		hand_overlay.show_hand_poses(gesture_engine.get_hand_poses(), _rgb_frame_size)
		var is_easter_egg_active := timestamp_ms < _wilhelm_easter_egg_active_until_ms
		gesture_status_label.visible = not detections.is_empty() or is_easter_egg_active
		if gesture_status_label.visible:
			var messages: Array[String] = []
			if is_easter_egg_active:
				messages.append("WILHELM SCREAM!")
			for detection in detections:
				messages.append(
					"#%d %s (%.0f%%)"
					% [detection.track_id, detection.gesture, detection.score * 100.0]
				)
			gesture_status_label.text = "  |  ".join(messages)
	elif is_instance_valid(gesture_status_label):
		gesture_status_label.visible = false


func _on_gun_shot_fired(_track_id: int, _screen_pos: Vector2, _direction: Vector2) -> void:
	if is_instance_valid(audio_manager) and audio_manager.has_method("play_gun_shot"):
		audio_manager.play_gun_shot()


func _trigger_wilhelm_easter_egg(track_id: int, anchor_uv: Vector2, timestamp_ms: int) -> void:
	_last_wilhelm_easter_egg_ms = timestamp_ms
	_wilhelm_easter_egg_active_until_ms = timestamp_ms + 2500
	_track_thumb_up_times.erase(track_id)
	_last_global_thumb_up_time_ms = -999999

	if is_instance_valid(audio_manager) and audio_manager.has_method("play_wilhelm_scream"):
		audio_manager.play_wilhelm_scream()

	if is_instance_valid(particles_layer) and particles_layer.has_method("EmitWilhelmEasterEgg"):
		particles_layer.EmitWilhelmEasterEgg(anchor_uv)


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
