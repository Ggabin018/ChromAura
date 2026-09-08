extends Control

@export_file("*.task") var model_path := "res://assets/models/gesture_recognizer.task"
@export_range(1, 8, 1) var max_hands := 4
@export_range(0.0, 1.0, 0.05) var pointing_up_confidence := 0.6
@export var depth_threshold := 0.65

@onready var depth_camera: DepthCameraNode = $DepthCameraNode
@onready var rgb_texture: TextureRect = $RGBTexture
@onready var depth_texture: TextureRect = $DepthTexture
@onready var out_texture: TextureRect = $OutTexture
@onready var hand_overlay: HandOverlay = $HandOverlay
@onready var status_label: Label = $Status
@onready var gesture_status_label: Label = $GestureStatus

var _gesture_recognizer: MediaPipeGestureRecognizer
var _recognition_pending := false
var _last_timestamp_ms := 0


func _ready() -> void:
	# Connect Kinect depth camera signals
	depth_camera.rgb_frame_ready.connect(_on_rgb_frame)
	depth_camera.depth_frame_ready.connect(_on_depth_frame)

	# Configure Kinect settings
	depth_camera.min_depth = 0.5   # meters
	depth_camera.max_depth = 3.0   # meters
	depth_camera.auto_reconnect = true

	# Initialize MediaPipe gesture recognizer for hand detection
	if _initialize_gesture_recognizer():
		_set_status("Kinect & MediaPipe Initialized.")
	else:
		_set_status("Kinect started (MediaPipe failed).")

	# Start Kinect stream
	depth_camera.start_streaming()
	print("Kinect Camera is READY")


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
	if image_texture == null:
		return

	rgb_texture.texture = image_texture

	if _recognition_pending or _gesture_recognizer == null:
		return

	_recognition_pending = true

	var image := image_texture.get_image()
	if image == null or image.is_empty():
		_recognition_pending = false
		return

	if image.get_format() != Image.FORMAT_RGB8:
		image.convert(Image.FORMAT_RGB8)

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

	var pointing_up_score := _get_pointing_up_score(result)
	_apply_gesture_result.call_deferred(hands, pointing_up_score)


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

	if hand_overlay:
		var frame_size := Vector2i(640, 480)
		if rgb_texture.texture:
			frame_size = Vector2i(rgb_texture.texture.get_width(), rgb_texture.texture.get_height())
		elif out_texture.texture:
			frame_size = Vector2i(out_texture.texture.get_width(), out_texture.texture.get_height())
		hand_overlay.show_hands(hands, frame_size)

	if gesture_status_label:
		gesture_status_label.visible = pointing_up_score >= pointing_up_confidence
		if gesture_status_label.visible:
			gesture_status_label.text = "Pointing Up detected (%.0f%%)" % (pointing_up_score * 100.0)


func _on_depth_frame(image_texture: ImageTexture) -> void:
	if image_texture == null:
		return

	depth_texture.texture = image_texture

	var tmp := image_texture.get_image()
	if tmp == null or tmp.is_empty():
		return

	var img := Image.create(image_texture.get_width(), image_texture.get_height(), false, Image.FORMAT_RGB8)

	for y in range(img.get_height()):
		for x in range(img.get_width()):
			var pixel_color: Color = tmp.get_pixel(x, y)
			if pixel_color.r < depth_threshold:
				img.set_pixel(x, y, Color(0, 0, 0, 1))
			else:
				img.set_pixel(x, y, Color.from_hsv((0.1 + (pixel_color.r * 8.0) / depth_threshold), 1.0, 1.0))

	out_texture.texture = ImageTexture.create_from_image(img)


func _set_status(message: String) -> void:
	if status_label:
		status_label.text = message


func _show_error(message: String) -> void:
	_set_status(message)
	push_error(message)
