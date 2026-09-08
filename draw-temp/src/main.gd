extends Control

@export_file("*.task") var model_path := "res://assets/models/gesture_recognizer.task"
@export_range(1, 8, 1) var max_hands := 4
@export_range(0.0, 1.0, 0.05) var pointing_up_confidence := 0.6

@onready var _frame_source: FrameSource = $FrameSource
@onready var _video: TextureRect = $Video
@onready var _overlay: HandOverlay = $HandOverlay
@onready var _status: Label = $Status
@onready var _gesture_status: Label = $GestureStatus

var _gesture_recognizer: MediaPipeGestureRecognizer
var _recognition_pending := false
var _last_timestamp_ms := 0


func _ready() -> void:
	_frame_source.frame_available.connect(_on_frame_available)
	_frame_source.frame_size_changed.connect(_on_frame_size_changed)
	_frame_source.status_changed.connect(_on_source_status_changed)
	_video.texture = _frame_source.get_output_texture()

	if _initialize_gesture_recognizer():
		_frame_source.start()


func _exit_tree() -> void:
	_frame_source.stop()
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


func _on_frame_available() -> void:
	if _recognition_pending or _gesture_recognizer == null:
		return

	_recognition_pending = true
	await RenderingServer.frame_post_draw

	if not is_instance_valid(_frame_source):
		_recognition_pending = false
		return

	var image := _frame_source.get_frame_image()
	if image == null or image.is_empty():
		_recognition_pending = false
		return

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
	_overlay.show_hands(hands, _frame_source.get_frame_size())
	_gesture_status.visible = pointing_up_score >= pointing_up_confidence
	if _gesture_status.visible:
		_gesture_status.text = "Pointing Up detected (%.0f%%)" % (pointing_up_score * 100.0)


func _on_frame_size_changed(frame_size: Vector2i) -> void:
	_overlay.show_hands([], frame_size)


func _on_source_status_changed(message: String) -> void:
	_status.text = message


func _show_error(message: String) -> void:
	_status.text = message
	push_error(message)
