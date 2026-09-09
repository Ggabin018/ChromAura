extends Control

@export_file("*.task") var model_path := "res://assets/models/gesture_recognizer.task"
@export_range(1, 8, 1) var max_hands := 4
@export_range(0.0, 1.0, 0.05) var pointing_up_confidence := 0.4
@export_range(0.0, 1.0, 0.05) var depth_threshold := 0.65
@export var show_hand_detection := true

@onready var depth_camera: DepthCameraNode = $DepthCameraNode
@onready var hand_overlay: HandOverlay = $HandDetectionLayer/HandOverlay
@onready var status_label: Label = $Status
@onready var gesture_status_label: Label = $GestureStatus

@export_range(0.0, 1.0, 0.01)
var depth_min_for_color := 0.65

@export_range(0.0, 1.0, 0.01)
var depth_max_for_color := 1.0

@export var depth_color_min := Color8(180, 250, 255)
@export var depth_color_max := Color8(110, 20, 220)

@export var mirror_depth := true

var _gesture_recognizer: MediaPipeGestureRecognizer
var _recognition_pending := false
var _last_timestamp_ms := 0
var _rgb_frame_size := Vector2i(640, 480)

func normalize_depth(depth: float) -> float:
	var normalized := inverse_lerp(
		depth_min_for_color,
		depth_max_for_color,
		depth
	)
	
	return clamp(normalized, 0.0, 1.0)

func get_depth_color(normalized_depth: float) -> Color:
	var t = clamp(normalized_depth, 0.0, 1.0)
	
	return depth_color_min.lerp(
		depth_color_max,
		t
	)

func _ready() -> void:
	depth_camera.rgb_frame_ready.connect(_on_rgb_frame)
	depth_camera.depth_frame_ready.connect(_on_depth_frame)

	depth_camera.min_depth = 0.5
	depth_camera.max_depth = 3.0
	depth_camera.auto_reconnect = true

	hand_overlay.visible = show_hand_detection
	status_label.visible = show_hand_detection
	gesture_status_label.visible = false

	if _initialize_gesture_recognizer():
		_set_status("Kinect & MediaPipe Initialized.")
	else:
		_set_status("Kinect started (MediaPipe failed).")

	depth_camera.start_streaming()

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

	_apply_gesture_result.call_deferred(hands, _get_pointing_up_score(result))

func _get_pointing_up_score(result: MediaPipeGestureRecognizerResult) -> float:
	var best_score := 0.0
	for gesture in result.gestures:
		for category in gesture.categories:
			if category.category_name == "Pointing_Up":
				best_score = maxf(best_score, category.score)
	return best_score

func _apply_gesture_result(
	hands: Array[PackedVector2Array],
	pointing_up_score: float,
) -> void:
	_recognition_pending = false

	if show_hand_detection:
		hand_overlay.show_hands(hands, _rgb_frame_size)
		gesture_status_label.visible = pointing_up_score >= pointing_up_confidence
		if gesture_status_label.visible:
			gesture_status_label.text = "Pointing Up detected (%.0f%%)" % (pointing_up_score * 100.0)
	else:
		gesture_status_label.visible = false

#func _on_depth_frame(image: ImageTexture):
	#$DepthTexture.texture = image
	#
	#var w = image.get_width()
	#var h = image.get_height()
	#
	#var img: Image = Image.create(image.get_width(), image.get_height(), false, Image.FORMAT_RGB8)
	#
	#var tmp = image.get_image()
	#
	#var threshold = 0.65
	#
	## 2. Iterate through every pixel
	#for y in range(h):
		#for x in range(w):
			#var input_color: Color = tmp.get_pixel(x, y)
	#
			## Check your condition (e.g., Red channel value)
			#if input_color.r < threshold:
				#img.set_pixel(w - x, y, Color(0, 0, 0, 1))
			#else: 
				#var normalized = inverse_lerp(0.65, 1.0, input_color.r)
				#var output_color = get_depth_color(
					#Color8(180, 250, 255),
					#Color8(110, 20, 220),
					#normalized
				#)
				#img.set_pixel(w - x, y, output_color)
	#
	#$OutTexture.texture = ImageTexture.create_from_image(img)

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
				var normalized_depth = normalize_depth(raw_depth)
				depth_mask.set_pixel(width - 1 - x, y, Color(normalized_depth, 0, 0, 1))

	$Particles.SetDepthImageMask(depth_mask)

func _set_status(message: String) -> void:
	if show_hand_detection:
		status_label.text = message


func _show_error(message: String) -> void:
	_set_status(message)
	push_error(message)
