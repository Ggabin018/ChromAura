extends Control

@export_file("*.task") var model_path := "res://assets/models/hand_landmarker.task"
@export_range(1, 8, 1) var max_hands := 4
@export_range(0.0, 1.0, 0.05) var hand_detection_confidence := 0.4
@export_range(0.0, 1.0, 0.05) var hand_presence_confidence := 0.6
@export_range(0.0, 1.0, 0.05) var hand_tracking_confidence := 0.6
@export_range(0.0, 1.0, 0.05) var depth_threshold := 0.65

@export var debug_body_enabled := false
@export_range(0.0, 1.0, 0.01) var debug_body_depth := 0.78
@export_range(0.01, 0.25, 0.01) var debug_depth_step := 0.03
@export_range(-220.0, 220.0, 1.0) var debug_body_offset_x := 0.0
@export_range(1.0, 80.0, 1.0) var debug_body_move_step := 20.0
@export_range(1.0, 30.0, 0.5) var debug_body_smoothing := 12.0
@export_range(0.2, 1.5, 0.05) var debug_body_far_scale := 0.65
@export_range(0.2, 1.5, 0.05) var debug_body_near_scale := 1.05
@export var debug_body_arm_extended := true

@onready var depth_camera = get_node_or_null("DepthCameraNode")
@onready var hand_overlay: HandOverlay = get_node_or_null("HandDetectionLayer/HandOverlay")
@onready var gesture_engine: HandGestureEngine = get_node_or_null("HandGestureEngine")
@onready var hand_detection_layer: CanvasLayer = get_node_or_null("HandDetectionLayer")
@onready var status_label: Label = get_node_or_null("Status")
@onready var body_status_label: RichTextLabel = get_node_or_null("BodyStatus")
@onready var gesture_status_label: Label = get_node_or_null("GestureStatus")
@onready var rgb_debug_layer: CanvasLayer = get_node_or_null("RgbDebugLayer")
@onready var rgb_debug_view: TextureRect = get_node_or_null("RgbDebugLayer/CameraView")

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
@onready var heart_particle_manager: Control = get_node_or_null("HeartParticleManager")

var _hand_landmarker: RefCounted = null
var _hand_landmarker_delegate := ""
var _recognition_pending := false
var _last_timestamp_ms := 0
var _rgb_frame_size := Vector2i(640, 480)
var _rgb_debug_texture: ImageTexture
var dev_mode_toggled := false
var _last_heart_audio_time_ms := 0
var _track_thumb_up_times: Dictionary[int, int] = {}
var _last_global_thumb_up_time_ms: int = -999999
var _last_wilhelm_easter_egg_ms: int = -999999
var _wilhelm_easter_egg_active_until_ms: int = -1
var _last_rock_toggle_ms: int = -999999
var _last_face_palm_toggle_ms: int = -999999
var rgb_debug_toggled := false

