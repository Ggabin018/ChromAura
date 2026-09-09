extends Control

@export_file("*.task") var model_path := "res://assets/models/gesture_recognizer.task"
@export_range(1, 8, 1) var max_hands := 4
@export_range(0.0, 1.0, 0.05) var pointing_up_confidence := 0.4
@export_range(0.0, 1.0, 0.05) var depth_threshold := 0.65

@onready var depth_camera: DepthCameraNode = $DepthCameraNode
@onready var hand_overlay: HandOverlay = $HandDetectionLayer/HandOverlay
@onready var status_label: Label = $Status
@onready var body_status_label: RichTextLabel = $BodyStatus
@onready var gesture_status_label: Label = $GestureStatus
@onready var particles_layer: Node = $Particles
@onready var draw_instruction: Control = $DrawInstructionLayer/DrawInstruction
@onready var audio_manager: Node = get_node_or_null("AudioManager")
@onready var body_silhouette: TextureRect = $Particles/BodySilhouette


var _gesture_recognizer: MediaPipeGestureRecognizer
var _recognition_pending := false
var _last_timestamp_ms := 0
var _rgb_frame_size := Vector2i(640, 480)
var dev_mode_toggled := false


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
	_update_body_debug_ui()

	if body_silhouette.material is ShaderMaterial:
		body_silhouette.material.set_shader_parameter("threshold", depth_threshold)

	if _initialize_gesture_recognizer():
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
	_gesture_recognizer = null


func _initialize_gesture_recognizer() -> bool:
	var model_file := FileAccess.open(model_path, FileAccess.READ)
	if model_file == null:
		_show_error("MediaPipe model not found: %s" % model_path)
		return false

	var base_options := MediaPipeTaskBaseOptions.new()
	base_options.delegate = MediaPipeTaskBaseOptions.DELEGATE_CPU
	base_options.model_asset_buffer = model_file.get_buffer(model_file.get_length())

	_gesture_recognizer = MediaPipeGestureRecognizer.new()
	if not _gesture_recognizer.initialize(
		base_options,
		MediaPipeVisionTask.RUNNING_MODE_LIVE_STREAM,
		max_hands,
	):
		_gesture_recognizer = null
		_show_error("Failed to initialize MediaPipe Gesture Recognizer.")
		return false

	_gesture_recognizer.result_callback.connect(_on_gesture_result)
	return true


func _on_rgb_frame(image_texture: ImageTexture) -> void:
	if image_texture == null or _recognition_pending or _gesture_recognizer == null:
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
	_gesture_recognizer.recognize_async(media_pipe_image, timestamp_ms)


func _on_gesture_result(
	result: MediaPipeGestureRecognizerResult,
	_image: MediaPipeImage,
	_timestamp_ms: int,
) -> void:
	var hands: Array[PackedVector2Array] = []
	for detected_hand in result.hand_landmarks:
		var points := PackedVector2Array()
		for landmark in detected_hand.landmarks:
			points.append(Vector2(landmark.x, landmark.y))
		hands.append(points)

	var pointing_fingers: Array[Vector2] = []
	var best_score := 0.0

	var hand_count := mini(result.hand_landmarks.size(), result.gestures.size())
	for i in range(hand_count):
		for category in result.gestures[i].categories:
			if category.category_name == "Pointing_Up":
				best_score = maxf(best_score, category.score)
				if category.score >= pointing_up_confidence and result.hand_landmarks[i].landmarks.size() > 8:
					pointing_fingers.append(Vector2(
						result.hand_landmarks[i].landmarks[8].x,
						result.hand_landmarks[i].landmarks[8].y
					))

	_apply_gesture_result.call_deferred(hands, best_score, pointing_fingers)


func _apply_gesture_result(
	hands: Array[PackedVector2Array],
	pointing_up_score: float,
	pointing_fingers: Array[Vector2],
) -> void:
	_recognition_pending = false

	var is_pointing := pointing_fingers.size() > 0 or pointing_up_score >= pointing_up_confidence

	if is_instance_valid(particles_layer) and particles_layer.has_method("UpdatePointingState"):
		particles_layer.UpdatePointingState(is_pointing, pointing_fingers)

	_update_draw_instruction(is_pointing)

	if is_instance_valid(audio_manager) and audio_manager.has_method("set_drawing"):
		audio_manager.set_drawing(is_pointing)

	if dev_mode_toggled:
		hand_overlay.show_hands(hands, _rgb_frame_size)
		gesture_status_label.visible = is_pointing
		if dev_mode_toggled:
			gesture_status_label.text = "Pointing Up detected (%.0f%%)" % (pointing_up_score * 100.0)
	else:
		gesture_status_label.visible = false


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

	body_silhouette.texture = image_texture

	var source := image_texture.get_image()
	if source == null or source.is_empty():
		return

	var width := source.get_width()
	var height := source.get_height()
	var depth_mask := Image.create(width, height, false, Image.FORMAT_RGB8)
	for y in range(height):
		for x in range(width):
			var depth := source.get_pixel(x, y).r
			if depth >= depth_threshold:
				depth_mask.set_pixel(width - 1 - x, y, Color(depth, 0, 0, 1))

	$Particles.SetDepthImageMask(depth_mask)
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
			for b in info:
				var pal_color: String = b.get("color_near", "ffffff")
				var pal_name: String = b.get("palette_name", "")
				var bid: int = b.get("id", 0)
				var is_ptr: bool = b.get("is_pointing", false)
				var ptr_icon := " ✍️" if is_ptr else ""
				text += "  [color=#%s]● P%d: %s%s[/color]" % [pal_color, bid, pal_name, ptr_icon]

	body_status_label.text = text


func _set_status(message: String) -> void:
	status_label.text = message


func _show_error(message: String) -> void:
	_set_status(message)
	push_error(message)