const DEBUG_MASK_SIZE := Vector2i(640, 480)
var _debug_body_image: Image
var _debug_display_offset_x := 0.0
var _debug_display_depth := 0.78
var _camera_mask_pixels := PackedByteArray()
var _camera_depth_mask: Image
func _ready() -> void:
	_register_debug_input_actions()
	_debug_display_offset_x = debug_body_offset_x
	_debug_display_depth = debug_body_depth
	if audio_manager == null:
		var audio_mgr_script: Script = load("res://audio_manager.gd")
		if audio_mgr_script != null:
			audio_manager = audio_mgr_script.new()
			audio_manager.name = "AudioManager"
			add_child(audio_manager)

	if heart_particle_manager == null:
		var heart_mgr_script: Script = load("res://scripts/heart_particle_manager.gd")
		if heart_mgr_script != null:
			heart_particle_manager = heart_mgr_script.new()
			heart_particle_manager.name = "HeartParticleManager"
			add_child(heart_particle_manager)

	depth_camera.rgb_frame_ready.connect(_on_rgb_frame)
	depth_camera.depth_frame_ready.connect(_on_depth_frame)

	depth_camera.min_depth = 0.5
	depth_camera.max_depth = 3.0
	depth_camera.auto_reconnect = true

	if is_instance_valid(hand_overlay):
		hand_overlay.visible = false
	if is_instance_valid(hand_detection_layer):
		hand_detection_layer.visible = false
	if is_instance_valid(status_label):
		status_label.visible = false
	if is_instance_valid(body_status_label):
		body_status_label.visible = false
	if is_instance_valid(gesture_status_label):
		gesture_status_label.visible = false
	if is_instance_valid(gesture_engine):
		gesture_engine.activation_threshold = 0.65
		gesture_engine.maintenance_threshold = 0.45
		gesture_engine.activation_delay_ms = 60
		gesture_engine.release_delay_ms = 250
	if is_instance_valid(rgb_debug_layer):
		rgb_debug_layer.visible = false
	set_process_input(true)

	if is_instance_valid(particles_layer) and particles_layer.has_signal("BodyCountChanged"):
		particles_layer.connect("BodyCountChanged", _on_body_count_changed)
	if is_instance_valid(particles_layer) and particles_layer.has_signal("GunShotFired"):
		particles_layer.connect("GunShotFired", _on_gun_shot_fired)
	if is_instance_valid(particles_layer) and particles_layer.has_signal("DrawingModeChanged"):
		particles_layer.connect("DrawingModeChanged", _on_drawing_mode_changed)
	_update_draw_instruction(false)
	_update_body_debug_ui()

	if _initialize_hand_landmarker():
		_set_status("Kinect & MediaPipe Initialized (%s)." % _hand_landmarker_delegate)
	else:
		_set_status("Kinect started (MediaPipe failed).")

	if is_instance_valid(depth_camera):
		depth_camera.start_streaming()
	if debug_body_enabled:
		_push_debug_body_mask()


func _input(event: InputEvent) -> void:
	if event.is_action_pressed("rgb-debug-toggle"):
		rgb_debug_toggled = not rgb_debug_toggled
		if is_instance_valid(rgb_debug_layer):
			rgb_debug_layer.visible = rgb_debug_toggled
		get_viewport().set_input_as_handled()
		return

	if event.is_action_pressed("dev-mode-toggle"):
		dev_mode_toggled = not dev_mode_toggled
		if is_instance_valid(hand_detection_layer):
			hand_detection_layer.visible = dev_mode_toggled
		if is_instance_valid(hand_overlay):
			hand_overlay.visible = dev_mode_toggled
		if is_instance_valid(status_label):
			status_label.visible = dev_mode_toggled
		if is_instance_valid(body_status_label):
			body_status_label.visible = dev_mode_toggled
		if is_instance_valid(gesture_status_label):
			gesture_status_label.visible = dev_mode_toggled and gesture_status_label.text != ""


func _process(delta: float) -> void:
	if is_instance_valid(heart_particle_manager) and is_instance_valid(audio_manager):
		var has_hearts: bool = heart_particle_manager.has_hearts() if heart_particle_manager.has_method("has_hearts") else false
		audio_manager.set_heart_music(has_hearts)

	if Input.is_action_just_pressed("debug_toggle_body"):
		debug_body_enabled = not debug_body_enabled
		if debug_body_enabled:
			_push_debug_body_mask()
		else:
			particles_layer.SetDemoBodyTransform(false, 0.0, 1.0, 0.0)
			$Particles.SetDepthImageMask(Image.create(1, 1, false, Image.FORMAT_RGB8))

	if not debug_body_enabled:
		return

	# Holding a key moves continuously; taps still move by one step.
	var movement := Input.get_axis("debug_body_move_left", "debug_body_move_right")
	var depth_change := Input.get_axis("debug_body_depth_down", "debug_body_depth_up")
	var movement_step := delta * 10.0
	var depth_step := delta * 10.0
	if Input.is_action_just_pressed("debug_body_move_left") or Input.is_action_just_pressed("debug_body_move_right"):
		movement_step = 1.0
	if Input.is_action_just_pressed("debug_body_depth_down") or Input.is_action_just_pressed("debug_body_depth_up"):
		depth_step = 1.0
	debug_body_depth = clampf(debug_body_depth + depth_change * debug_depth_step * depth_step, 0.0, 1.0)
	debug_body_offset_x = clampf(debug_body_offset_x + movement * debug_body_move_step * movement_step, -220.0, 220.0)
	var blend := 1.0 - exp(-debug_body_smoothing * delta)
	_debug_display_depth = lerpf(_debug_display_depth, debug_body_depth, blend)
	_debug_display_offset_x = lerpf(_debug_display_offset_x, debug_body_offset_x, blend)
	_update_debug_body_transform()


func _exit_tree() -> void:
	if depth_camera:
		depth_camera.stop_streaming()
	_hand_landmarker = null


func _initialize_hand_landmarker() -> bool:
	if not ClassDB.class_exists("MediaPipeHandLandmarker"):
		_show_error("MediaPipe GDExtension not available.")
		return false

	var model_file := FileAccess.open(model_path, FileAccess.READ)
	if model_file == null:
		_show_error("MediaPipe model not found: %s" % model_path)
		return false
	var model_buffer := model_file.get_buffer(model_file.get_length())

	var gpu_delegate: int = (
		ClassDB.class_get_integer_constant("MediaPipeTaskBaseOptions", "DELEGATE_GPU")
		if ClassDB.class_has_integer_constant("MediaPipeTaskBaseOptions", "DELEGATE_GPU")
		else 1
	)
	var cpu_delegate: int = (
		ClassDB.class_get_integer_constant("MediaPipeTaskBaseOptions", "DELEGATE_CPU")
		if ClassDB.class_has_integer_constant("MediaPipeTaskBaseOptions", "DELEGATE_CPU")
		else 0
	)

	if _try_initialize_hand_landmarker(model_buffer, gpu_delegate):
		_hand_landmarker_delegate = "GPU"
		return true

	push_warning("MediaPipe GPU delegate unavailable; falling back to CPU.")
	if _try_initialize_hand_landmarker(model_buffer, cpu_delegate):
		_hand_landmarker_delegate = "CPU fallback"
		return true

	_hand_landmarker = null
	_show_error("Failed to initialize MediaPipe Hand Landmarker with GPU and CPU delegates.")
	return false


func _try_initialize_hand_landmarker(model_buffer: PackedByteArray, delegate: int) -> bool:
	if not ClassDB.can_instantiate("MediaPipeTaskBaseOptions") or not ClassDB.can_instantiate("MediaPipeHandLandmarker"):
		return false

	var base_options: Object = ClassDB.instantiate("MediaPipeTaskBaseOptions")
	base_options.set("delegate", delegate)
	base_options.set("model_asset_buffer", model_buffer)

	var candidate: Object = ClassDB.instantiate("MediaPipeHandLandmarker")
	var running_mode: int = (
		ClassDB.class_get_integer_constant("MediaPipeVisionTask", "RUNNING_MODE_LIVE_STREAM")
		if ClassDB.class_has_integer_constant("MediaPipeVisionTask", "RUNNING_MODE_LIVE_STREAM")
		else 3
	)
	if not candidate.initialize(
		base_options,
		running_mode,
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
	var media_pipe_image: Object = ClassDB.instantiate("MediaPipeImage") if ClassDB.can_instantiate("MediaPipeImage") else null
	if media_pipe_image == null:
		_recognition_pending = false
		return
	media_pipe_image.set_image(image)
	var timestamp_ms := maxi(Time.get_ticks_msec(), _last_timestamp_ms + 1)
	_last_timestamp_ms = timestamp_ms
	_hand_landmarker.detect_async(media_pipe_image, timestamp_ms)


func _update_rgb_debug_view(image: Image) -> void:
	if not is_instance_valid(rgb_debug_view):
		return
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
	result: Object,
	_image: Object,
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
	var heart_detections: Array[Dictionary] = []
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
		elif detection.gesture == HandGestureEngine.TWO_HAND_HEART:
			heart_detections.append({
				"anchor": detection.anchor_uv,
				"gesture": detection.gesture,
				"scale_hint": 1.35,
			})
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
		elif detection.gesture == HandGestureEngine.ROCK_AND_ROLL:
			if (timestamp_ms - _last_rock_toggle_ms) >= 800:
				_last_rock_toggle_ms = timestamp_ms
				_toggle_drawing_mode()
		elif detection.gesture == HandGestureEngine.FACE_PALM:
			if (timestamp_ms - _last_face_palm_toggle_ms) >= 800:
				_last_face_palm_toggle_ms = timestamp_ms
				_trigger_face_palm_color_change(detection.anchor_uv)

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
		if up_score >= 0.50:
			_track_thumb_up_times[pose.track_id] = timestamp_ms
			_last_global_thumb_up_time_ms = timestamp_ms
		elif down_score >= 0.50:
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

	var is_heart := not heart_detections.is_empty()
	if is_instance_valid(heart_particle_manager) and heart_particle_manager.has_method("update_heart_state"):
		heart_particle_manager.update_heart_state(heart_detections)
	elif is_instance_valid(audio_manager) and audio_manager.has_method("set_heart_music"):
		audio_manager.set_heart_music(is_heart)

	_update_draw_instruction(is_pointing)

	if is_instance_valid(audio_manager) and audio_manager.has_method("set_drawing"):
		audio_manager.set_drawing(is_pointing)

	if dev_mode_toggled:
		if is_instance_valid(hand_overlay) and is_instance_valid(gesture_engine):
			hand_overlay.show_hand_poses(gesture_engine.get_hand_poses(), _rgb_frame_size)
		var is_easter_egg_active := timestamp_ms < _wilhelm_easter_egg_active_until_ms
		if is_instance_valid(gesture_status_label):
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


func _on_drawing_mode_changed(_is_physics: bool) -> void:
	_update_draw_instruction(false)


func _toggle_drawing_mode() -> void:
	if is_instance_valid(particles_layer) and particles_layer.has_method("ToggleDrawingMode"):
		var _is_phys: bool = particles_layer.ToggleDrawingMode()
		_update_draw_instruction(false)
	elif is_instance_valid(particles_layer) and particles_layer.has_method("ToggleGravity"):
		particles_layer.ToggleGravity()
		_update_draw_instruction(false)


func _trigger_face_palm_color_change(anchor_uv: Vector2) -> void:
	if is_instance_valid(particles_layer) and particles_layer.has_method("ChangeBodyColorRandomly"):
		particles_layer.ChangeBodyColorRandomly(anchor_uv)
		_update_body_debug_ui()


func _update_draw_instruction(is_pointing: bool) -> void:
	if not is_instance_valid(draw_instruction):
		return
	if is_pointing:
		draw_instruction.modulate = Color(0.25, 1.0, 0.85, 1.0)
	else:
		draw_instruction.modulate = Color(1.0, 1.0, 1.0, 0.85)

	# The instruction carousel names the drawing row "Dessinez". Keep the old
	# path as a fallback so this remains compatible with scenes made before the
	# instruction-carousel merge.
	var label_node := draw_instruction.get_node_or_null("Dessinez/Label") as Label
	if not is_instance_valid(label_node):
		label_node = draw_instruction.get_node_or_null("HBoxContainer/Label") as Label
	if is_instance_valid(label_node) and is_instance_valid(particles_layer) and "GravityEnabled" in particles_layer:
		var is_physics: bool = bool(particles_layer.GravityEnabled)
		label_node.text = "Dessinez"
		var instruction_layer := draw_instruction.get_parent()
		if is_instance_valid(instruction_layer) and instruction_layer.has_method("set_gravity_status"):
			instruction_layer.set_gravity_status(is_physics)


func _on_depth_frame(image_texture: ImageTexture) -> void:
	if debug_body_enabled:
		return

	if image_texture == null:
		return

	var source := image_texture.get_image()
	if source == null or source.is_empty():
		return

	var width := source.get_width()
	var height := source.get_height()
	if source.get_format() != Image.FORMAT_RGB8:
		source.convert(Image.FORMAT_RGB8)
	var source_pixels := source.get_data()
	_camera_mask_pixels.resize(width * height * 3)
	# Threshold once here; retain analog red depth and the existing horizontal flip.
	# Byte access avoids hundreds of thousands of get_pixel/set_pixel calls per frame.
	for y in range(height):
		for x in range(width):
			var depth := source_pixels[(y * width + x) * 3]
			var destination := (y * width + width - 1 - x) * 3
			_camera_mask_pixels[destination] = depth if depth / 255.0 >= depth_threshold else 0
	if _camera_depth_mask == null:
		_camera_depth_mask = Image.create_from_data(width, height, false, Image.FORMAT_RGB8, _camera_mask_pixels)
	else:
		_camera_depth_mask.set_data(width, height, false, Image.FORMAT_RGB8, _camera_mask_pixels)

	particles_layer.SetDepthImageMask(_camera_depth_mask)
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


func _register_debug_input_actions() -> void:
	_add_debug_key("rgb-debug-toggle", KEY_R)
	_add_debug_key("debug_toggle_body", KEY_F)
	_add_debug_key("debug_body_depth_up", KEY_PLUS)
	_add_debug_key("debug_body_depth_up", KEY_KP_ADD)
	_add_debug_key("debug_body_depth_down", KEY_MINUS)
	_add_debug_key("debug_body_depth_down", KEY_KP_SUBTRACT)
	_add_debug_key("debug_body_move_left", KEY_LEFT)
	_add_debug_key("debug_body_move_right", KEY_RIGHT)


func _add_debug_key(action: StringName, keycode: Key) -> void:
	if not InputMap.has_action(action):
		InputMap.add_action(action)
	else:
		for existing_event in InputMap.action_get_events(action):
			if (
				existing_event is InputEventKey
				and (
					existing_event.keycode == keycode
					or existing_event.physical_keycode == keycode
				)
			):
				return

	var key_event := InputEventKey.new()
	key_event.keycode = keycode
	InputMap.action_add_event(action, key_event)


func _push_debug_body_mask() -> void:
	if _debug_body_image == null:
		_debug_body_image = Image.create(DEBUG_MASK_SIZE.x, DEBUG_MASK_SIZE.y, false, Image.FORMAT_RGB8)
		for y in range(DEBUG_MASK_SIZE.y):
			for x in range(DEBUG_MASK_SIZE.x):
				if _is_debug_body_pixel(x, y):
					_debug_body_image.set_pixel(x, y, Color(1.0, 0.0, 0.0, 1.0))
	$Particles.SetDepthImageMask(_debug_body_image)
	_update_debug_body_transform()


func _update_debug_body_transform() -> void:
	var body_scale := lerpf(debug_body_far_scale, debug_body_near_scale, _debug_display_depth)
	# Keep the complete silhouette inside the mask area, including at the largest zoom.
	var half_width := 215.0 if debug_body_arm_extended else 130.0
	var horizontal_limit := maxf(0.0, DEBUG_MASK_SIZE.x * 0.5 - half_width * body_scale)
	debug_body_offset_x = clampf(debug_body_offset_x, -horizontal_limit, horizontal_limit)
	_debug_display_offset_x = clampf(_debug_display_offset_x, -horizontal_limit, horizontal_limit)
	particles_layer.SetDemoBodyTransform(true, _debug_display_offset_x, body_scale, _debug_display_depth)
	_update_body_debug_ui()


func _is_debug_body_pixel(x: float, y: float) -> bool:
	# Fit the feet as well as the head in the cached mask (the previous legs were cropped).
	var body_y := (y - 240.0) / 0.78 + 304.0
	var head := _inside_ellipse(x, body_y, 320.0, 132.0, 48.0, 58.0)
	var torso := _inside_ellipse(x, body_y, 320.0, 275.0, 86.0, 128.0)
	var left_arm := _inside_ellipse(x, body_y, 225.0, 280.0, 34.0, 118.0)
	if debug_body_arm_extended:
		left_arm = _inside_ellipse(x, body_y, 207.0, 240.0, 100.0, 28.0)
	var right_arm := _inside_ellipse(x, body_y, 415.0, 280.0, 34.0, 118.0)
	var left_leg := _inside_ellipse(x, body_y, 278.0, 425.0, 42.0, 110.0)
	var right_leg := _inside_ellipse(x, body_y, 362.0, 425.0, 42.0, 110.0)
	return head or torso or left_arm or right_arm or left_leg or right_leg


func _inside_ellipse(x: float, y: float, cx: float, cy: float, rx: float, ry: float) -> bool:
	return pow((x - cx) / rx, 2.0) + pow((y - cy) / ry, 2.0) <= 1.0


func _set_status(message: String) -> void:
	status_label.text = message


func _show_error(message: String) -> void:
	_set_status(message)
	push_error(message)
